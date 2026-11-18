using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.RegularExpressions;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Collection;

public sealed partial class CollectionService(
    MesDbContext db,
    FactoryMES.Backend.Features.Alarms.AlarmService alarms,
    AutoProductionService auto,
    ILogger<CollectionService> log
)
{
    private void Check(
        [System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid,
        string message,
        [CallerMemberName] string operation = ""
    )
    {
        log.LogDebug(
            "Collection validation in {Operation}: passed {Passed}",
            operation,
            valid
        );
        if (!valid)
        {
            log.LogWarning("Collection validation rejected in {Operation}", operation);
            throw new OrderFault(
                422,
                "CollectionValidation",
                message
            );
        }
    }

    private void Conflict(
        bool conflict,
        string message,
        [CallerMemberName] string operation = ""
    )
    {
        log.LogDebug(
            "Collection conflict check in {Operation}: conflict {Conflict}",
            operation,
            conflict
        );
        if (conflict)
        {
            log.LogWarning("Collection conflict rejected in {Operation}", operation);
            throw new OrderFault(
                409,
                "ConfigurationConflict",
                message
            );
        }
    }

    private static string Id(long id) => OrderService.Id(id);
    private static long Parse(string? id) => OrderService.ParseId(id);

    private async Task<IntegrationTarget> TargetAsync(string code, bool locked = false)
    {
        // 연결 코드를 검증해 원본 대상을 찾고 변경·수집 요청은 설비 다음 연결 순서로 잠근다.
        log.LogDebug("Collection TargetAsync entered");

        Check(code is not null && Regex.IsMatch(code, "^[A-Za-z][A-Za-z0-9_.-]{0,63}$"),
            "연결 ID 형식을 확인해 주세요.");
        if (locked)
        {
            // AUTO 명령과 수집이 같은 설비 경계에서 직렬화되도록 Target보다 Equipment를 먼저 잡는다.
            var equipment = await db.Set<IntegrationTarget>().Where(x => x.TargetCode == code)
                .Select(x => x.EquipmentId).SingleOrDefaultAsync();
            if (equipment is { } equipmentId)
                await AutoProductionService.LockEquipmentAsync(db, equipmentId);
        }
        log.LogDebug(
            "Collection target lookup Target={TargetId} Locked={Locked}",
            code,
            locked
        );
        var rows = locked
            ? await db.Set<IntegrationTarget>().FromSqlInterpolated(
                $"SELECT * FROM INTEGRATION_TARGET WHERE TARGET_CODE = {code} FOR UPDATE")
                    .ToListAsync()
            : await db.Set<IntegrationTarget>().Where(x => x.TargetCode == code).ToListAsync();
        return rows.SingleOrDefault(x => x.Kind == "EQUIPMENT"
            && (x.ConnectorType == "equipment-http" || x.ConnectorType == ModbusFormat.Connector)
            && !x.Deleted)
            ?? throw new OrderFault(
                404,
                "TargetNotFound",
                "설비 연결을 찾을 수 없습니다."
            );
    }

    public async Task<EquipmentResolvedConfig> ConfigurationAsync(string code)
    {
        // 내부 게이트웨이의 설정 조회를 받아 잠금 안에서 원본 설정과 해시를 일관되게 계산한다.
        log.LogDebug("Collection ConfigurationAsync entered");

        await using var tx = await db.Database.BeginTransactionAsync();
        var result = await CompileAsync(await TargetAsync(code, true));
        await tx.CommitAsync();
        return result;
    }

    private async Task<EquipmentResolvedConfig> CompileAsync(IntegrationTarget target)
    {
        // Oracle 연결·태그 매핑을 읽어 커넥터 설정과 해시를 만든다. URL·자격증명·전체 설정은 로그에 남기지 않는다.
        log.LogDebug("Collection CompileAsync entered");

        var connection = await db.Set<EquipmentConnection>()
            .SingleAsync(x => x.TargetId == target.TargetId);
        var mappings = await db.Set<EquipmentTagMapping>()
            .Where(x => x.TargetId == target.TargetId).OrderBy(x => x.MappingId).ToListAsync();
        var settings = new EquipmentSettings(
            connection.Protocol,
            connection.ApprovedEndpoint,
            connection.PollIntervalMs,
            connection.TimeoutMs,
            connection.StaleSec,
            null,
            mappings.Select(x => new MappingInput(
                Id(x.MappingId),
                x.MetricCode,
                x.SourcePath,
                x.DataType,
                x.ValueKind,
                x.Unit,
                CollectionFormat.Number(x.Scale),
                CollectionFormat.Number(x.OffsetValue),
                x.Enabled
            )).ToArray()
        );
        var content = new EquipmentConfigContent(
            target.TargetCode,
            "EQUIPMENT",
            Id(target.EquipmentId!.Value),
            target.ConnectorType,
            target.ConfigVersion,
            target.Enabled && !target.Deleted,
            settings
        );
        return new EquipmentResolvedConfig(content, CollectionFormat.Hash(content));
    }

    private async Task<TargetView> ViewAsync(IntegrationTarget target)
    {
        // 업무 엔터티와 하위 이력을 조회해 화면용 DTO를 만든다. 메모·사유 본문은 로그로 복제하지 않는다.
        log.LogDebug("Collection ViewAsync entered");

        var runtime = await db.Set<ConnectorRuntime>().AsNoTracking()
            .Where(x => x.TargetId == target.TargetId)
            .OrderByDescending(x => x.LastHeartbeatAtUtc).FirstOrDefaultAsync();
        return new TargetView(
            target.TargetCode,
            target.Kind,
            target.DisplayName,
            Id(target.EquipmentId!.Value),
            target.Enabled,
            target.Deleted,
            target.ConfigVersion,
            target.Version,
            runtime?.AppliedConfigVersion ?? 0,
            runtime?.ConnectionState,
            runtime?.LastHeartbeatAtUtc,
            await CompileAsync(target)
        );
    }

    public async Task<TargetView> GetAsync(string code) => await ViewAsync(await TargetAsync(code));

    public async Task<PageResult<TargetView>> ListAsync(int? limit, string? cursor)
    {
        // 화면의 조회 요청을 받아 접근 가능한 단건을 조회하고 표시용 응답으로 변환한다.
        log.LogDebug("Collection GetAsync entered");

        var size = limit ?? 50;
        if (size is < 1 or > 100) throw new OrderFault(
            400,
            "InvalidLimit",
            "목록은1~100개입니다."
        );
        var after = cursor is null ? 0 : Parse(cursor);
        var rows = await db.Set<IntegrationTarget>()
            .Where(x => x.Kind == "EQUIPMENT"
                && (x.ConnectorType == "equipment-http" || x.ConnectorType == ModbusFormat.Connector)
                && x.Deleted == EF.Parameter(false)
                && x.TargetId > after)
            .OrderBy(x => x.TargetId).Take(size + 1).ToListAsync();
        var items = new List<TargetView>();
        foreach (var row in rows.Take(size)) items.Add(await ViewAsync(row));
        return new PageResult<TargetView>(items.ToArray(),
            rows.Count > size ? Id(rows[size - 1].TargetId) : null);
    }

    private void ValidateSettings(EquipmentSettings settings)
    {
        Check(settings is not null
            && ((settings.Protocol == "HTTP" && CollectionFormat.MockEndpoint(settings.ApprovedEndpoint))
                || (settings.Protocol == ModbusFormat.Protocol
                    && ModbusFormat.TryEndpoint(settings.ApprovedEndpoint, out _, out _))),
            "승인된 로컬 HTTP 또는 Modbus TCP 설비 주소를 입력하세요.");
        Check(settings!.CredentialRef is null, "현재 로컬 수집에는 자격증명을 등록하지 않습니다.");
        Check(settings.PollIntervalMs is >= 1000 and <= 60000
            && settings.TimeoutMs is >= 100 and <= 10000
            && settings.TimeoutMs <= settings.PollIntervalMs
            && settings.StaleSec is >= 1 and <= 3600, "수집주기·제한시간을 확인해 주세요.");
        Check(settings.Mappings is { Length: >= 1 and <= 50 }, "매핑은1~50개입니다.");
        Check(settings.Mappings!.All(x => x is not null)
            && settings.Mappings.Select(x => x.MetricCode).Distinct().Count() == settings.Mappings
                .Length,
            "중복·빈 매핑이 있습니다.");
        foreach (var map in settings.Mappings)
        {
            Check(map.MetricCode is not null && Regex.IsMatch(map.MetricCode,
                "^[A-Za-z][A-Za-z0-9_.-]{0,39}$"),
                "지표 코드는 영문으로 시작하는40자 이내입니다.");
            Check(map.Unit is null || map.Unit.Length <= 20, "단위는20자 이내입니다.");
            Check(CollectionFormat.TryDecimal(map.Scale, out _)
                && CollectionFormat.TryDecimal(map.Offset, out _), "변환 수치는 소수6자리 이내입니다.");
            if (settings.Protocol == ModbusFormat.Protocol)
            {
                Check(ModbusFormat.ValidMapping(map),
                    "Modbus 주소·자료형·순서를 확인하세요. COUNTER는 배율1·오프셋0이어야 합니다.");
                continue;
            }
            Check((map.SourcePath == "totalCount" && map.DataType == "INTEGER" && map
                .ValueKind == "COUNTER")
                || (map.SourcePath == "temperature" && map.DataType == "DECIMAL" && map
                    .ValueKind == "NUMBER")
                || (map.SourcePath == "operatingState" && map.DataType == "STRING" && map
                    .ValueKind == "STATE"),
                "Mock 매핑은 totalCount/temperature/operatingState 형식만 지원합니다.");
        }
        Check(settings.Mappings.Any(x => x.Enabled), "활성 매핑이 하나 이상 필요합니다.");
        if (settings.Protocol == ModbusFormat.Protocol)
        {
            Check(ModbusFormat.ValidSpan(settings.Mappings),
                "활성 Modbus 태그는 하나의 125 레지스터 이내 읽기 범위여야 합니다.");
        }
    }

    private async Task ReferencesAsync(long equipmentId, EquipmentSettings settings)
    {
        // 설정 저장 전에 설비·라인 활성과 HTTP 설비 코드 또는 Modbus unit의 설비 소유를 확인한다.
        log.LogDebug("Collection ReferencesAsync entered");

        var equipment = await db.Set<Equipment>().SingleOrDefaultAsync(x => x
            .EquipmentId == equipmentId);
        Check(equipment is not null && equipment.Active
            && await db.Set<Line>().AnyAsync(x => x.LineId == equipment.LineId
                && x.Active == EF.Parameter(true)), "활성 라인·설비가 필요합니다.");
        if (settings.Protocol == "HTTP")
        {
            Check(new Uri(settings.ApprovedEndpoint).AbsolutePath == "/equipment/" + equipment!.Code,
                "Mock URL의 설비 코드는 선택한 설비 코드와 같아야 합니다.");
        }
        else
        {
            // 같은 물리 unit을 다른 설비로 이중 등록해 AUTO 수량을 복제하지 않는다.
            var alreadyBound = await (
                from c in db.Set<EquipmentConnection>()
                join t in db.Set<IntegrationTarget>() on c.TargetId equals t.TargetId
                where c.Protocol == ModbusFormat.Protocol
                    && c.ApprovedEndpoint == settings.ApprovedEndpoint
                    && t.EquipmentId != equipmentId && t.Deleted == EF.Parameter(false)
                select c.TargetId
            ).AnyAsync();
            Conflict(alreadyBound, "같은 Modbus unit은 다른 설비에 이미 연결되어 있습니다.");
        }
    }

    private async Task ApplyMappingsAsync(long targetId, MappingInput[] inputs)
    {
        // 관리자 매핑 입력을 현재 정의와 대조해 갱신하고 삭제 대신 비활성화하여 과거 측정의 식별자를 보존한다.
        log.LogDebug("Collection ApplyMappingsAsync entered");

        var current = await db.Set<EquipmentTagMapping>().Where(x => x.TargetId == targetId)
            .ToListAsync();
        log.LogDebug(
            "Mapping update: target {TargetId}, current {Current}, requested {Requested}",
            targetId,
            current.Count,
            inputs.Length
        );
        var retained = new HashSet<long>();
        foreach (var item in inputs)
        {
            var map = item.MappingId is null
                ? current.SingleOrDefault(x => x.MetricCode == item.MetricCode)
                : current.SingleOrDefault(x => x.MappingId == Parse(item.MappingId));
            Check(item.MappingId is null || map is not null, "다른 연결의 매핑 ID입니다.");
            if (map is null)
            {
                map = new EquipmentTagMapping {
                    TargetId = targetId,
                    MetricCode = item.MetricCode
                };
                db.Add(map);
            }
            else
            {
                Check(map.MetricCode == item.MetricCode, "기존 매핑의 지표 코드는 바꿀 수 없습니다.");
                retained.Add(map.MappingId);
                // An old latest value must not be interpreted using a new conversion definition.
                if (map.SourcePath != item.SourcePath || map.Scale != decimal.Parse(item.Scale,
                    CultureInfo.InvariantCulture)
                    || map.OffsetValue != decimal.Parse(item.Offset, CultureInfo.InvariantCulture))
                {
                    log.LogDebug("Mapping {MappingId} latest value cleared after conversion change", map.MappingId);
                    map.LatestValue = null;
                    map.LatestSampleAtUtc = null;
                    map.LatestReceivedAtUtc = null;
                }
            }
            map.SourcePath = item.SourcePath;
            map.DataType = item.DataType;
            map.ValueKind = item.ValueKind;
            map.Unit = item.Unit;
            map.Scale = decimal.Parse(item.Scale, CultureInfo.InvariantCulture);
            map.OffsetValue = decimal.Parse(item.Offset, CultureInfo.InvariantCulture);
            map.Enabled = item.Enabled;
        }
        // 과거 측정의 매핑 참조를 보존하기 위해 누락 매핑은 삭제하지 않고 비활성화한다.
        foreach (var old in current.Where(x => !retained.Contains(x.MappingId))) old
            .Enabled = false;
        Check(current.Count + inputs.Count(x => x.MappingId is null
            && !current.Any(m => m.MetricCode == x.MetricCode)) <= 50, "누적 매핑은50개 이내입니다.");
    }

    private async Task RecordChangeAsync(
        IntegrationTarget target,
        ClaimsPrincipal actor,
        string reason
    )
    {
        // 설정 원본 변경에서 Outbox와 감사 이력을 함께 준비한다. 최종 커밋은 등록·수정 호출자가 수행한다.
        log.LogDebug(
            "Collection RecordChangeAsync entered by actor {ActorId}",
            OrderService.Actor(actor)
        );

        var config = await CompileAsync(target);
        var now = CollectionFormat.UtcNow();
        var changed = new ConfigChanged(
            Guid.NewGuid().ToString(),
            "mes.integration.config.changed.v1",
            1,
            "mes.backend",
            now,
            Guid.NewGuid().ToString("N"),
            new ConfigChangeData(
                target.TargetCode,
                target.ConfigVersion,
                config
            )
        );
        db.Add(new OutboxEvent
        {
            EventId = changed.EventId,
            Type = changed.Type,
            AggregateType = "INTEGRATION_TARGET",
            AggregateId = target.TargetCode,
            AggregateVersion = target.ConfigVersion,
            Payload = CollectionFormat.Canonical(changed),
            Status = "PENDING"
        });
        db.Add(new AuditEvent
        {
            ActorId = OrderService.Actor(actor),
            Principal = "USER:" + Id(OrderService.Actor(actor)),
            Action = "equipment.configuration.changed",
            EntityType = "INTEGRATION_TARGET",
            EntityId = target.TargetCode,
            Detail = CollectionFormat.Canonical(new {
                reason,
                configuration = config
            }),
            OccurredAtUtc = now
        });
        await db.SaveChangesAsync();
    }

    public async Task<TargetView> CreateAsync(EquipmentTargetCreate input, ClaimsPrincipal actor)
    {
        // 관리자 화면의 등록 입력을 받아 참조·형식을 검증하고 업무 데이터와 감사 이력을 함께 저장한다.
        log.LogDebug("Collection CreateAsync entered by actor {ActorId}", OrderService
            .Actor(actor));

        Check(input.Kind == "EQUIPMENT", "설비 연결 종류가 필요합니다.");
        Check(input.TargetId is not null && Regex.IsMatch(input.TargetId,
            "^[A-Za-z][A-Za-z0-9_.-]{0,63}$"),
            "연결ID를 확인해 주세요.");
        Check(!string.IsNullOrWhiteSpace(input.DisplayName) && input.DisplayName.Length <= 100,
            "표시명은1~100자입니다.");
        ValidateSettings(input.Settings);
        Check(input.ConnectorType == (input.Settings.Protocol == "HTTP"
            ? "equipment-http" : ModbusFormat.Connector), "프로토콜과 커넥터 종류가 다릅니다.");
        Check(input.Settings.Mappings.All(x => x.MappingId is null), "신규 매핑에 ID를 지정하지 않습니다.");
        var equipmentId = Parse(input.EquipmentId);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE PRODUCT IN SHARE ROW EXCLUSIVE MODE");
        await ReferencesAsync(equipmentId, input.Settings);
        var target = new IntegrationTarget
        {
            TargetCode = input.TargetId,
            Kind = "EQUIPMENT",
            DisplayName = input.DisplayName,
            ConnectorType = input.ConnectorType,
            EquipmentId = equipmentId,
            Enabled = input.Enabled,
            ConfigVersion = 1,
            ActivatedAtUtc = input.Enabled ? CollectionFormat.UtcNow() : null
        };
        db.Add(target);
        await db.SaveChangesAsync();
        db.Add(new EquipmentConnection
        {
            TargetId = target.TargetId,
            Kind = "EQUIPMENT",
            Protocol = input.Settings.Protocol,
            ApprovedEndpoint = input.Settings.ApprovedEndpoint,
            PollIntervalMs = input.Settings.PollIntervalMs,
            TimeoutMs = input.Settings.TimeoutMs,
            StaleSec = input.Settings.StaleSec
        });
        await ApplyMappingsAsync(target.TargetId, input.Settings.Mappings);
        await db.SaveChangesAsync();
        await RecordChangeAsync(
            target,
            actor,
            "연결 등록"
        );
        var result = await ViewAsync(target);
        await tx.CommitAsync();
        log.LogInformation(
            "CreateAsync committed: target {TargetId}, actor {ActorId}, config version {ConfigVersion}",
            target.TargetId,
            OrderService.Actor(actor),
            target.ConfigVersion
        );
        return result;
    }

    private async Task ProtectHighMappingsAsync(long targetId, MappingInput[] inputs)
    {
        // 설정 변경 전에 활성 HIGH 규칙과 미복구 경보가 사용하는 매핑 정의가 유지되는지 검사한다.
        log.LogDebug("Collection ProtectHighMappingsAsync entered");

        var rules = await db.Set<AlarmRule>().Where(x => x.TargetId == targetId && x.Kind == "HIGH"
            && (x.Enabled == EF.Parameter(true) || db.Set<Alarm>().Any(a => a.RuleId == x.RuleId
                && a.Status == "ACTIVE"))).ToListAsync();
        foreach (var id in rules.Select(x => x.MappingId!.Value).Distinct())
        {
            var old = await db.Set<EquipmentTagMapping>().SingleAsync(x => x.MappingId == id);
            var matches = inputs.Where(x => x.MappingId == Id(id)
                || (x.MappingId is null && x.MetricCode == old.MetricCode)).ToArray();
            Check(matches.Length <= 1, "보호된 매핑을 여러 입력으로 변경할 수 없습니다.");
            var next = matches.SingleOrDefault();
            Conflict(next is null || !next.Enabled || next.SourcePath != old.SourcePath
                || next.DataType != old.DataType || next.ValueKind != old.ValueKind || next
                    .Unit != old.Unit
                || decimal.Parse(next.Scale, CultureInfo.InvariantCulture) != old.Scale
                || decimal.Parse(next.Offset, CultureInfo.InvariantCulture) != old.OffsetValue,
                "사용 중인 HIGH 규칙/미복구 경보의 수치 매핑은 변경하거나 중지할 수 없습니다.");
        }
    }

    public async Task<TargetView> UpdateAsync(
        string code,
        TargetConfigUpdate input,
        ClaimsPrincipal actor
    )
    {
        // 관리자 설정 변경을 받아 버전·AUTO 작업·HIGH 매핑 보호를 검증하고 설정 버전과 Outbox를 함께 저장한다.
        log.LogDebug("Collection UpdateAsync entered by actor {ActorId}", OrderService
            .Actor(actor));

        ValidateSettings(input.Settings);
        Check(!string.IsNullOrWhiteSpace(input.DisplayName) && input.DisplayName.Length <= 100,
            "표시명은1~100자입니다.");
        Check(
            !string.IsNullOrWhiteSpace(input.Reason) && input.Reason.Length <= 1000,
            "변경 사유가 필요합니다."
        );
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE PRODUCT IN SHARE ROW EXCLUSIVE MODE");
        var target = await TargetAsync(code, true);
        Conflict(target.Version != input.ExpectedVersion || target.ConfigVersion != input
            .ExpectedConfigVersion,
            "설정이 변경되었습니다. 최신 값을 다시 조회해 주세요.");
        Conflict(await db.Set<WorkOrder>().AnyAsync(x => x.EquipmentId == target.EquipmentId
            && x.CountMode == "AUTO" && (x.Status == "RUNNING" || x.Status == "PAUSED")),
            "AUTO 작업 중 설정을 바꿀 수 없습니다.");
        await ReferencesAsync(target.EquipmentId!.Value, input.Settings);
        await ProtectHighMappingsAsync(target.TargetId, input.Settings.Mappings);
        target.ConnectorType = input.Settings.Protocol == "HTTP"
            ? "equipment-http" : ModbusFormat.Connector;
        target.DisplayName = input.DisplayName;
        target.Enabled = input.Enabled;
        target.ConfigVersion++;
        if (input.Enabled) target.ActivatedAtUtc = CollectionFormat.UtcNow();
        var connection = await db.Set<EquipmentConnection>().SingleAsync(x => x.TargetId == target
            .TargetId);
        connection.Protocol = input.Settings.Protocol;
        connection.ApprovedEndpoint = input.Settings.ApprovedEndpoint;
        connection.PollIntervalMs = input.Settings.PollIntervalMs;
        connection.TimeoutMs = input.Settings.TimeoutMs;
        connection.StaleSec = input.Settings.StaleSec;
        await ApplyMappingsAsync(target.TargetId, input.Settings.Mappings);
        await db.SaveChangesAsync();
        await RecordChangeAsync(
            target,
            actor,
            input.Reason
        );
        var result = await ViewAsync(target);
        await tx.CommitAsync();
        log.LogInformation(
            "UpdateAsync committed: target {TargetId}, actor {ActorId}, config version {ConfigVersion}",
            target.TargetId,
            OrderService.Actor(actor),
            target.ConfigVersion
        );
        return result;
    }
}




