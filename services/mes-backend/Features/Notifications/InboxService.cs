using System.Security.Claims;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Notifications;

// 업무 알림함 경계. 원인 이벤트별 중복 생성 방지와 사용자별 수신·읽음 처리를 제공한다.
public sealed class InboxService(
    MesDbContext db,
    ILogger<InboxService>? logger = null
)
{
    private readonly ILogger<InboxService> log = logger
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<InboxService>.Instance;

    // Called inside the originating business transaction. No external delivery happens here.
    // 업무 트랜잭션 안에서 이벤트 중복을 확인하고 알림·수신자·Outbox를 준비한다. 최종 커밋은 호출자가 맡는다.
    public async Task CreateAsync(
        string causeEventId,
        string title,
        string body,
        long? alarmId,
        long[] recipients,
        string correlationId
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "CreateAsync",
                "입력 처리 시작"
            );
            if (recipients.Length == 0)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "CreateAsync",
                    "분기 일치 [recipients.Length == 0]"
                );
                return;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "CreateAsync",
                    "분기 불일치 [recipients.Length == 0]"
                );
            }

            if (await db.Set<Notification>().AnyAsync(x => x.EventId == causeEventId))
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "CreateAsync",
                    "분기 일치 [await db.Set<Notification>().AnyAsync(x => x.EventId == causeEve...]"
                );
                return;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "CreateAsync",
                    "분기 불일치 [await db.Set<Notification>().AnyAsync(x => x.EventId == causeEve...]"
                );
            }

            var notification = new Notification
            {
                EventId = causeEventId,
                Title = title,
                Body = body,
                AlarmId = alarmId
            };
            db.Add(notification);
            await db.SaveChangesAsync();
            foreach (var userId in recipients.Distinct())
            {
                db.Add(new NotificationRecipient
                {
                    NotificationId = notification.NotificationId,
                    UserId = userId
                });
            }
            foreach (var chunk in recipients.Distinct().Chunk(100))
            {
                var eventId = Guid.NewGuid().ToString();
                var now = CollectionFormat.UtcNow();
                db.Add(new OutboxEvent
                {
                    EventId = eventId,
                    Type = "mes.notification.created.v1",
                    AggregateType = "NOTIFICATION",
                    AggregateId = OrderService.Id(notification.NotificationId),
                    AggregateVersion = 1,
                    Status = "PENDING",
                    Payload = CollectionFormat.Canonical(new
                    {
                        eventId,
                        type = "mes.notification.created.v1",
                        schemaVersion = 1,
                        source = "mes.backend",
                        occurredAt = now,
                        correlationId,
                        causationId = causeEventId,
                        data = new
                        {
                            notificationId = OrderService.Id(notification.NotificationId),
                            title,
                            body,
                            recipientIds = chunk.Select(OrderService.Id).ToArray(),
                            alarmId = alarmId is null ? null : OrderService.Id(alarmId.Value),
                            orderId = (string?)null,
                            analysisResultId = (string?)null
                        }
                    })
                });
            }
            await db.SaveChangesAsync();
            log.LogDebug(
                "알림·수신자·Outbox 저장 준비 완료: {NotificationId}, {RecipientCount}, {CorrelationId}",
                notification.NotificationId,
                recipients.Distinct().Count(),
                correlationId
            );
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "CreateAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "CreateAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    public async Task<PageResult<NotificationView>> ListAsync(
        ClaimsPrincipal user,
        bool? unreadOnly,
        int? limit,
        string? cursor
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ListAsync",
                "입력 처리 시작"
            );
            var actor = OrderService.Actor(user);
            var size = limit ?? 50;
            if (size is < 1 or > 100)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ListAsync",
                    "분기 일치 [size is < 1 or > 100]"
                );
                throw new OrderFault(
                    400,
                    "InvalidLimit",
                    "목록은1~100개입니다."
                );
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ListAsync",
                    "분기 불일치 [size is < 1 or > 100]"
                );
            }

            var before = cursor is null ? long.MaxValue : OrderService.ParseId(cursor);
            var query = from recipient in db.Set<NotificationRecipient>().AsNoTracking()
                        join notification in db.Set<Notification>().AsNoTracking()
                            on recipient.NotificationId equals notification.NotificationId
                        where recipient.UserId == actor && notification.NotificationId < before
                            && (unreadOnly != true || recipient.ReadAtUtc == null)
                        select new
                        {
                            notification,
                            recipient.ReadAtUtc
                        };
            var rows = await query.OrderByDescending(x => x.notification.NotificationId)
                .Take(size + 1).ToListAsync();
            return new PageResult<NotificationView>(rows.Take(size)
                .Select(x => new NotificationView(
                    OrderService.Id(x.notification.NotificationId),
                    x.notification.EventId,
                    x.notification.Title,
                    x.notification.Body,
                    x.notification.AlarmId is null ? null : OrderService.Id(x.notification.AlarmId
                        .Value),
                    x.notification.OrderId is null ? null : OrderService.Id(x.notification.OrderId
                        .Value),
                    x.notification.AnalysisResultId is null ? null : OrderService.Id(x.notification
                        .AnalysisResultId.Value),
                    x.notification.CreatedAtUtc,
                    x.ReadAtUtc
                )).ToArray(), rows.Count > size ? OrderService.Id(rows[size - 1].notification
                .NotificationId) : null);
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

    // Cookie 사용자에게 속한 수신 행만 조건부 갱신해 최초 읽음 시각을 보존한다.
    public async Task ReadAsync(string notificationId, ClaimsPrincipal user)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ReadAsync",
                "입력 처리 시작"
            );
            var actor = OrderService.Actor(user);
            var id = OrderService.ParseId(notificationId);
            var now = CollectionFormat.UtcNow();
            // Atomic conditional update: concurrent callers cannot replace the first read timestamp.
            var updated = await db.Set<NotificationRecipient>()
                .Where(x => x.NotificationId == id && x.UserId == actor && x.ReadAtUtc == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ReadAtUtc, now));
            if (!await db.Set<NotificationRecipient>().AnyAsync(x => x.NotificationId == id && x
                .UserId == actor))
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ReadAsync",
                    "분기 일치 [!await db.Set<NotificationRecipient>().AnyAsync(x => x.Notificat...]"
                );
                throw new OrderFault(
                    404,
                    "NotificationNotFound",
                    "알림을 찾을 수 없습니다."
                );
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ReadAsync",
                    "분기 불일치 [!await db.Set<NotificationRecipient>().AnyAsync(x => x.Notificat...]"
                );
                if (updated > 0)
                {
                    log.LogInformation(
                        "알림 읽음 처리 완료: {ActorUserId}, {NotificationId}",
                        actor,
                        id
                    );
                }
                else
                {
                    log.LogDebug(
                        "이미 읽은 알림 재요청: {ActorUserId}, {NotificationId}",
                        actor,
                        id
                    );
                }
            }
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "ReadAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "ReadAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }
}





