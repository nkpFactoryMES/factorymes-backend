using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Orders;

// 관리자 기준정보 경계. 품목·라인·설비·사용자 변경에 관계·버전 검증과 감사 이력을 적용한다.
public sealed class MasterDataService(
    MesDbContext db,
    IPasswordHasher<UserAccount> hasher,
    ILogger<MasterDataService>? logger = null
)
{
    private readonly ILogger<MasterDataService> log = logger
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MasterDataService>.Instance;

    private static void Check(bool valid, string message)
    {
        if (!valid)
        {
            throw new OrderFault(
                422,
                "BusinessValidation",
                message
            );
        }
    }

    private static void Conflict(bool conflict, string message)
    {
        if (conflict)
        {
            throw new OrderFault(
                409,
                "ReferenceConflict",
                message
            );
        }
    }

    private static void Version(long current, long expected)
    {
        if (current != expected || expected < 1)
        {
            throw new OrderFault(
                409,
                "VersionConflict",
                "다시 조회하여 변경 내용을 확인해 주세요."
            );
        }
    }

    private static void Names(string? code, string? name)
    {
        Check(!string.IsNullOrWhiteSpace(code) && code.Length <= 40,
            "코드는 1~40자입니다.");
        Check(!string.IsNullOrWhiteSpace(name) && name.Length <= 100,
            "이름은 1~100자입니다.");
    }

    // 기준정보 변경끼리 Oracle 잠금으로 직렬화해 사용 중 관계와 코드 중복 검증 사이의 경쟁을 제한한다.
    private async Task LockAsync()
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "LockAsync",
            "입력 처리 시작"
        );
        // Same lock as order reference validation. No work-order locks are acquired here.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE PRODUCT IN SHARE ROW EXCLUSIVE MODE");
    }

    private IQueryable<WorkOrder> OpenOrders => db.Set<WorkOrder>()
        .Where(x => x.Status != "CLOSED" && x.Status != "CANCELLED");

    // 검증된 변경의 행위자·대상·상태를 감사 엔터티로 준비한다. 이 단계는 아직 커밋 완료가 아니다.
    private void Audit(
        ClaimsPrincipal actor,
        string type,
        long id
    )
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "Audit",
            "입력 처리 시작"
        );
        log.LogDebug(
            "기준정보 변경 검증 완료·감사 기록 준비: {ActorUserId}, {EntityType}, {EntityId}",
            OrderService.Actor(actor),
            type,
            id
        );
        db.Add(new AuditEvent
        {
            ActorId = OrderService.Actor(actor),
            Principal = "USER:" + OrderService.Id(OrderService.Actor(actor)),
            Action = "master.saved",
            EntityType = type,
            EntityId = OrderService.Id(id),
            Detail = JsonSerializer.Serialize(new {
                operation = "saved"
            }),
            OccurredAtUtc = DateTime.UtcNow
        });
    }

    public static ProductChoice View(Product x) => new(
        OrderService.Id(x.ProductId),
        x.Code,
        x.Name,
        x.Unit,
        x.Active,
        x.Version
    );

    public static LineChoice View(Line x) => new(
        OrderService.Id(x.LineId),
        x.Code,
        x.Name,
        x.Active,
        x.Version
    );

    public static EquipmentChoice View(Equipment x) => new(
        OrderService.Id(x.EquipmentId),
        x.Code,
        x.Name,
        OrderService.Id(x.LineId),
        x.Active,
        x.Version
    );

    private async Task<UserChoice> ViewAsync(UserAccount x) => new(
        OrderService.Id(x.UserId),
        x.Login,
        x.Name,
        await db.Set<UserRole>()
            .Where(r => r.UserId == x.UserId)
            .OrderBy(r => r.Role)
            .Select(r => r.Role)
            .ToArrayAsync(),
        x.Email,
        x.Active,
        x.Version
    );

    // 품목 화면이 선택한 식별자로 기준정보를 조회한다. 비밀번호·해시·보안 스탬프는 반환하지 않는다.
    public async Task<ProductChoice> GetProductAsync(string id)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "GetProductAsync",
                "입력 처리 시작"
            );
            var key = OrderService.ParseId(id);
            var row = await db.Set<Product>().SingleOrDefaultAsync(x => x.ProductId == key)
                ?? throw new OrderFault(
                    404,
                    "NotFound",
                    "기준정보를 찾을 수 없습니다."
                );
            return View(row);
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "GetProductAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "GetProductAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 라인 화면이 선택한 식별자로 기준정보를 조회한다. 비밀번호·해시·보안 스탬프는 반환하지 않는다.
    public async Task<LineChoice> GetLineAsync(string id)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "GetLineAsync",
                "입력 처리 시작"
            );
            var key = OrderService.ParseId(id);
            var row = await db.Set<Line>().SingleOrDefaultAsync(x => x.LineId == key)
                ?? throw new OrderFault(
                    404,
                    "NotFound",
                    "기준정보를 찾을 수 없습니다."
                );
            return View(row);
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "GetLineAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "GetLineAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 설비 화면이 선택한 식별자로 기준정보를 조회한다. 비밀번호·해시·보안 스탬프는 반환하지 않는다.
    public async Task<EquipmentChoice> GetEquipmentAsync(string id)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "GetEquipmentAsync",
                "입력 처리 시작"
            );
            var key = OrderService.ParseId(id);
            var row = await db.Set<Equipment>().SingleOrDefaultAsync(x => x.EquipmentId == key)
                ?? throw new OrderFault(
                    404,
                    "NotFound",
                    "기준정보를 찾을 수 없습니다."
                );
            return View(row);
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "GetEquipmentAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "GetEquipmentAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 사용자 화면이 선택한 식별자로 기준정보를 조회한다. 비밀번호·해시·보안 스탬프는 반환하지 않는다.
    public async Task<UserChoice> GetUserAsync(string id)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "GetUserAsync",
                "입력 처리 시작"
            );
            var key = OrderService.ParseId(id);
            var row = await db.Set<UserAccount>().SingleOrDefaultAsync(x => x.UserId == key)
                ?? throw new OrderFault(
                    404,
                    "NotFound",
                    "기준정보를 찾을 수 없습니다."
                );
            return await ViewAsync(row);
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "GetUserAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "GetUserAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 품목 등록 요청을 받아 관계·중복을 검증하고 새 기준정보와 감사 기록을 함께 저장한다.
    public async Task<ProductChoice> CreateProductAsync(
        ProductCreate input,
        ClaimsPrincipal actor
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "CreateProductAsync",
                "입력 처리 시작"
            );
            Names(input.Code, input.Name);
            await using var tx = await db.Database.BeginTransactionAsync();
            await LockAsync();
            Check(input.Unit == "EA", "품목 단위는 EA입니다.");
            var row = new Product
            {
                Code = input.Code,
                Name = input.Name,
                Unit = input.Unit,
                Active = input.Active
            };
            db.Add(row);
            await db.SaveChangesAsync();
            Audit(
                actor,
                "PRODUCT",
                row.ProductId
            );
            await db.SaveChangesAsync();
            var result = View(row);
            await tx.CommitAsync();
            log.LogInformation(
                "기준정보 변경 커밋 완료: {Operation}, {ActorUserId}, {EntityId}, {Version}",
                "CreateProductAsync",
                OrderService.Actor(actor),
                row.ProductId,
                result.Version
            );
            return result;
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "CreateProductAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "CreateProductAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 품목 수정 요청의 현재 버전과 사용 중 관계를 확인하고 기준정보와 감사 기록을 함께 저장한다.
    public async Task<ProductChoice> UpdateProductAsync(
        string id,
        ProductUpdate input,
        ClaimsPrincipal actor
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "UpdateProductAsync",
                "입력 처리 시작"
            );
            Names(input.Code, input.Name);
            await using var tx = await db.Database.BeginTransactionAsync();
            await LockAsync();
            Check(input.Unit == "EA", "품목 단위는 EA입니다.");
            var key = OrderService.ParseId(id);
            var row = (await db.Set<Product>()
                .FromSqlInterpolated($"SELECT * FROM PRODUCT WHERE PRODUCT_ID = {key} FOR UPDATE")
                .ToListAsync()).SingleOrDefault()
                ?? throw new OrderFault(
                    404,
                    "NotFound",
                    "기준정보를 찾을 수 없습니다."
                );
            Version(row.Version, input.ExpectedVersion);
            var history = await db.Set<WorkOrder>().AnyAsync(x => x.ProductId == row.ProductId)
                || await db.Set<DailyProduction>().AnyAsync(x => x.ProductId == row.ProductId);
            Conflict(history && row.Code != input.Code, "이력이 있는 품목의 코드는 바꿀 수 없습니다.");
            Conflict(!input.Active && await OpenOrders.AnyAsync(x => x.ProductId == row.ProductId),
                "미완료 작업에서 사용 중인 품목입니다.");
            row.Code = input.Code;
            row.Name = input.Name;
            row.Unit = input.Unit;
            row.Active = input.Active;
            db.Entry(row).Property(x => x.UpdatedAtUtc).IsModified = true;
            await db.SaveChangesAsync();
            Audit(
                actor,
                "PRODUCT",
                row.ProductId
            );
            await db.SaveChangesAsync();
            var result = View(row);
            await tx.CommitAsync();
            log.LogInformation(
                "기준정보 변경 커밋 완료: {Operation}, {ActorUserId}, {EntityId}, {Version}",
                "UpdateProductAsync",
                OrderService.Actor(actor),
                row.ProductId,
                result.Version
            );
            return result;
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "UpdateProductAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "UpdateProductAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 라인 등록 요청을 받아 관계·중복을 검증하고 새 기준정보와 감사 기록을 함께 저장한다.
    public async Task<LineChoice> CreateLineAsync(
        LineCreate input,
        ClaimsPrincipal actor
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "CreateLineAsync",
                "입력 처리 시작"
            );
            Names(input.Code, input.Name);
            await using var tx = await db.Database.BeginTransactionAsync();
            await LockAsync();
            var row = new Line
            {
                Code = input.Code,
                Name = input.Name,
                Active = input.Active
            };
            db.Add(row);
            await db.SaveChangesAsync();
            Audit(
                actor,
                "LINE",
                row.LineId
            );
            await db.SaveChangesAsync();
            var result = View(row);
            await tx.CommitAsync();
            log.LogInformation(
                "기준정보 변경 커밋 완료: {Operation}, {ActorUserId}, {EntityId}, {Version}",
                "CreateLineAsync",
                OrderService.Actor(actor),
                row.LineId,
                result.Version
            );
            return result;
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "CreateLineAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "CreateLineAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 라인 수정 요청의 현재 버전과 사용 중 관계를 확인하고 기준정보와 감사 기록을 함께 저장한다.
    public async Task<LineChoice> UpdateLineAsync(
        string id,
        LineUpdate input,
        ClaimsPrincipal actor
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "UpdateLineAsync",
                "입력 처리 시작"
            );
            Names(input.Code, input.Name);
            await using var tx = await db.Database.BeginTransactionAsync();
            await LockAsync();
            var key = OrderService.ParseId(id);
            var row = (await db.Set<Line>()
                .FromSqlInterpolated($"SELECT * FROM LINE WHERE LINE_ID = {key} FOR UPDATE")
                .ToListAsync()).SingleOrDefault()
                ?? throw new OrderFault(
                    404,
                    "NotFound",
                    "기준정보를 찾을 수 없습니다."
                );
            Version(row.Version, input.ExpectedVersion);
            Conflict(row.Code != input.Code
                && await db.Set<Equipment>().AnyAsync(x => x.LineId == row.LineId),
                "설비가 연결된 라인의 코드는 바꿀 수 없습니다.");
            Conflict(!input.Active && await db.Set<Equipment>().AnyAsync(
                x => x.LineId == row.LineId && x.Active == EF.Parameter(true)),
                "활성 설비가 있는 라인은 비활성화할 수 없습니다.");
            Conflict(!input.Active && await OpenOrders.AnyAsync(
                x => db.Set<Equipment>().Any(e => e.EquipmentId == x.EquipmentId
                    && e.LineId == row.LineId)), "미완료 작업에서 사용 중인 라인입니다.");
            row.Code = input.Code;
            row.Name = input.Name;
            row.Active = input.Active;
            db.Entry(row).Property(x => x.UpdatedAtUtc).IsModified = true;
            await db.SaveChangesAsync();
            Audit(
                actor,
                "LINE",
                row.LineId
            );
            await db.SaveChangesAsync();
            var result = View(row);
            await tx.CommitAsync();
            log.LogInformation(
                "기준정보 변경 커밋 완료: {Operation}, {ActorUserId}, {EntityId}, {Version}",
                "UpdateLineAsync",
                OrderService.Actor(actor),
                row.LineId,
                result.Version
            );
            return result;
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "UpdateLineAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "UpdateLineAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 설비 등록 요청을 받아 관계·중복을 검증하고 새 기준정보와 감사 기록을 함께 저장한다.
    public async Task<EquipmentChoice> CreateEquipmentAsync(
        EquipmentCreate input,
        ClaimsPrincipal actor
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "CreateEquipmentAsync",
                "입력 처리 시작"
            );
            Names(input.Code, input.Name);
            await using var tx = await db.Database.BeginTransactionAsync();
            await LockAsync();
            var lineId = OrderService.ParseId(input.LineId);
            Check(await db.Set<Line>().AnyAsync(x => x.LineId == lineId
                && (!input.Active || x.Active == EF.Parameter(true))), "라인 상태를 확인해 주세요.");
            var row = new Equipment
            {
                Code = input.Code,
                Name = input.Name,
                LineId = lineId,
                Active = input.Active
            };
            db.Add(row);
            await db.SaveChangesAsync();
            Audit(
                actor,
                "EQUIPMENT",
                row.EquipmentId
            );
            await db.SaveChangesAsync();
            var result = View(row);
            await tx.CommitAsync();
            log.LogInformation(
                "기준정보 변경 커밋 완료: {Operation}, {ActorUserId}, {EntityId}, {Version}",
                "CreateEquipmentAsync",
                OrderService.Actor(actor),
                row.EquipmentId,
                result.Version
            );
            return result;
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "CreateEquipmentAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "CreateEquipmentAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 설비 수정 요청의 현재 버전과 사용 중 관계를 확인하고 기준정보와 감사 기록을 함께 저장한다.
    public async Task<EquipmentChoice> UpdateEquipmentAsync(
        string id,
        EquipmentUpdate input,
        ClaimsPrincipal actor
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "UpdateEquipmentAsync",
                "입력 처리 시작"
            );
            Names(input.Code, input.Name);
            await using var tx = await db.Database.BeginTransactionAsync();
            await LockAsync();
            var lineId = OrderService.ParseId(input.LineId);
            Check(await db.Set<Line>().AnyAsync(x => x.LineId == lineId
                && (!input.Active || x.Active == EF.Parameter(true))), "라인 상태를 확인해 주세요.");
            var key = OrderService.ParseId(id);
            var row = (await db.Set<Equipment>()
                .FromSqlInterpolated($"SELECT * FROM EQUIPMENT WHERE EQUIPMENT_ID = {key} FOR UPDATE")
                .ToListAsync()).SingleOrDefault()
                ?? throw new OrderFault(
                    404,
                    "NotFound",
                    "기준정보를 찾을 수 없습니다."
                );
            Version(row.Version, input.ExpectedVersion);
            var history = await db.Set<WorkOrder>().AnyAsync(x => x.EquipmentId == row.EquipmentId)
                || await db.Set<IntegrationTarget>().AnyAsync(x => x.EquipmentId == row.EquipmentId)
                || await db.Set<TelemetryEvent>().AnyAsync(x => x.EquipmentId == row.EquipmentId)
                || await db.Set<Alarm>().AnyAsync(x => x.EquipmentId == row.EquipmentId)
                || await db.Set<DailyProduction>().AnyAsync(x => x.EquipmentId == row.EquipmentId);
            Conflict(history && (row.Code != input.Code || row.LineId != lineId),
                "이력이 있는 설비의 코드·라인은 바꿀 수 없습니다.");
            Conflict(!input.Active && await OpenOrders.AnyAsync(x => x.EquipmentId == row
                .EquipmentId),
                "미완료 작업에서 사용 중인 설비입니다.");
            Conflict(!input.Active && await db.Set<IntegrationTarget>().AnyAsync(
                x => x.EquipmentId == row.EquipmentId && x.Enabled == EF.Parameter(true)
                    && x.Deleted == EF.Parameter(false)), "활성 연결이 있는 설비입니다.");
            Conflict(!input.Active && await db.Set<Alarm>().AnyAsync(
                x => x.EquipmentId == row.EquipmentId && x.ClosedAtUtc == null),
                "미해결 경보가 있는 설비입니다.");
            row.Code = input.Code;
            row.Name = input.Name;
            row.LineId = lineId;
            row.Active = input.Active;
            db.Entry(row).Property(x => x.UpdatedAtUtc).IsModified = true;
            await db.SaveChangesAsync();
            Audit(
                actor,
                "EQUIPMENT",
                row.EquipmentId
            );
            await db.SaveChangesAsync();
            var result = View(row);
            await tx.CommitAsync();
            log.LogInformation(
                "기준정보 변경 커밋 완료: {Operation}, {ActorUserId}, {EntityId}, {Version}",
                "UpdateEquipmentAsync",
                OrderService.Actor(actor),
                row.EquipmentId,
                result.Version
            );
            return result;
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "UpdateEquipmentAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "UpdateEquipmentAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    private static void UserFields(string? name, string? email)
    {
        Check(!string.IsNullOrWhiteSpace(name) && name.Length <= 100,
            "이름은 1~100자입니다.");
        Check(email is null || (email.Length <= 254
            && MailAddress.TryCreate(email, out var parsed) && parsed.Address == email),
            "이메일 주소를 확인해 주세요.");
    }

    // 사용자 등록 요청을 받아 관계·중복을 검증하고 새 기준정보와 감사 기록을 함께 저장한다.
    public async Task<UserChoice> CreateUserAsync(UserCreate input, ClaimsPrincipal actor)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "CreateUserAsync",
                "입력 처리 시작"
            );
            UserFields(input.Name, input.Email);
            Check(!string.IsNullOrWhiteSpace(input.Login) && input.Login.Length <= 100,
                "로그인 ID는 1~100자입니다.");
            Check(input.Role is "ADMIN" or "WORKER", "역할을 확인해 주세요.");
            Check(input.InitialPassword is { Length: >= 12 and <= 256 },
                "초기 암호는 12~256자입니다.");
            await using var tx = await db.Database.BeginTransactionAsync();
            await LockAsync();
            var row = new UserAccount
            {
                Login = input.Login.Trim(),
                NormalizedLogin = input.Login.Trim().ToUpperInvariant(),
                Name = input.Name,
                Email = input.Email,
                Active = true,
                SecurityStamp = Guid.NewGuid().ToString("N")
            };
            row.PasswordHash = hasher.HashPassword(row, input.InitialPassword);
            db.Add(row);
            await db.SaveChangesAsync();
            db.Add(new UserRole
            {
                UserId = row.UserId,
                Role = input.Role
            });
            Audit(
                actor,
                "USER_ACCOUNT",
                row.UserId
            );
            await db.SaveChangesAsync();
            var result = await ViewAsync(row);
            await tx.CommitAsync();
            log.LogInformation(
                "기준정보 변경 커밋 완료: {Operation}, {ActorUserId}, {EntityId}, {Version}",
                "CreateUserAsync",
                OrderService.Actor(actor),
                row.UserId,
                result.Version
            );
            return result;
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "CreateUserAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "CreateUserAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 사용자 수정 요청의 현재 버전과 사용 중 관계를 확인하고 기준정보와 감사 기록을 함께 저장한다.
    public async Task<UserChoice> UpdateUserAsync(
        string id,
        UserUpdate input,
        ClaimsPrincipal actor
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "UpdateUserAsync",
                "입력 처리 시작"
            );
            UserFields(input.Name, input.Email);
            Check(input.Roles is { Length: >= 1 and <= 2 }
                && input.Roles.All(r => r is "ADMIN" or "WORKER")
                && input.Roles.Distinct().Count() == input.Roles.Length, "역할을 확인해 주세요.");
            var key = OrderService.ParseId(id);
            await using var tx = await db.Database.BeginTransactionAsync();
            await LockAsync();
            var row = (await db.Set<UserAccount>()
                .FromSqlInterpolated($"SELECT * FROM USER_ACCOUNT WHERE USER_ID = {key} FOR UPDATE")
                .ToListAsync()).SingleOrDefault()
                ?? throw new OrderFault(
                    404,
                    "NotFound",
                    "사용자를 찾을 수 없습니다."
                );
            Version(row.Version, input.ExpectedVersion);
            var roles = await db.Set<UserRole>().Where(x => x.UserId == key).ToListAsync();
            var changed = !roles.Select(x => x.Role).Order().SequenceEqual(input.Roles.Order());
            if (row.Active && roles.Any(x => x.Role == "ADMIN")
                && (!input.Active || !input.Roles.Contains("ADMIN")))
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "UpdateUserAsync",
                    "분기 일치 [row.Active && roles.Any(x => x.Role == \"ADMIN\") && (!input.Activ...]"
                );
                var another = await db.Set<UserAccount>().AnyAsync(
                    x => x.UserId != key && x.Active == EF.Parameter(true)
                        && db.Set<UserRole>().Any(r => r.UserId == x.UserId && r.Role == "ADMIN"));
                if (!another)
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "UpdateUserAsync",
                        "분기 일치 [!another]"
                    );
                    throw new OrderFault(
                        409,
                        "LastAdministrator",
                        "마지막 활성 관리자는 유지해야 합니다."
                    );
                }
                else
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "UpdateUserAsync",
                        "분기 불일치 [!another]"
                    );
                }
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "UpdateUserAsync",
                    "분기 불일치 [row.Active && roles.Any(x => x.Role == \"ADMIN\") && (!input.Activ...]"
                );
            }

            Conflict((!input.Active || !input.Roles.Contains("WORKER") || input.Roles
                .Contains("ADMIN"))
                        && await OpenOrders.AnyAsync(x => x.AssigneeId == key),
                        "미완료 작업에 배정된 작업자의 역할·활성 상태를 바꿀 수 없습니다.");
            if (changed)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "UpdateUserAsync",
                    "분기 일치 [changed]"
                );
                var desired = input.Roles.ToHashSet();
                db.RemoveRange(roles.Where(x => !desired.Contains(x.Role)));
                foreach (var role in desired.Except(roles.Select(x => x.Role)))
                {
                    db.Add(new UserRole
                    {
                        UserId = key,
                        Role = role
                    });
                }
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "UpdateUserAsync",
                    "분기 불일치 [changed]"
                );
            }

            if (changed || row.Active != input.Active)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "UpdateUserAsync",
                    "분기 일치 [changed || row.Active != input.Active]"
                );
                row.SecurityStamp = Guid.NewGuid().ToString("N");
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "UpdateUserAsync",
                    "분기 불일치 [changed || row.Active != input.Active]"
                );
            }

            row.Name = input.Name;
            row.Email = input.Email;
            row.Active = input.Active;
            db.Entry(row).Property(x => x.UpdatedAtUtc).IsModified = true;
            Audit(
                actor,
                "USER_ACCOUNT",
                key
            );
            await db.SaveChangesAsync();
            var result = await ViewAsync(row);
            await tx.CommitAsync();
            log.LogInformation(
                "기준정보 변경 커밋 완료: {Operation}, {ActorUserId}, {EntityId}, {Version}",
                "UpdateUserAsync",
                OrderService.Actor(actor),
                row.UserId,
                result.Version
            );
            return result;
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "UpdateUserAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "UpdateUserAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }
}




