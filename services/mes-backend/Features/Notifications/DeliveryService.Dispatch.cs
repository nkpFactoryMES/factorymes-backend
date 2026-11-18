using System.Text.Json;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Notifications;

// Outbox·수신자별 이력을 관리하고 Mock 또는 로컬 SMTP·HTTP에 위임한다. 외부 실발송은 없다.
public sealed partial class DeliveryService
{
    private static bool Due(OutboxEvent row, DateTime now) =>
        row.Status != "DELIVERED" && (row.Status == "PENDING" || row.NextAttemptAtUtc is not null)
            && (row.NextAttemptAtUtc is null || row.NextAttemptAtUtc <= now);

    // 내부 배치 요청을 받아 전달 가능한 Outbox를 선별하고 수신자별 발송 이력을 생성한다.
    public async Task<DispatchBatchResult> DispatchOutboxAsync(DispatchBatchRequest input,
        CancellationToken ct)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "DispatchOutboxAsync",
                "입력 처리 시작"
            );
            var size = Limit(input.Limit);
            await coordinator.Gate.WaitAsync(ct);
            try
            {
                var now = CollectionFormat.UtcNow();
                var ids = await db.Set<OutboxEvent>().AsNoTracking().Where(x =>
                    (x.Type == "mes.alarm.changed.v1" || x.Type == "mes.notification.created.v1")
                    && (x.Status == "PENDING" || (x.Status == "FAILED" && x
                        .NextAttemptAtUtc != null))
                    && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= now))
                    .OrderBy(x => x.OutboxId).Select(x => x.OutboxId).Take(size).ToArrayAsync(ct);
                var delivered = 0;
                var retry = 0;
                var failed = 0;
                foreach (var id in ids)
                {
                    var result = await OutboxOneAsync(id, ct);
                    if (result == "DELIVERED")
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "DispatchOutboxAsync",
                            "분기 일치 [result == \"DELIVERED\"]"
                        );
                        delivered++;
                    }
                    else
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "DispatchOutboxAsync",
                            "분기 불일치 [result == \"DELIVERED\"]"
                        );
                    }

                    if (result == "RETRY")
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "DispatchOutboxAsync",
                            "분기 일치 [result == \"RETRY\"]"
                        );
                        retry++;
                    }
                    else
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "DispatchOutboxAsync",
                            "분기 불일치 [result == \"RETRY\"]"
                        );
                    }

                    if (result == "FAILED")
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "DispatchOutboxAsync",
                            "분기 일치 [result == \"FAILED\"]"
                        );
                        failed++;
                    }
                    else
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "DispatchOutboxAsync",
                            "분기 불일치 [result == \"FAILED\"]"
                        );
                    }

                    db.ChangeTracker.Clear();
                }
                return new DispatchBatchResult(
                    ids.Length,
                    delivered,
                    retry,
                    failed
                );
            }
            finally { coordinator.Gate.Release(); }
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "DispatchOutboxAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "DispatchOutboxAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // Outbox 한 행을 잠근 뒤 계약을 검증해 발송 건을 생성하고 처리 완료 또는 재시도 상태를 저장한다.
    private async Task<string?> OutboxOneAsync(long id, CancellationToken ct)
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "OutboxOneAsync",
            "입력 처리 시작"
        );
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = (await db.Set<OutboxEvent>().FromSqlInterpolated(
            $"SELECT * FROM OUTBOX_EVENT WHERE OUTBOX_ID = {id} FOR UPDATE SKIP LOCKED")
            .ToListAsync(ct)).SingleOrDefault();
        if (row is null || !Due(row, CollectionFormat.UtcNow()))
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "OutboxOneAsync",
                "분기 일치 [row is null || !Due(row, CollectionFormat.UtcNow())]"
            );
            return null;
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "OutboxOneAsync",
                "분기 불일치 [row is null || !Due(row, CollectionFormat.UtcNow())]"
            );
        }

        try
        {
            await ConsumeAsync(row, ct);
            row.Attempts++;
            row.Status = "DELIVERED";
            row.DeliveredAtUtc = CollectionFormat.UtcNow();
            row.NextAttemptAtUtc = null;
            row.LastError = null;
            db.Add(new AuditEvent
            {
                Principal = "SERVICE:jobs-worker",
                Action = "outbox.local.accepted",
                EntityType = "OUTBOX_EVENT",
                EntityId = Id(id),
                Detail = CollectionFormat.Canonical(new
                {
                    row.EventId,
                    row.Type
                }),
                OccurredAtUtc = CollectionFormat.UtcNow()
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            log.LogInformation("Outbox 발송 이력 인계 완료: {OutboxId}", id);
            return "DELIVERED";
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(
                "Outbox 인계 처리 실패: {OutboxId}, {FailureType}",
                id,
                error.GetType().Name
            );
            await tx.RollbackAsync(ct);
            await tx.DisposeAsync();
            db.ChangeTracker.Clear();
            // Save the failure separately: never retain partially created intents.
            await using var failure = await db.Database.BeginTransactionAsync(ct);
            var saved = (await db.Set<OutboxEvent>().FromSqlInterpolated(
                $"SELECT * FROM OUTBOX_EVENT WHERE OUTBOX_ID = {id} FOR UPDATE")
                .ToListAsync(ct)).Single();
            if (saved.Status == "DELIVERED")
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "OutboxOneAsync",
                    "분기 일치 [saved.Status == \"DELIVERED\"]"
                );
                return null;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "OutboxOneAsync",
                    "분기 불일치 [saved.Status == \"DELIVERED\"]"
                );
            }

            saved.Attempts++;
            saved.Status = "FAILED";
            saved.LastError = error is InvalidOperationException { Message: "MOCK_OUTBOX_FAILURE" }
                ? "MOCK_OUTBOX_FAILURE"
                : error is JsonException or InvalidOperationException
                    ? "OUTBOX_INVALID_EVENT" : "OUTBOX_HANDLER_FAILURE";
            var attemptBase = await OutboxAttemptBaseAsync(id, ct);
            var round = saved.Attempts - attemptBase;
            saved.NextAttemptAtUtc = round < 3
                ? CollectionFormat.UtcNow().AddSeconds(5 * round) : null;
            await db.SaveChangesAsync(ct);
            await failure.CommitAsync(ct);
            return saved.NextAttemptAtUtc is null ? "FAILED" : "RETRY";
        }
    }

    // 저장된 이벤트 봉투의 유형·식별자·수신자를 검증하고 중복 없이 채널별 발송 이력을 준비한다.
    private async Task ConsumeAsync(
        OutboxEvent row,
        CancellationToken ct,
        bool validateOnly = false
    )
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "ConsumeAsync",
            "입력 처리 시작"
        );
        using var document = JsonDocument.Parse(row.Payload);
        var envelope = document.RootElement;
        if (envelope.GetProperty("eventId").GetString() != row.EventId
            || envelope.GetProperty("type").GetString() != row.Type
            || envelope.GetProperty("schemaVersion").GetInt32() != 1)
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 일치 [envelope.GetProperty(\"eventId\").GetString() != row.EventId || en...]"
            );
            throw new InvalidOperationException("Invalid envelope");
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 불일치 [envelope.GetProperty(\"eventId\").GetString() != row.EventId || en...]"
            );
        }

        var data = envelope.GetProperty("data");
        if (row.Type == "mes.alarm.changed.v1")
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 일치 [row.Type == \"mes.alarm.changed.v1\"]"
            );
            var id = Parse(data.GetProperty("alarmId").GetString());
            var version = data.GetProperty("version").GetInt64();
            if (row.AggregateType != "ALARM" || row.AggregateId != Id(id)
                || row.AggregateVersion != version || version < 1
                || !await db.Set<Alarm>().AnyAsync(x => x.AlarmId == id && x.Version >= version,
                    ct))
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ConsumeAsync",
                    "분기 일치 [row.AggregateType != \"ALARM\" || row.AggregateId != Id(id) || row...]"
                );
                throw new InvalidOperationException("Invalid alarm responsibility");
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ConsumeAsync",
                    "분기 불일치 [row.AggregateType != \"ALARM\" || row.AggregateId != Id(id) || row...]"
                );
            }

            return;
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 불일치 [row.Type == \"mes.alarm.changed.v1\"]"
            );
        }

        var notificationId = Parse(data.GetProperty("notificationId").GetString());
        if (row.AggregateType != "NOTIFICATION" || row.AggregateId != Id(notificationId))
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 일치 [row.AggregateType != \"NOTIFICATION\" || row.AggregateId != Id(not...]"
            );
            throw new InvalidOperationException("Invalid notification aggregate");
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 불일치 [row.AggregateType != \"NOTIFICATION\" || row.AggregateId != Id(not...]"
            );
        }

        var notification = (await db.Set<Notification>().FromSqlInterpolated(
                    $"SELECT * FROM NOTIFICATION WHERE NOTIFICATION_ID = {notificationId} FOR UPDATE")
                    .ToListAsync(ct)).Single();
        if (notification.Title != data.GetProperty("title").GetString()
            || notification.Body != data.GetProperty("body").GetString())
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 일치 [notification.Title != data.GetProperty(\"title\").GetString() || n...]"
            );
            throw new InvalidOperationException("Invalid notification content");
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 불일치 [notification.Title != data.GetProperty(\"title\").GetString() || n...]"
            );
        }

        var recipients = data.GetProperty("recipientIds").EnumerateArray()
                    .Select(x => Parse(x.GetString())).ToArray();
        if (recipients.Length is < 1 or > 100 || recipients.Distinct().Count() != recipients.Length
            || await db.Set<NotificationRecipient>().CountAsync(x => x
                .NotificationId == notificationId
                && recipients.Contains(x.UserId), ct) != recipients.Length)
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 일치 [recipients.Length is < 1 or > 100 || recipients.Distinct().Count...]"
            );
            throw new InvalidOperationException("Invalid recipient snapshot");
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 불일치 [recipients.Length is < 1 or > 100 || recipients.Distinct().Count...]"
            );
        }

        // 업무 알림(경보·AI): 생성 당시 채널 스냅샷만 따른다. 현재 설정은 다시 읽지 않는다.
        // validateOnly(Outbox 재처리 정합성 검사)도 같은 검증을 거치고 원장은 만들지 않는다.
        var routing = NotificationRouting.ReadSnapshot(data, recipients);
        if (routing is not null)
        {
            log.LogDebug(
                "업무 알림 채널 스냅샷 적용: {NotificationId}, {RecipientCount}, {ValidationOnly}",
                notificationId,
                routing.Length,
                validateOnly
            );
            if (!validateOnly)
            {
                await CreateRoutedIntentsAsync(
                    notificationId,
                    routing,
                    ct
                );
            }
            return;
        }

        var key = Id(notificationId);
        var localAudit = await db.Set<AuditEvent>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.EntityType == "NOTIFICATION" && x.EntityId == key
                && x.Action == "local.notification.created", ct);
        if (localAudit is not null)
        {
            var localScenario = JsonDocument.Parse(localAudit.Detail)
                .RootElement.GetProperty("scenario").GetString();
            if (!LocalDeliveryPolicy.ValidScenario(localScenario))
            {
                throw new InvalidOperationException("LOCAL_SCENARIO_REJECTED");
            }
            log.LogDebug(
                "로컬 발송 경로 확인: {NotificationId}, {ValidationOnly}",
                notificationId,
                validateOnly
            );
            if (!validateOnly)
            {
                await CreateLocalIntentsAsync(
                    notificationId,
                    recipients,
                    localScenario!,
                    ct
                );
            }
            return;
        }
        var scenarioAudit = await db.Set<AuditEvent>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.EntityType == "NOTIFICATION" && x.EntityId == key && x
                .Action == "mock.notification.created", ct);
        var scenario = scenarioAudit is null ? "SUCCESS" : JsonDocument.Parse(scenarioAudit.Detail)
            .RootElement.GetProperty("scenario").GetString()!;
        if (!MockDeliveryProvider
            .ValidScenario(scenario))
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 일치 [!MockDeliveryProvider .ValidScenario(scenario)]"
            );
            throw new InvalidOperationException("Invalid scenario");
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 불일치 [!MockDeliveryProvider .ValidScenario(scenario)]"
            );
        }

        if (validateOnly)
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 일치 [validateOnly]"
            );
            return;
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 불일치 [validateOnly]"
            );
        }

        // Explicit administrator-only local fault scenarios, never an external failure claim.
        if (scenario == "OUTBOX_ALWAYS_FAIL"
            || (scenario == "OUTBOX_FAIL_ONCE" && row.Attempts < 3))
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 일치 [scenario == \"OUTBOX_ALWAYS_FAIL\" || (scenario == \"OUTBOX_FAIL_ON...]"
            );
            throw new InvalidOperationException("MOCK_OUTBOX_FAILURE");
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 불일치 [scenario == \"OUTBOX_ALWAYS_FAIL\" || (scenario == \"OUTBOX_FAIL_ON...]"
            );
        }

        if (scenario is "OUTBOX_FAIL_ONCE" or "OUTBOX_ALWAYS_FAIL")
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 일치 [scenario is \"OUTBOX_FAIL_ONCE\" or \"OUTBOX_ALWAYS_FAIL\"]"
            );
            scenario = "SUCCESS";
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ConsumeAsync",
                "분기 불일치 [scenario is \"OUTBOX_FAIL_ONCE\" or \"OUTBOX_ALWAYS_FAIL\"]"
            );
        }

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
                        "{Operation}: {Decision}",
                        "ConsumeAsync",
                        "분기 일치 [await db.Set<Delivery>().AnyAsync(x => x.NotificationId == notif...]"
                    );
                    continue;
                }
                else
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "ConsumeAsync",
                        "분기 불일치 [await db.Set<Delivery>().AnyAsync(x => x.NotificationId == notif...]"
                    );
                }

                db.Add(new Delivery
                {
                    NotificationId = notificationId,
                    RecipientId = recipient,
                    Channel = channel,
                    DestinationKey = "mock." + channel
                                        .ToLowerInvariant() + ".user." + Id(recipient),
                    TargetRef = "mock://local/" + channel
                                        .ToLowerInvariant() + "/" + Id(recipient) + "/" + scenario,
                    Status = "PENDING"
                });
            }
        }
    }

    // 제한된 대기 범위에서 Mock과 최대2건의 로컬 SMTP·HTTP를 처리한다. 외부 실발송은 없다.
    public async Task<DispatchBatchResult> DispatchDeliveriesAsync(DispatchBatchRequest input,
        CancellationToken ct)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "DispatchDeliveriesAsync",
                "입력 처리 시작"
            );
            var size = Limit(input.Limit);
            await coordinator.Gate.WaitAsync(ct);
            try
            {
                var now = CollectionFormat.UtcNow();
                // Canonical DELIVERY has no MOCK_SENT state. Durable receipts exclude completed simulations.
                var ids = await db.Set<Delivery>().FromSqlRaw("""
                SELECT d.* FROM DELIVERY d
                WHERE d.status = 'PENDING'
                AND NOT EXISTS (
                    SELECT 1 FROM AUDIT_EVENT a
                    WHERE a.entity_type = 'DELIVERY'
                        AND a.action IN ('delivery.mock.accepted', 'delivery.local.accepted')
                    AND a.entity_id = TO_CHAR(d.delivery_id, 'FM9999999999999999999')
                )
                """)
                    .AsNoTracking().Where(x => x.NextAttemptAtUtc == null || x
                        .NextAttemptAtUtc <= now)
                    .OrderBy(x => x.DeliveryId).Select(x => x.DeliveryId).Take(size)
                        .ToArrayAsync(ct);
                var retry = 0;
                var failed = 0;
                var examined = 0;
                var localHandled = 0;
                foreach (var id in ids)
                {
                    var target = await db.Set<Delivery>().AsNoTracking()
                        .Where(x => x.DeliveryId == id)
                        .Select(x => x.TargetRef)
                        .SingleAsync(ct);
                    var local = LocalDeliveryPolicy.IsLocal(target);
                    if (local && localHandled >= 2)
                    {
                        // 로컬 통신만 다음 배치로 미루고 현재 조회 범위의 뒤쪽 Mock은 계속 처리한다.
                        log.LogDebug("로컬 통신 상한2건으로 해당 건을 미루고 뒤쪽 Mock을 확인합니다.");
                        continue;
                    }
                    examined++;
                    if (local)
                    {
                        localHandled++;
                    }
                    var result = await DeliveryOneAsync(id, ct);
                    if (result == "RETRY")
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "DispatchDeliveriesAsync",
                            "분기 일치 [result == \"RETRY\"]"
                        );
                        retry++;
                    }
                    else
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "DispatchDeliveriesAsync",
                            "분기 불일치 [result == \"RETRY\"]"
                        );
                    }

                    if (result == "FAILED")
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "DispatchDeliveriesAsync",
                            "분기 일치 [result == \"FAILED\"]"
                        );
                        failed++;
                    }
                    else
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "DispatchDeliveriesAsync",
                            "분기 불일치 [result == \"FAILED\"]"
                        );
                    }

                    db.ChangeTracker.Clear();
                }
                // 로컬 SMTP·HTTP 수신도 외부 발송 SENT 건수로 계산하지 않는다.
                return new DispatchBatchResult(
                    examined,
                    0,
                    retry,
                    failed
                );
            }
            finally { coordinator.Gate.Release(); }
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "DispatchDeliveriesAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "DispatchDeliveriesAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 행 잠금·기처리·수신자 상태를 확인하고 공급자 결과의 영수증·재시도·실패를 저장한다.
    private async Task<string?> DeliveryOneAsync(long id, CancellationToken ct)
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "DeliveryOneAsync",
            "입력 처리 시작"
        );
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = (await db.Set<Delivery>().FromSqlInterpolated(
            $"SELECT * FROM DELIVERY WHERE DELIVERY_ID = {id} FOR UPDATE SKIP LOCKED")
            .ToListAsync(ct)).SingleOrDefault();
        var now = CollectionFormat.UtcNow();
        if (row is null || row.Status != "PENDING" || row.NextAttemptAtUtc > now)
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "DeliveryOneAsync",
                "분기 일치 [row is null || row.Status != \"PENDING\" || row.NextAttemptAtUtc >...]"
            );
            return null;
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "DeliveryOneAsync",
                "분기 불일치 [row is null || row.Status != \"PENDING\" || row.NextAttemptAtUtc >...]"
            );
        }

        var key = Id(id);
        if (await db.Set<AuditEvent>().AnyAsync(x => x.EntityType == "DELIVERY" && x.EntityId == key
            && (x.Action == "delivery.mock.accepted" || x.Action == "delivery.local.accepted"), ct))
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "DeliveryOneAsync",
                "분기 일치 [await db.Set<AuditEvent>().AnyAsync(x => x.EntityType == \"DELIVE...]"
            );
            return null;
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "DeliveryOneAsync",
                "분기 불일치 [await db.Set<AuditEvent>().AnyAsync(x => x.EntityType == \"DELIVE...]"
            );
        }

        var retryAudit = await db.Set<AuditEvent>().AsNoTracking().Where(x => x
                    .EntityType == "DELIVERY"
                    && x.EntityId == key && x.Action == "delivery.retry.requested")
                    .OrderByDescending(x => x.AuditId).Select(x => x.Detail)
                        .FirstOrDefaultAsync(ct);
        var attemptBase = retryAudit is null ? 0 : JsonDocument.Parse(retryAudit).RootElement
            .GetProperty("attemptBase").GetInt64();
        row.Attempts++;
        var round = row.Attempts - attemptBase;
        var active = await db.Set<UserAccount>().AnyAsync(x => x.UserId == row.RecipientId
            && x.Active == EF.Parameter(true), ct);
        if (LocalDeliveryPolicy.IsLocal(row.TargetRef))
        {
            // 로컬 네트워크 처리는 같은 행 잠금 안에서 수행한다. 성공 영수증과 시도를 한 번에 커밋한다.
            var localResult = await ProcessLocalAsync(
                row,
                now,
                round,
                active,
                ct
            );
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            log.LogInformation(
                "로컬 발송 원장 커밋 완료: {DeliveryId}, {Attempt}, {Outcome}",
                row.DeliveryId,
                row.Attempts,
                localResult
            );
            return localResult;
        }

        var outcome = active ? provider.Outcome(row) : "INACTIVE";
        if (outcome == "ACCEPTED")
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "DeliveryOneAsync",
                "분기 일치 [outcome == \"ACCEPTED\"]"
            );
            var notification = await db.Set<Notification>().SingleAsync(x => x
                .NotificationId == row.NotificationId, ct);
            var receipt = provider.Receive(row, notification);
            row.LastError = null;
            row.NextAttemptAtUtc = null;
            Audit(
                row,
                "delivery.mock.accepted",
                receipt
            );
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "DeliveryOneAsync",
                "분기 불일치 [outcome == \"ACCEPTED\"]"
            );
            if (outcome == "RETRY" && round < 3)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "DeliveryOneAsync",
                    "분기 일치 [outcome == \"RETRY\" && round < 3]"
                );
                row.LastError = "MOCK_TRANSIENT";
                row.NextAttemptAtUtc = now.AddSeconds(5 * round);
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "DeliveryOneAsync",
                    "분기 불일치 [outcome == \"RETRY\" && round < 3]"
                );
                row.Status = "FAILED";
                row.NextAttemptAtUtc = null;
                row.LastError = outcome switch
                {
                    "INACTIVE" => "RECIPIENT_INACTIVE",
                    "REJECTED" => "MOCK_TARGET_REJECTED",
                    "FAILED" => "MOCK_MANUAL_FAILURE",
                    _ => "MOCK_RETRY_LIMIT"
                };
            }
        }

        Audit(
            row,
            "delivery.mock.attempt",
            new
            {
                attempt = row.Attempts,
                outcome,
                round
            }
        );
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (outcome == "ACCEPTED")
        {
            log.LogInformation(
                "Mock 알림 수신 확인 완료: {DeliveryId}, {Attempt}, {Channel}",
                row.DeliveryId,
                row.Attempts,
                row.Channel
            );
        }
        else
        {
            log.LogWarning(
                "Mock 알림 처리 실패 또는 재시도: {DeliveryId}, {Attempt}, {Status}, {FailureCode}",
                row.DeliveryId,
                row.Attempts,
                row.Status,
                row.LastError
            );
        }
        return row.Status == "FAILED" ? "FAILED" : outcome == "RETRY" ? "RETRY" : "ACCEPTED";
    }
}










