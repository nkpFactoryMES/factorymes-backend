using System.Security.Claims;
using System.Text.Json;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Notifications;

// Outbox와 수신자별 발송 이력을 관리하며 Mock 또는 고정 로컬 통신 공급자에 위임한다.
public sealed partial class DeliveryService(
    MesDbContext db,
    InboxService inbox,
    MockDeliveryProvider provider,
    LocalDeliveryProvider localProvider,
    DeliveryCoordinator coordinator,
    ILogger<DeliveryService>? logger = null
)
{
    private readonly ILogger<DeliveryService> log = logger
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DeliveryService>.Instance;

    private static string Id(long id) => OrderService.Id(id);
    private static long Parse(string? value) => OrderService.ParseId(value);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new OrderFault(
            422,
            "DeliveryValidation",
            message
        );
    }

    private static void Conflict(bool invalid, string message)
    {
        if (invalid) throw new OrderFault(
            409,
            "DeliveryConflict",
            message
        );
    }

    private static int Limit(int? value)
    {
        var size = value ?? 50;
        if (size is < 1 or > 100) throw new OrderFault(
            400,
            "InvalidLimit",
            "목록은1~100개입니다."
        );
        return size;
    }

    private static DeliveryView View(Delivery row) => new(
        Id(row.DeliveryId),
        Id(row.NotificationId),
        Id(row.RecipientId),
        row.Channel,
        row.DestinationKey,
        row.Status,
        row.Attempts,
        row.NextAttemptAtUtc,
        row.SentAtUtc,
        row.LastError,
        row.Version
    );

    public async Task<PageResult<DeliveryView>> ListAsync(
        int? limit,
        string? cursor,
        string? status,
        string? channel,
        string? notificationId
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ListAsync",
                "입력 처리 시작"
            );
            var size = Limit(limit);
            var before = cursor is null ? long.MaxValue : Parse(cursor);
            var query = db.Set<Delivery>().AsNoTracking().Where(x => x.DeliveryId < before);
            if (status is not null)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ListAsync",
                    "분기 일치 [status is not null]"
                );
                Check(status is "PENDING" or "SENT" or "FAILED", "발송 상태를 확인해 주세요.");
                query = query.Where(x => x.Status == status);
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ListAsync",
                    "분기 불일치 [status is not null]"
                );
            }

            if (channel is not null)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ListAsync",
                    "분기 일치 [channel is not null]"
                );
                Check(channel is "EMAIL" or "CHAT", "채널을 확인해 주세요.");
                query = query.Where(x => x.Channel == channel);
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ListAsync",
                    "분기 불일치 [channel is not null]"
                );
            }

            if (notificationId is not null)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ListAsync",
                    "분기 일치 [notificationId is not null]"
                );
                var notification = Parse(notificationId);
                query = query.Where(x => x.NotificationId == notification);
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ListAsync",
                    "분기 불일치 [notificationId is not null]"
                );
            }

            var rows = await query.OrderByDescending(x => x.DeliveryId).Take(size + 1)
                .ToArrayAsync();
            return new PageResult<DeliveryView>(rows.Take(size).Select(View).ToArray(),
                rows.Length > size ? Id(rows[size - 1].DeliveryId) : null);
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "ListAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "ListAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 관리자 화면이 지정한 발송 식별자들의 Mock 수신 상태만 조회한다.
    public async Task<DeliveryMockStatus[]> MockStatusesAsync(string? deliveryIds)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "MockStatusesAsync",
                "입력 처리 시작"
            );
            var parts = deliveryIds?.Split(',') ?? [];
            Check(parts.Length is >= 1 and <= 100, "발송 ID는1~100개입니다.");
            var ids = parts.Select(Parse).Distinct().ToArray();
            var keys = ids.Select(Id).ToArray();
            var rows = await db.Set<Delivery>().AsNoTracking().Where(x => ids.Contains(x
                .DeliveryId))
                .ToArrayAsync();
            var receipts = await db.Set<AuditEvent>().AsNoTracking().Where(x => x
                .EntityType == "DELIVERY"
                && x.Action == "delivery.mock.accepted" && keys.Contains(x.EntityId!))
                    .ToArrayAsync();
            var accepted = receipts.ToDictionary(x => x.EntityId!, x =>
                JsonSerializer.Deserialize<MockReceiptView>(x.Detail, Json)!);
            return rows.Select(row =>
            {
                accepted.TryGetValue(Id(row.DeliveryId), out var receipt);
                var outcome = receipt is not null ? "ACCEPTED" : row.Status == "FAILED" ? "FAILED"
                    : row.LastError is not null && row
                        .NextAttemptAtUtc is not null ? "RETRY_WAIT" : "WAITING";
                return new DeliveryMockStatus(
                    Id(row.DeliveryId),
                    outcome,
                    receipt?.ReceiptId,
                    receipt?.AcceptedAt
                );
            }).ToArray();
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "MockStatusesAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "MockStatusesAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 발송 이력에 연결된 Mock 수신 확인 기록을 읽어 관리자 상세 화면에 반환한다.
    public async Task<MockReceiptView> ReceiptAsync(string id)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ReceiptAsync",
                "입력 처리 시작"
            );
            var key = Id(Parse(id));
            var receipt = await db.Set<AuditEvent>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.EntityType == "DELIVERY" && x.EntityId == key && x
                    .Action == "delivery.mock.accepted")
                ?? throw new OrderFault(
                    404,
                    "ReceiptNotFound",
                    "Mock 수신 기록이 아직 없습니다."
                );
            return JsonSerializer.Deserialize<MockReceiptView>(receipt.Detail, Json)!;
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "ReceiptAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "ReceiptAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 관리자의 현재 버전 확인 후 실패 발송 건을 재대기시키고 요청자와 재시도 기준을 감사 기록에 남긴다.
    public async Task<DeliveryView> RetryAsync(
        string id,
        ReasonCommand input,
        ClaimsPrincipal user
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "RetryAsync",
                "입력 처리 시작"
            );
            Check(!string.IsNullOrWhiteSpace(input.Reason) && input.Reason.Length <= 1000,
                "재시도 사유를1~1000자로 입력해 주세요.");
            var key = Parse(id);
            await using var tx = await db.Database.BeginTransactionAsync();
            var row = (await db.Set<Delivery>().FromSqlInterpolated(
                $"SELECT * FROM DELIVERY WHERE DELIVERY_ID = {key} FOR UPDATE").ToListAsync())
                    .SingleOrDefault()
                ?? throw new OrderFault(
                    404,
                    "DeliveryNotFound",
                    "발송 이력을 찾을 수 없습니다."
                );
            Conflict(
                input.ExpectedVersion < 1 || row.Version != input.ExpectedVersion,
                "발송 이력이 변경되었습니다."
            );
            Conflict(row.Status != "FAILED", "실패한 발송만 재시도할 수 있습니다.");
            row.Status = "PENDING";
            row.NextAttemptAtUtc = CollectionFormat.UtcNow();
            row.LastError = null;
            Audit(
                row,
                "delivery.retry.requested",
                new
                {
                    attemptBase = row.Attempts,
                    reason = input.Reason.Trim()
                },
                user
            );
            await db.SaveChangesAsync();
            var result = View(row);
            await tx.CommitAsync();
            log.LogInformation(
                "발송 재시도 요청 커밋 완료: {ActorUserId}, {DeliveryId}, {Attempt}",
                OrderService.Actor(user),
                row.DeliveryId,
                row.Attempts
            );
            return result;
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "RetryAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "RetryAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 관리자가 지정한 허용 시나리오로 로컬 알림 이벤트를 생성하고 요청 중복을 방지한다.
    public async Task<(MockNotificationResult Result, bool Created)> CreateMockAsync(
        MockNotificationRequest input,
        ClaimsPrincipal user
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "CreateMockAsync",
                "입력 처리 시작"
            );
            Check(Guid.TryParseExact(
                input.ClientRequestId,
                "D",
                out _
            )
                && MockDeliveryProvider.ValidScenario(input.Scenario), "시연 요청 ID와 시나리오를 확인해 주세요.");
            var requestId = Guid.Parse(input.ClientRequestId).ToString();
            var actor = OrderService.Actor(user);
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Set<UserAccount>().FromSqlInterpolated(
                $"SELECT * FROM USER_ACCOUNT WHERE USER_ID = {actor} FOR UPDATE").ToListAsync();
            var existing = await db.Set<Notification>().SingleOrDefaultAsync(x => x
                .EventId == requestId);
            if (existing is not null)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "CreateMockAsync",
                    "분기 일치 [existing is not null]"
                );
                var id = Id(existing.NotificationId);
                var audit = await db.Set<AuditEvent>().SingleOrDefaultAsync(x => x
                    .EntityType == "NOTIFICATION"
                    && x.EntityId == id && x.Action == "mock.notification.created");
                Conflict(audit is null || audit.ActorId != actor
                    || JsonDocument.Parse(audit.Detail).RootElement.GetProperty("scenario")
                        .GetString() != input.Scenario,
                    "같은 요청 ID에 다른 시연 정보가 있습니다.");
                await tx.CommitAsync();
                return (new MockNotificationResult(id, requestId), false);
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "CreateMockAsync",
                    "분기 불일치 [existing is not null]"
                );
            }

            await inbox.CreateAsync(
                requestId,
                "Mock 시연 알림 · " + input.Scenario,
                "외부 발송 없는 EMAIL·CHAT 로컬 수신 검증입니다.",
                null,
                [
                    actor
                ],
                requestId
            );
            var created = await db.Set<Notification>().SingleAsync(x => x.EventId == requestId);
            db.Add(new AuditEvent
            {
                ActorId = actor,
                Principal = "USER:" + Id(actor),
                Action = "mock.notification.created",
                EntityType = "NOTIFICATION",
                EntityId = Id(created.NotificationId),
                Detail = CollectionFormat.Canonical(new
                {
                    scenario = input.Scenario,
                    clientRequestId = requestId
                }),
                OccurredAtUtc = CollectionFormat.UtcNow()
            });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            log.LogInformation(
                "Mock 알림 시연 등록 완료: {ActorUserId}, {NotificationId}",
                OrderService.Actor(user),
                created.NotificationId
            );
            return (new MockNotificationResult(Id(created.NotificationId), requestId), true);
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "CreateMockAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "CreateMockAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 관리자 조건·커서로 Outbox 상태와 제한된 이력 목록을 조회한다.
    public async Task<PageResult<OutboxView>> OutboxAsync(
        int? limit,
        string? cursor,
        string? status
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "OutboxAsync",
                "입력 처리 시작"
            );
            var size = Limit(limit);
            var before = cursor is null ? long.MaxValue : Parse(cursor);
            var query = db.Set<OutboxEvent>().AsNoTracking().Where(x => x.OutboxId < before);
            if (status is not null)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "OutboxAsync",
                    "분기 일치 [status is not null]"
                );
                Check(status is "PENDING" or "FAILED" or "DELIVERED", "Outbox 상태를 확인해 주세요.");
                query = query.Where(x => x.Status == status);
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "OutboxAsync",
                    "분기 불일치 [status is not null]"
                );
            }

            var rows = await query.OrderByDescending(x => x.OutboxId).Take(size + 1).ToArrayAsync();
            return new PageResult<OutboxView>(rows.Take(size).Select(x => new OutboxView(
                Id(x.OutboxId),
                x.EventId,
                x.Type,
                x.AggregateId,
                x.Status,
                x.Attempts,
                x.NextAttemptAtUtc,
                x.DeliveredAtUtc,
                x.LastError
            )).ToArray(),
                rows.Length > size ? Id(rows[size - 1].OutboxId) : null);
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "OutboxAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "OutboxAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 검증된 변경의 행위자·대상·상태를 감사 엔터티로 준비한다. 이 단계는 아직 커밋 완료가 아니다.
    private void Audit(
        Delivery row,
        string action,
        object detail,
        ClaimsPrincipal? user = null
    )
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "Audit",
            "입력 처리 시작"
        );
        log.LogDebug(
            "발송 변경·감사 기록 준비: {ActorUserId}, {DeliveryId}, {Action}, {Status}",
            user is null ? null : (long?)OrderService.Actor(user),
            row.DeliveryId,
            action,
            row.Status
        );
        db.Add(new AuditEvent
        {
            ActorId = user is null ? null : OrderService.Actor(user),
            Principal = user is null ? "SERVICE:jobs-worker" : "USER:" + Id(OrderService
                .Actor(user)),
            Action = action,
            EntityType = "DELIVERY",
            EntityId = Id(row.DeliveryId),
            Detail = CollectionFormat.Canonical(detail),
            OccurredAtUtc = CollectionFormat.UtcNow()
        });
    }
}









