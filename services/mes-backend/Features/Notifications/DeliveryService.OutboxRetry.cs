using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Notifications;

// Outbox와 수신자별 Mock 발송 이력의 상태 전이를 관리한다. 실제 전송은 이 서비스에서 수행하지 않는다.
public sealed partial class DeliveryService
{
    private sealed record OutboxRetryIntent(
        string ClientRequestId,
        string ExpectedStateHash,
        string Reason,
        long AttemptBase
    );

    private static OutboxRetryState OutboxState(OutboxEvent row)
    {
        var block = row.DeliveredAtUtc is not null || row.Status == "DELIVERED"
            ? "ALREADY_HANDED_OFF"
            : row.Type is not ("mes.alarm.changed.v1" or "mes.notification.created.v1")
                ? "UNSUPPORTED_CONSUMER"
                : row.Status != "FAILED"
                    ? "NOT_FAILED"
                    : row.NextAttemptAtUtc is not null
                        ? "AUTOMATIC_RETRY_PENDING"
                        : null;
        var payloadHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(row.Payload))
        ).ToLowerInvariant();
        var hash = CollectionFormat.Hash(new
        {
            row.OutboxId,
            row.EventId,
            row.Type,
            row.AggregateType,
            row.AggregateId,
            row.AggregateVersion,
            payloadHash,
            row.Status,
            row.Attempts,
            row.NextAttemptAtUtc,
            row.LastError,
            row.DeliveredAtUtc
        });

        return new OutboxRetryState(
            new OutboxView(
                Id(row.OutboxId),
                row.EventId,
                row.Type,
                row.AggregateId,
                row.Status,
                row.Attempts,
                row.NextAttemptAtUtc,
                row.DeliveredAtUtc,
                row.LastError
            ),
            hash,
            block is null,
            block
        );
    }

    // 관리자 재시도 확인 화면에 현재 상태·버전·실행 이력을 제공한다.
    public async Task<OutboxRetryState> OutboxRetryStateAsync(string id)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "OutboxRetryStateAsync",
                "입력 처리 시작"
            );
            var key = Parse(id);
            var row = await db.Set<OutboxEvent>()
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.OutboxId == key)
                ?? throw new OrderFault(
                    404,
                    "OutboxNotFound",
                    "인계 기록이 없습니다."
                );

            return OutboxState(row);
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "OutboxRetryStateAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "OutboxRetryStateAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 관리자가 확인한 Outbox 버전·재시도 의도를 검증하고 실패 건만 다시 대기 상태로 변경한다.
    public async Task<OutboxRetryState> RetryOutboxAsync(
        string id,
        OutboxRetryRequest input,
        ClaimsPrincipal user
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "RetryOutboxAsync",
                "입력 처리 시작"
            );
            Check(Guid.TryParseExact(
                input.ClientRequestId,
                "D",
                out _
            )
                && input.ExpectedStateHash is { Length: 64 }
                && Regex.IsMatch(input.ExpectedStateHash, "^[0-9a-f]{64}$")
                && !string.IsNullOrWhiteSpace(input.Reason)
                && input.Reason.Trim().Length <= 1000,
                "요청 ID, 조회 상태, 사유 1~1000자를 확인하세요.");
            var key = Parse(id);
            var requestId = Guid.Parse(input.ClientRequestId).ToString();
            var reason = input.Reason.Trim();
            var actor = OrderService.Actor(user);
            await coordinator.Gate.WaitAsync();
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var row = (await db.Set<OutboxEvent>().FromSqlInterpolated(
                    $"SELECT * FROM OUTBOX_EVENT WHERE OUTBOX_ID = {key} FOR UPDATE"
                ).ToListAsync()).SingleOrDefault()
                    ?? throw new OrderFault(
                        404,
                        "OutboxNotFound",
                        "인계 기록이 없습니다."
                    );
                var entityId = Id(key);
                var audits = await db.Set<AuditEvent>()
                    .AsNoTracking()
                    .Where(x => x.EntityType == "OUTBOX_EVENT" && x.EntityId == entityId
                        && x.Action == "outbox.retry.requested")
                    .ToArrayAsync();
                foreach (var audit in audits)
                {
                    var intent = JsonSerializer.Deserialize<OutboxRetryIntent>(audit.Detail, Json)!;
                    if (intent.ClientRequestId != requestId)
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "RetryOutboxAsync",
                            "분기 일치 [intent.ClientRequestId != requestId]"
                        );
                        continue;
                    }
                    else
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "RetryOutboxAsync",
                            "분기 불일치 [intent.ClientRequestId != requestId]"
                        );
                    }

                    Conflict(audit.ActorId != actor || intent.ExpectedStateHash != input
                        .ExpectedStateHash
                        || intent.Reason != reason, "같은 요청 ID의 내용이 다릅니다.");
                    return OutboxState(row);
                }

                var state = OutboxState(row);
                Conflict(state.StateHash != input.ExpectedStateHash, "인계 상태가 변경되었습니다. 다시 확인하세요.");
                Conflict(!state.CanRetry, "자동 재시도가 끝난 지원 이벤트만 재처리할 수 있습니다.");
                try
                {
                    await ConsumeAsync(
                        row,
                        CancellationToken.None,
                        validateOnly: true
                    );
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException
                    or OrderFault or KeyNotFoundException or FormatException)
                {
                    throw new OrderFault(
                        422,
                        "OutboxSourceInvalid",
                        "원본 이벤트의 정합성 오류를 먼저 해결하세요."
                    );
                }

                var retry = new OutboxRetryIntent(
                    requestId,
                    input.ExpectedStateHash,
                    reason,
                    row.Attempts
                );
                row.Status = "PENDING";
                row.NextAttemptAtUtc = CollectionFormat.UtcNow();
                row.LastError = null;
                db.Add(new AuditEvent
                {
                    ActorId = actor,
                    Principal = "USER:" + Id(actor),
                    Action = "outbox.retry.requested",
                    EntityType = "OUTBOX_EVENT",
                    EntityId = entityId,
                    Detail = JsonSerializer.Serialize(retry, Json),
                    OccurredAtUtc = CollectionFormat.UtcNow()
                });
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                log.LogInformation(
                    "Outbox 재시도 요청 커밋 완료: {ActorUserId}, {OutboxId}",
                    OrderService.Actor(user),
                    row.OutboxId
                );

                return OutboxState(row);
            }
            finally
            {
                coordinator.Gate.Release();
            }
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "RetryOutboxAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "RetryOutboxAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 감사 기록에서 현재 재시도 회차의 시작점을 찾아 시도 횟수 계산에 사용한다.
    private async Task<long> OutboxAttemptBaseAsync(long id, CancellationToken ct)
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "OutboxAttemptBaseAsync",
            "입력 처리 시작"
        );
        var key = Id(id);
        var detail = await db.Set<AuditEvent>()
            .AsNoTracking()
            .Where(x => x.EntityType == "OUTBOX_EVENT" && x.EntityId == key
                && x.Action == "outbox.retry.requested")
            .OrderByDescending(x => x.AuditId)
            .Select(x => x.Detail)
            .FirstOrDefaultAsync(ct);

        return detail is null ? 0
            : JsonSerializer.Deserialize<OutboxRetryIntent>(detail, Json)!.AttemptBase;
    }
}




