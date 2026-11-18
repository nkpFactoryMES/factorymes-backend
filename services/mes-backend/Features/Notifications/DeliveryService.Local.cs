using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Notifications;

public sealed partial class DeliveryService
{
    // 관리자가 선택한 로컬 통신 시연을 공통 알림함·Outbox에 넣는다. 주소나 외부 자격 증명은 받지 않는다.
    public async Task<(MockNotificationResult Result, bool Created)> CreateLocalAsync(
        LocalNotificationRequest input,
        ClaimsPrincipal user
    )
    {
        log.LogDebug("로컬 통신 알림 요청 검증을 시작합니다.");
        Check(Guid.TryParseExact(
            input.ClientRequestId,
            "D",
            out _
        )
            && LocalDeliveryPolicy.ValidScenario(input.Scenario),
            "요청 ID와 로컬 시나리오를 확인해 주세요.");
        var requestId = Guid.Parse(input.ClientRequestId).ToString();
        var actor = OrderService.Actor(user);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Set<UserAccount>().FromSqlInterpolated(
            $"SELECT * FROM USER_ACCOUNT WHERE USER_ID = {actor} FOR UPDATE"
        ).ToListAsync();
        var existing = await db.Set<Notification>().SingleOrDefaultAsync(x => x.EventId == requestId);
        if (existing is not null)
        {
            var id = Id(existing.NotificationId);
            var audit = await db.Set<AuditEvent>().SingleOrDefaultAsync(x =>
                x.EntityType == "NOTIFICATION" && x.EntityId == id
                    && x.Action == "local.notification.created");
            Conflict(audit is null || audit.ActorId != actor
                || JsonDocument.Parse(audit.Detail).RootElement.GetProperty("scenario")
                    .GetString() != input.Scenario,
                "같은 요청 ID에 다른 로컬 시연 정보가 있습니다.");
            await tx.CommitAsync();
            log.LogDebug("기존 로컬 알림을 재사용합니다: {NotificationId}", existing.NotificationId);
            return (new MockNotificationResult(id, requestId), false);
        }

        await inbox.CreateAsync(
            requestId,
            "로컬 통신 검증 알림 · " + input.Scenario,
            "실제 SMTP·HTTP 수신기를 사용한 로컬 검증입니다. 외부 발송은 하지 않습니다.",
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
            Action = "local.notification.created",
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
            "로컬 알림 등록 완료: {ActorId}, {NotificationId}",
            actor,
            created.NotificationId
        );
        return (new MockNotificationResult(Id(created.NotificationId), requestId), true);
    }

    // 수신 상태는 외부 SENT가 아니라 별도 영수증을 기준으로 표시한다.
    public async Task<LocalDeliveryStatus[]> LocalStatusesAsync(string? deliveryIds)
    {
        log.LogDebug("로컬 수신 상태 조회를 시작합니다.");
        var parts = deliveryIds?.Split(',') ?? [];
        Check(parts.Length is >= 1 and <= 100, "발송 ID는1~100개입니다.");
        var ids = parts.Select(Parse).Distinct().ToArray();
        var keys = ids.Select(Id).ToArray();
        var rows = await db.Set<Delivery>().AsNoTracking()
            .Where(x => ids.Contains(x.DeliveryId)).ToArrayAsync();
        var audits = await db.Set<AuditEvent>().AsNoTracking()
            .Where(x => x.EntityType == "DELIVERY" && x.Action == "delivery.local.accepted"
                && keys.Contains(x.EntityId!)).ToArrayAsync();
        var receipts = audits.ToDictionary(x => x.EntityId!, x =>
            JsonSerializer.Deserialize<LocalReceiptView>(x.Detail, Json)!);
        return rows.Where(x => LocalDeliveryPolicy.IsLocal(x.TargetRef)).Select(row =>
        {
            receipts.TryGetValue(Id(row.DeliveryId), out var receipt);
            var outcome = receipt is not null ? "ACCEPTED" : row.Status == "FAILED" ? "FAILED"
                : row.LastError is not null && row.NextAttemptAtUtc is not null
                    ? "RETRY_WAIT" : "WAITING";
            return new LocalDeliveryStatus(
                Id(row.DeliveryId),
                outcome,
                receipt?.ReceiptId,
                receipt?.AcceptedAt
            );
        }).ToArray();
    }

    // 관리자 상세 화면에 로컬 수신 영수증을 제공한다. 제목·본문은 일반 로그에 남기지 않는다.
    public async Task<LocalReceiptView> LocalReceiptAsync(string id)
    {
        var key = Id(Parse(id));
        var audit = await db.Set<AuditEvent>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.EntityType == "DELIVERY" && x.EntityId == key && x.Action == "delivery.local.accepted")
            ?? throw new OrderFault(
                404,
                "LocalReceiptNotFound",
                "로컬 수신 기록이 아직 없습니다."
            );
        return JsonSerializer.Deserialize<LocalReceiptView>(audit.Detail, Json)!;
    }

    // 검증된 Outbox 수신자 스냅샷으로 고정 로컬 대상 원장을 만든다. 기존 원장은 중복 생성하지 않는다.
    private async Task CreateLocalIntentsAsync(
        long notificationId,
        long[] recipients,
        string scenario,
        CancellationToken ct
    )
    {
        foreach (var recipient in recipients)
        {
            foreach (var channel in new[] {
                "EMAIL",
                "CHAT"
            })
            {
                if (await db.Set<Delivery>().AnyAsync(x => x.NotificationId == notificationId
                    && x.RecipientId == recipient && x.Channel == channel, ct))
                {
                    log.LogDebug(
                        "이미 존재하는 로컬 발송 원장을 유지합니다: {NotificationId}, {Channel}",
                        notificationId,
                        channel
                    );
                    continue;
                }
                db.Add(new Delivery
                {
                    NotificationId = notificationId,
                    RecipientId = recipient,
                    Channel = channel,
                    DestinationKey = LocalDeliveryPolicy.Destination(channel, Id(recipient)),
                    TargetRef = LocalDeliveryPolicy.Target(
                        channel,
                        Id(recipient),
                        scenario
                    ),
                    Status = "PENDING"
                });
            }
        }
    }

    // 업무 알림 채널 스냅샷으로 발송 원장을 만든다.
    // OFF는 원장 없음(Inbox만), MOCK은 기존 Mock 고정 대상, LOCAL은 기존 로컬 SMTP·HTTP 고정 대상이다.
    // 시나리오는 SUCCESS로 고정한다(업무 알림에 시험용 실패 시나리오를 섞지 않는다).
    // 알림·수신자·채널당 원장 1행(UQ_DLV_RECIPIENT)이므로 재소비해도 중복 생성하지 않는다.
    private async Task CreateRoutedIntentsAsync(
        long notificationId,
        NotificationRoutingSnapshot[] routing,
        CancellationToken ct
    )
    {
        foreach (var item in routing)
        {
            var recipient = Parse(item.UserId);
            foreach (var (channel, mode) in new[] {
                ("EMAIL", item.EmailMode),
                ("CHAT", item.ChatMode)
            })
            {
                if (mode == "OFF")
                {
                    continue;
                }

                if (await db.Set<Delivery>().AnyAsync(x => x.NotificationId == notificationId
                    && x.RecipientId == recipient && x.Channel == channel, ct))
                {
                    log.LogDebug(
                        "이미 존재하는 업무 발송 원장을 유지합니다: {NotificationId}, {Channel}",
                        notificationId,
                        channel
                    );
                    continue;
                }

                var lower = channel.ToLowerInvariant();
                db.Add(new Delivery
                {
                    NotificationId = notificationId,
                    RecipientId = recipient,
                    Channel = channel,
                    DestinationKey = mode == "LOCAL"
                        ? LocalDeliveryPolicy.Destination(channel, item.UserId)
                        : "mock." + lower + ".user." + item.UserId,
                    TargetRef = mode == "LOCAL"
                        ? LocalDeliveryPolicy.Target(channel, item.UserId, "SUCCESS")
                        : "mock://local/" + lower + "/" + item.UserId + "/SUCCESS",
                    Status = "PENDING"
                });
            }
        }

        log.LogDebug(
            "업무 발송 원장 준비: {NotificationId}, {RecipientCount}",
            notificationId,
            routing.Length
        );
    }

    // 행 잠금·시도 횟수는 호출자가 소유한다. 이 메서드는 제한된 로컬 통신 후 변경을 준비한다.
    private async Task<string> ProcessLocalAsync(
        Delivery row,
        DateTime now,
        long round,
        bool active,
        CancellationToken ct
    )
    {
        log.LogDebug(
            "로컬 통신 시도 시작: {DeliveryId}, {Attempt}, {Channel}",
            row.DeliveryId,
            row.Attempts,
            row.Channel
        );
        var notification = await db.Set<Notification>().SingleAsync(x =>
            x.NotificationId == row.NotificationId, ct);
        var outcome = active
            ? await localProvider.SendAsync(
                row,
                notification,
                ct
            )
            : new LocalDeliveryOutcome(
                "REJECTED",
                "RECIPIENT_INACTIVE",
                null
            );
        if (outcome.Outcome == "ACCEPTED" && ValidLocalReceipt(
            row,
            notification,
            outcome.Receipt
        ))
        {
            row.LastError = null;
            row.NextAttemptAtUtc = null;
            Audit(
                row,
                "delivery.local.accepted",
                outcome.Receipt!
            );
            log.LogInformation(
                "로컬 수신 ACK 확인: {DeliveryId}, {Attempt}, {Channel}",
                row.DeliveryId,
                row.Attempts,
                row.Channel
            );
        }
        else
        {
            var retry = outcome.Outcome == "RETRY" && round < 3;
            row.LastError = outcome.Outcome == "ACCEPTED" ? "LOCAL_RECEIPT_REJECTED"
                : outcome.ErrorCode is { } code && Regex.IsMatch(code, "^[A-Z0-9_]{1,80}$")
                    ? code : "LOCAL_PROVIDER_REJECTED";
            if (outcome.Outcome == "RETRY" && !retry)
            {
                row.LastError = "LOCAL_RETRY_LIMIT";
            }
            row.Status = retry ? "PENDING" : "FAILED";
            row.NextAttemptAtUtc = retry
                ? now.AddSeconds(outcome.RetryAfterSeconds is >= 2 and <= 30
                    ? outcome.RetryAfterSeconds.Value : 5 * round)
                : null;
            log.LogWarning(
                "로컬 통신 실패: {DeliveryId}, {Attempt}, {Code}, {Retry}",
                row.DeliveryId,
                row.Attempts,
                row.LastError,
                retry
            );
        }
        Audit(
            row,
            "delivery.local.attempt",
            new
            {
                attempt = row.Attempts,
                outcome = outcome.Outcome,
                round
            }
        );
        return row.Status == "FAILED" ? "FAILED" : outcome.Outcome == "RETRY" ? "RETRY" : "ACCEPTED";
    }

    private static bool ValidLocalReceipt(
        Delivery row,
        Notification notification,
        LocalReceiptView? receipt
    ) => receipt is not null && receipt.DeliveryId == Id(row.DeliveryId)
        && receipt.Channel == row.Channel && receipt.DestinationKey == row.DestinationKey
        && receipt.Provider == (row.Channel == "EMAIL" ? "LOCAL_SMTP" : "LOCAL_WEBHOOK")
        && receipt.Subject == notification.Title && receipt.Body == notification.Body
        && receipt.AcceptedAt.Kind == DateTimeKind.Utc
        && receipt.AcceptedAt <= DateTime.UtcNow.AddSeconds(5)
        && !string.IsNullOrWhiteSpace(receipt.ReceiptId)
        && receipt.MessageHash == CollectionFormat.Hash(new
        {
            deliveryId = Id(row.DeliveryId),
            row.Channel,
            row.DestinationKey,
            subject = notification.Title,
            body = notification.Body
        });
}


