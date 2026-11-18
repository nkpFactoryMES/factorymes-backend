using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using FactoryMES.Foundation;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace FactoryMES.Backend.Features.Collection;

public sealed partial class CollectionService
{
    public async Task<LeaseResponse> LeaseAsync(LeaseRequest input)
    {
        // 커넥터의 원본 설정 버전·해시를 확인한 뒤 짧은 수집 허가를 발급한다. 허가·키·proof는 로그에 남기지 않는다.
        log.LogDebug("Collection LeaseAsync entered");

        Check(input.InstanceId is not null && input.InstanceId.StartsWith("equipment-connector.")
            && input.InstanceId.Length <= 64, "승인된 커넥터 인스턴스가 필요합니다.");
        await using var tx = await db.Database.BeginTransactionAsync();
        var target = await TargetAsync(input.TargetId, true);
        var config = await CompileAsync(target);
        Conflict(!target.Enabled || target.Deleted || target.ConfigVersion != input.ConfigVersion
            || config.ConfigHash != input.ConfigHash, "원본 설정이 중지되었거나 변경되었습니다.");
        using var key = RSA.Create();
        key.ImportFromPem(SecretFiles.Read("/run/secrets/collection_private_key"));
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .UtcDateTime;
        var expires = now.AddSeconds(15);
        var token = new JwtSecurityToken(
            "mes-backend",
            "mes-equipment-collection",
            [
                new Claim("target_id", input.TargetId),
                new Claim("instance_id", input.InstanceId),
                new Claim(
                    "config_version",
                    input.ConfigVersion.ToString(CultureInfo.InvariantCulture),
                    ClaimValueTypes.Integer64
                ),
                new Claim("config_hash", input.ConfigHash),
                new Claim("scope", "telemetry:collect"),
                new Claim(
                    "iat",
                    new DateTimeOffset(now).ToUnixTimeSeconds().ToString(),
                    ClaimValueTypes.Integer64
                )
            ],
            now,
            expires,
            new SigningCredentials(
                new RsaSecurityKey(key)
                {
                    CryptoProviderFactory = new CryptoProviderFactory {
                        CacheSignatureProviders = false
                    }
                },
                SecurityAlgorithms.RsaSha256
            )
        );
        var result = new LeaseResponse(
            input.TargetId,
            input.InstanceId,
            input.ConfigVersion,
            input.ConfigHash,
            new JwtSecurityTokenHandler().WriteToken(token),
            now,
            expires,
            expires.AddSeconds(60)
        );
        await tx.CommitAsync();
        return result;
    }

    public async Task<TelemetryBatchAck> StoreAsync(TelemetryBatch batch, string correlationId)
    {
        // 게이트웨이 수집 배치를 받아 바인딩·서명을 검증하고 측정·AUTO 원장·경보를 같은 트랜잭션으로 저장한다.
        log.LogDebug("Collection StoreAsync entered");

        Check(batch is not null && batch.Configuration is not null && batch
            .Events is { Length: >= 1 and <= 100 },
            "수집 배치는1~100개입니다.");
        Check(batch!.CollectionAuthorization is { Length: >= 20 and <= 8192 }, "서명 수집 허가가 필요합니다.");
        log.LogDebug(
            "Telemetry batch received Events={Count} Correlation={CorrelationId}",
            batch.Events.Length,
            correlationId
        );
        var configuration = batch.Configuration;
        Check(configuration.Content is not null
            && CollectionFormat.Hash(configuration.Content) == configuration.ConfigHash,
            "설정 해시가 일치하지 않습니다.");
        var config = configuration.Content;
        var received = CollectionFormat.UtcNow();
        await using var tx = await db.Database.BeginTransactionAsync();
        var target = await TargetAsync(config.TargetId, true);
        Check(config.Kind == "EQUIPMENT"
            && (config.ConnectorType == "equipment-http" || config.ConnectorType == ModbusFormat.Connector)
            && config.EquipmentId == Id(target.EquipmentId!.Value), "설비·대상 바인딩이 다릅니다.");
        ValidateSettings(config.Settings);
        Check(batch.Events.All(x => x is not null)
            && batch.Events.Select(x => x.EventId).Distinct().Count() == batch.Events.Length,
            "배치 이벤트ID가 중복되거나 비어 있습니다.");
        using var key = RSA.Create();
        key.ImportFromPem(SecretFiles.Read("/run/secrets/collection_public_key"));
        var acknowledgements = new List<BatchAckItem>();
        foreach (var item in batch.Events)
        {
            var data = item.Data;
            Check(Guid.TryParseExact(
                item.EventId,
                "D",
                out _
            )
                && item.Type == "mes.telemetry.collected.v1" && item.SchemaVersion == 1
                && item.Source == "mes.equipment-connector" && item.OccurredAt
                    .Kind == DateTimeKind.Utc
                && item.OccurredAt.Ticks % 10 == 0
                && item.CorrelationId is { Length: >= 1 and <= 64 }, "이벤트 형식을 확인해 주세요.");
            Check(data is not null && data.TargetId == config.TargetId
                && data.EquipmentId == config.EquipmentId && data.ConfigVersion == config
                    .ConfigVersion
                && data.ConfigHash == configuration.ConfigHash
                && data.InstanceId is { Length: >= 1 and <= 64 }
                && data.StreamId is { Length: >= 1 and <= 64 }
                && long.TryParse(
                    data.SequenceNo,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var sequence
                ) && sequence >= 0, "수집 데이터 바인딩을 확인해 주세요.");
            var hash = CollectionFormat.Hash(item);
            var existing = await db.Set<TelemetryEvent>().SingleOrDefaultAsync(x => x
                .EventId == item.EventId);
            if (existing is not null)
            {
                Conflict(existing.PayloadHash != hash || existing.TargetId != target.TargetId,
                    "같은 이벤트ID의 데이터가 다릅니다.");
                log.LogDebug(
                    "Telemetry duplicate retained Event={EventId} Telemetry={TelemetryId}",
                    item.EventId,
                    existing.TelemetryId
                );
                acknowledgements.Add(new BatchAckItem(
                    item.EventId,
                    Id(existing.TelemetryId),
                    "DUPLICATE",
                    existing.ReceivedAtUtc
                ));
                continue;
            }
            // 이미 확정된 동일 이벤트는 읽기 전용으로 응답한다. 혼합 배치의 새 이벤트는 서명을 다시 검증한다.
            try
            {
                CollectionProof.Validate(
                    batch.CollectionAuthorization,
                    key,
                    new MeasurementContext(
                        data!.TargetId,
                        data.InstanceId,
                        data.ConfigVersion,
                        data.ConfigHash,
                        item.OccurredAt,
                        received
                    )
                );
            }
            catch (Exception error) when (error is SecurityTokenException or ArgumentException)
            {
                log.LogWarning(
                    "Collection authorization rejected for target {TargetId}: type {ErrorType}",
                    target.TargetId,
                    error.GetType().Name
                );
                log.LogWarning(
                    "Collection StoreAsync rejected with code {Code}",
                    "CollectionAuthorizationRejected"
                );
                throw new OrderFault(
                    403,
                    "CollectionAuthorizationRejected",
                    "수집 허가가 유효하지 않습니다."
                );
            }
            var seq = long.Parse(data!.SequenceNo, CultureInfo.InvariantCulture);
            Conflict(await db.Set<TelemetryEvent>().AnyAsync(x => x.EquipmentId == target
                .EquipmentId
                && x.StreamId == data.StreamId && x.SequenceNo == seq), "스트림 순번이 중복됩니다.");
            var enabled = config.Settings.Mappings.Where(x => x.Enabled).ToArray();
            Check(data.Metrics is { Length: >= 1 and <= 50 }
                && data.Metrics.Length == enabled.Length
                && data.Metrics.All(x => x is not null)
                && data.Metrics.Select(x => x.MetricCode).Distinct().Count() == data.Metrics.Length,
                "설정에 맞는 매핑별 측정값이 필요합니다.");
            var values = new List<(CollectedMetric Metric, MappingInput Definition,
                decimal? Number)>();
            foreach (var metric in data.Metrics!)
            {
                var mapping = enabled.SingleOrDefault(x => x.MappingId == metric.MappingId
                    && x.MetricCode == metric.MetricCode);
                Check(mapping is not null && metric.Unit == mapping.Unit
                    && metric.Quality is "GOOD" or "BAD" or "UNCERTAIN", "매핑·품질을 확인해 주세요.");
                decimal? number = null;
                log.LogDebug(
                    "Telemetry metric validated: target {TargetId}, mapping {MappingId}, quality {Quality}",
                    target.TargetId,
                    Parse(metric.MappingId),
                    metric.Quality
                );
                if (mapping!.ValueKind == "STATE")
                {
                    Check(metric.NumericValue is null && metric
                        .TextValue is "RUNNING" or "IDLE" or "FAULT" or "STOPPED",
                        "설비 상태 형식이 다릅니다.");
                }
                else
                {
                    Check(metric.TextValue is null && CollectionFormat.TryDecimal(metric
                        .NumericValue, out _),
                        "수치 측정값 형식이 다릅니다.");
                    number = decimal.Parse(metric.NumericValue!, CultureInfo.InvariantCulture);
                    Check(mapping.ValueKind != "COUNTER" || (number >= 0 && decimal
                        .Truncate(number.Value) == number),
                        "카운터는0이상 정수입니다.");
                }
                Check(await db.Set<EquipmentTagMapping>().AnyAsync(x => x
                    .MappingId == Parse(metric.MappingId)
                    && x.TargetId == target.TargetId && x.MetricCode == metric.MetricCode),
                        "등록 매핑이 아닙니다.");
                values.Add((metric, mapping, number));
            }
            var row = new TelemetryEvent
            {
                EventId = item.EventId,
                EquipmentId = target.EquipmentId.Value,
                TargetId = target.TargetId,
                StreamId = data.StreamId,
                SequenceNo = seq,
                SchemaVersion = 1,
                ConfigVersion = data.ConfigVersion,
                OccurredAtUtc = item.OccurredAt,
                ReceivedAtUtc = received,
                Payload = CollectionFormat.Canonical(item),
                PayloadHash = hash
            };
            db.Add(row);
            await db.SaveChangesAsync();
            foreach (var value in values)
            {
                db.Add(new TelemetryMetric
                {
                    TelemetryId = row.TelemetryId,
                    MappingId = Parse(value.Metric.MappingId),
                    MetricCode = value.Metric.MetricCode,
                    NumericValue = value.Number,
                    TextValue = value.Metric.TextValue,
                    Unit = value.Metric.Unit,
                    Quality = value.Metric.Quality
                });
                var map = await db.Set<EquipmentTagMapping>().SingleAsync(
                    x => x.MappingId == Parse(value.Metric.MappingId));
                if (value.Metric.Quality == "GOOD" && data.ConfigVersion == target.ConfigVersion
                    && (map.LatestSampleAtUtc is null || item.OccurredAt > map.LatestSampleAtUtc))
                {
                    log.LogDebug("Latest metric updated for mapping {MappingId}", map.MappingId);
                    map.LatestValue = value.Number;
                    map.LatestSampleAtUtc = item.OccurredAt;
                    map.LatestReceivedAtUtc = received;
                }
            }
            var connection = await db.Set<EquipmentConnection>().SingleAsync(x => x
                .TargetId == target.TargetId);
            if (values.All(x => x.Metric.Quality == "GOOD") && data.ConfigVersion == target
                .ConfigVersion
                && (connection.LastValidSampleAtUtc is null || item.OccurredAt > connection
                    .LastValidSampleAtUtc))
            {
                connection.LastValidSampleAtUtc = item.OccurredAt;
                connection.LastValidReceivedAtUtc = received;
            }
            await db.SaveChangesAsync();
            // 원시 측정 저장 뒤 AUTO 원장과 경보를 함께 확정한다. 실패하면 배치 전체가 롤백된다.
            log.LogDebug(
                "Stored telemetry {TelemetryId}; dispatching AUTO and HIGH evaluation",
                row.TelemetryId
            );
            await auto.ConsumeAsync(row);
            await alarms.EvaluateHighTargetAsync(target);
            acknowledgements.Add(new BatchAckItem(
                item.EventId,
                Id(row.TelemetryId),
                "STORED",
                received
            ));
        }
        await tx.CommitAsync();
        log.LogDebug(
            "Telemetry batch committed Target={TargetId} Events={Count} Correlation={CorrelationId}",
            config.TargetId,
            acknowledgements.Count,
            correlationId
        );
        return new TelemetryBatchAck(acknowledgements.ToArray(), correlationId);
    }

    public async Task ReportAsync(RuntimeReport input)
    {
        // 커넥터 상태 보고를 받아 최신 시각 보고만 반영한다. heartbeat는 유효 측정 신선도를 갱신하지 않는다.
        log.LogDebug("Collection ReportAsync entered");

        Check(input.InstanceId is not null && input.InstanceId.StartsWith("equipment-connector.")
            && input.InstanceId.Length <= 64
            && input.ConnectionState is "CONNECTED" or "DISCONNECTED" or "RETRYING" or "CONFIG_ERROR"
            && input.AppliedConfigVersion >= 0 && input.ReportedAt.Kind == DateTimeKind.Utc
            && input.ReportedAt <= DateTime.UtcNow.AddSeconds(2)
            && input.OperatingState is null or "RUNNING" or "IDLE" or "FAULT" or "STOPPED",
            "실행 상태 보고 형식을 확인해 주세요.");
        await using var tx = await db.Database.BeginTransactionAsync();
        var target = await TargetAsync(input.TargetId, true);
        Check(input.AppliedConfigVersion <= target.ConfigVersion, "미래 설정 버전입니다.");
        var runtime = await db.Set<ConnectorRuntime>().SingleOrDefaultAsync(
            x => x.TargetId == target.TargetId && x.InstanceId == input.InstanceId);
        if (runtime is null)
        {
            runtime = new ConnectorRuntime {
                TargetId = target.TargetId,
                InstanceId = input.InstanceId
            };
            db.Add(runtime);
        }
        log.LogDebug(
            "Runtime report resolved: target {TargetId}, state {State}, config version {ConfigVersion}",
            target.TargetId,
            input.ConnectionState,
            input.AppliedConfigVersion
        );
        if (runtime.LastHeartbeatAtUtc is null || input.ReportedAt >= runtime.LastHeartbeatAtUtc)
        {
            runtime.AppliedConfigVersion = input.AppliedConfigVersion;
            runtime.ConnectionState = input.ConnectionState;
            runtime.OperatingState = input.OperatingState;
            runtime.LastHeartbeatAtUtc = input.ReportedAt;
            // 크기가 제한된 오류 코드만 DB에 보존한다. 원문 응답·URL·헤더·자격증명은 로그에 출력하지 않는다.
            runtime.LastError = input.LastErrorCode is { Length: <= 64 } ? input
                .LastErrorCode : null;
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task<PageResult<SampleView>> SamplesAsync(
        string code,
        int? limit,
        string? cursor
    )
    {
        // 관리자 수집 이력 요청을 받아 원시 이벤트와 매핑별 측정값을 제한 건수로 조회한다.
        log.LogDebug("Collection SamplesAsync entered");

        var target = await TargetAsync(code);
        var size = limit ?? 20;
        if (size is < 1 or > 100) throw new OrderFault(
            400,
            "InvalidLimit",
            "목록은1~100개입니다."
        );
        var before = cursor is null ? long.MaxValue : Parse(cursor);
        var rows = await db.Set<TelemetryEvent>().AsNoTracking()
            .Where(x => x.TargetId == target.TargetId && x.TelemetryId < before)
            .OrderByDescending(x => x.TelemetryId).Take(size + 1).ToListAsync();
        var items = new List<SampleView>();
        foreach (var row in rows.Take(size))
        {
            var metrics = await db.Set<TelemetryMetric>().AsNoTracking()
                .Where(x => x.TelemetryId == row.TelemetryId).OrderBy(x => x.MetricCode)
                    .ToListAsync();
            items.Add(new SampleView(
                Id(row.TelemetryId),
                row.EventId,
                row.StreamId,
                Id(row.SequenceNo),
                row.ConfigVersion,
                row.OccurredAtUtc,
                row.ReceivedAtUtc,
                metrics.Select(x => new CollectedMetric(
                    x.MetricCode,
                    Id(x.MappingId),
                    x.NumericValue is null ? null : CollectionFormat.Number(x.NumericValue.Value),
                    x.TextValue,
                    x.Unit,
                    x.Quality
                )).ToArray()
            ));
        }
        return new PageResult<SampleView>(items.ToArray(),
            rows.Count > size ? Id(rows[size - 1].TelemetryId) : null);
    }
}



