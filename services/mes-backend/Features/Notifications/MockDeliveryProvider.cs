using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;

namespace FactoryMES.Backend.Features.Notifications;

// No network client or real provider is registered in this local-only implementation.
// 외부 네트워크 없이 Mock 수신·실패 시나리오를 재현하는 로컬 알림 공급자다.
public sealed class MockDeliveryProvider(ILogger<MockDeliveryProvider>? logger = null)
{
    private readonly ILogger<MockDeliveryProvider> log = logger
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MockDeliveryProvider>.Instance;

    public static bool ValidScenario(string? value) =>
        value is "SUCCESS" or "FAIL_ONCE" or "TRANSIENT_ONCE" or "ALWAYS_FAIL"
            or "OUTBOX_FAIL_ONCE" or "OUTBOX_ALWAYS_FAIL";

    // DB에 저장된 Mock 시나리오와 시도 횟수로 성공·일시 오류·영구 실패를 결정한다. 외부 호출은 없다.
    public string Outcome(Delivery delivery)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "Outcome",
                "입력 처리 시작"
            );
            var scenario = delivery.TargetRef.Split('/').LastOrDefault();
            var recipient = delivery.RecipientId.ToString(System.Globalization.CultureInfo
                .InvariantCulture);
            var expected = "mock://local/" + delivery.Channel.ToLowerInvariant() + "/"
                + recipient + "/" + scenario;
            if (!ValidScenario(scenario) || delivery.TargetRef != expected
                || delivery.DestinationKey != "mock." + delivery.Channel.ToLowerInvariant()
                    + ".user." + recipient)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "Outcome",
                    "분기 일치 [!ValidScenario(scenario) || delivery.TargetRef != expected || de...]"
                );
                return "REJECTED";
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "Outcome",
                    "분기 불일치 [!ValidScenario(scenario) || delivery.TargetRef != expected || de...]"
                );
            }

            if (scenario == "FAIL_ONCE" && delivery.Attempts == 1)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "Outcome",
                    "분기 일치 [scenario == \"FAIL_ONCE\" && delivery.Attempts == 1]"
                );
                return "FAILED";
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "Outcome",
                    "분기 불일치 [scenario == \"FAIL_ONCE\" && delivery.Attempts == 1]"
                );
            }

            if (scenario == "ALWAYS_FAIL" || (scenario == "TRANSIENT_ONCE" && delivery
                .Attempts == 1))
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "Outcome",
                    "분기 일치 [scenario == \"ALWAYS_FAIL\" || (scenario == \"TRANSIENT_ONCE\" && de...]"
                );
                return "RETRY";
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "Outcome",
                    "분기 불일치 [scenario == \"ALWAYS_FAIL\" || (scenario == \"TRANSIENT_ONCE\" && de...]"
                );
            }

            return "ACCEPTED";
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "Outcome");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "Outcome",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // Mock 발송·알림 정보로 로컬 수신 확인을 구성한다. 제목·본문·수신 주소는 로그에 남기지 않는다.
    public MockReceiptView Receive(Delivery delivery, Notification notification)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "Receive",
                "입력 처리 시작"
            );
            var hash = CollectionFormat.Hash(new
            {
                deliveryId = delivery.DeliveryId.ToString(),
                delivery.Channel,
                delivery.DestinationKey,
                subject = notification.Title,
                body = notification.Body
            });
            return new MockReceiptView(
                delivery.DeliveryId.ToString(),
                "mock:" + Guid.NewGuid(),
                "LOCAL_MOCK",
                delivery.Channel,
                delivery.DestinationKey,
                notification.Title,
                notification.Body,
                hash,
                CollectionFormat.UtcNow()
            );
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "Receive");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "Receive",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }
}




