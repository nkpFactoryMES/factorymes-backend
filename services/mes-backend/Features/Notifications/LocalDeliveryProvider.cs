using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace FactoryMES.Backend.Features.Notifications;

// 확정된 발송 원장·알림을 받아 내부 SMTP 또는 HTTP 수신기에 실제 통신한다.
// 목적지는 설정 문자열에서 만들지 않고 고정 상수만 사용하며 재시도 횟수는 상위 원장이 관리한다.
public sealed class LocalDeliveryProvider(
    IHttpClientFactory clients,
    ILogger<LocalDeliveryProvider> log
)
{
    public const string ClientName = "LocalDelivery";
    private const int MaximumAckBytes = 1024;

    public async Task<LocalDeliveryOutcome> SendAsync(
        Delivery delivery,
        Notification notification,
        CancellationToken ct
    )
    {
        var recipient = delivery.RecipientId.ToString(CultureInfo.InvariantCulture);
        if (delivery.DeliveryId < 1 || delivery.NotificationId != notification.NotificationId
            || delivery.TargetRef is null || delivery.DestinationKey is null
            || !LocalDeliveryPolicy.ValidTarget(
                delivery.Channel,
                recipient,
                delivery.DestinationKey,
                delivery.TargetRef
            )
            || string.IsNullOrWhiteSpace(notification.Title) || notification.Title.Length > 200
            || notification.Title.Any(char.IsControl)
            || notification.Body is null || notification.Body.Length > 4000)
        {
            log.LogWarning("Local delivery target or content rejected: {DeliveryId}", delivery.DeliveryId);
            return Rejected("LOCAL_TARGET_REJECTED");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(LocalDeliveryPolicy.TimeoutSeconds));
        var elapsed = Stopwatch.StartNew();
        log.LogDebug(
            "Local delivery started: {DeliveryId}, {Channel}, {Attempt}",
            delivery.DeliveryId,
            delivery.Channel,
            delivery.Attempts
        );
        LocalDeliveryOutcome outcome;
        try
        {
            outcome = delivery.Channel == "EMAIL"
                ? await SendSmtpAsync(
                    delivery,
                    notification,
                    timeout.Token
                )
                : await SendChatAsync(
                    delivery,
                    notification,
                    timeout.Token
                );
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            outcome = Retry("LOCAL_TIMEOUT");
        }
        catch (SmtpCommandException error)
        {
            outcome = ClassifySmtpStatus((int)error.StatusCode);
        }
        catch (Exception error) when (error is HttpRequestException or IOException
            or SocketException or SmtpProtocolException or ServiceNotConnectedException)
        {
            outcome = Retry("LOCAL_TRANSPORT_FAILURE");
        }
        catch (ServiceNotAuthenticatedException)
        {
            outcome = Rejected("LOCAL_SMTP_AUTH_REQUIRED");
        }

        catch (Exception error) when (error is not OperationCanceledException)
        {
            // 예기치 못한 공급자 오류도 원장에 영구 실패로 반환해 같은 poison 건을 무한 처리하지 않는다.
            // 호출자 취소는 위 필터에서 제외해 상위 트랜잭션의 취소 의미를 보존한다.
            log.LogWarning(
                "Local provider unexpected failure: {DeliveryId}, {FailureType}",
                delivery.DeliveryId,
                error.GetType().Name
            );
            outcome = Rejected("LOCAL_PROVIDER_ERROR");
        }
        var level = outcome.Outcome == "ACCEPTED" ? LogLevel.Information : LogLevel.Warning;
        log.Log(
            level,
            "Local delivery finished: {DeliveryId}, {Channel}, {Outcome}, {Code}, {ElapsedMs}ms",
            delivery.DeliveryId,
            delivery.Channel,
            outcome.Outcome,
            outcome.ErrorCode,
            elapsed.ElapsedMilliseconds
        );
        return outcome;
    }

    // SMTP DATA의 성공 응답을 받은 뒤 QUIT만 실패했다면 수신 성공을 취소하지 않는다.
    private async Task<LocalDeliveryOutcome> SendSmtpAsync(
        Delivery delivery,
        Notification notification,
        CancellationToken ct
    )
    {
        var id = delivery.DeliveryId.ToString(CultureInfo.InvariantCulture);
        var messageId = "delivery-" + id + "@factorymes.invalid";
        var message = new MimeMessage
        {
            MessageId = messageId,
            Subject = notification.Title,
            Body = new TextPart("plain")
            {
                Text = notification.Body
            }
        };
        message.From.Add(new MailboxAddress("FactoryMES local capture", "factorymes@factorymes.invalid"));
        message.To.Add(new MailboxAddress("Local recipient",
            "user-" + delivery.RecipientId.ToString(CultureInfo.InvariantCulture) + "@factorymes.invalid"));
        message.Headers.Add("X-FactoryMES-Delivery-Id", id);
        using var smtp = new SmtpClient();
        smtp.Timeout = LocalDeliveryPolicy.TimeoutSeconds * 1000;
        // 평문은 인증·외부 relay가 없는 internal capture 망에만 한정한다.
        await smtp.ConnectAsync(
            LocalDeliveryPolicy.SmtpHost,
            LocalDeliveryPolicy.SmtpPort,
            SecureSocketOptions.None,
            ct
        );
        await smtp.SendAsync(message, ct);
        var receipt = Receipt(
            delivery,
            notification,
            "smtp:" + messageId,
            "LOCAL_SMTP"
        );
        log.LogDebug("Local SMTP DATA acknowledged: {DeliveryId}", delivery.DeliveryId);
        try
        {
            await smtp.DisconnectAsync(true, ct);
        }
        catch (Exception error) when (error is IOException or SmtpProtocolException
            or OperationCanceledException or SocketException or SmtpCommandException
            or ServiceNotConnectedException)
        {
            log.LogDebug("SMTP QUIT failed after acceptance: {DeliveryId}", delivery.DeliveryId);
        }
        return new LocalDeliveryOutcome(
            "ACCEPTED",
            null,
            receipt
        );
    }

    private async Task<LocalDeliveryOutcome> SendChatAsync(
        Delivery delivery,
        Notification notification,
        CancellationToken ct
    )
    {
        var text = Escape("[FactoryMES 로컬 검증] " + notification.Title + "\n" + notification.Body);
        if (text.Length > 6000)
        {
            return Rejected("LOCAL_CONTENT_LIMIT");
        }
        var id = delivery.DeliveryId.ToString(CultureInfo.InvariantCulture);
        var hash = MessageHash(delivery, notification);
        using var message = new HttpRequestMessage(HttpMethod.Post, LocalDeliveryPolicy.ChatEndpoint)
        {
            Content = JsonContent.Create(new
            {
                text,
                mrkdwn = false,
                unfurl_links = false,
                unfurl_media = false
            })
        };
        message.Headers.Add("X-FactoryMES-Delivery-Id", id);
        message.Headers.Add("X-FactoryMES-Message-Hash", hash);
        message.Headers.Add("X-FactoryMES-Scenario", delivery.TargetRef.Split('/').Last());
        using var client = clients.CreateClient(ClientName);
        using var response = await client.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        var code = (int)response.StatusCode;
        if (code == 429 || code >= 500)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds;
            var seconds = retryAfter is >= 2 and <= 30 && retryAfter == Math.Truncate(retryAfter.Value)
                ? (int?)retryAfter.Value : null;
            return new LocalDeliveryOutcome(
                "RETRY",
                "LOCAL_HTTP_" + code,
                null,
                seconds
            );
        }
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return Rejected("LOCAL_HTTP_" + code);
        }
        if (response.Content.Headers.ContentLength > MaximumAckBytes)
        {
            return Rejected("LOCAL_ACK_TOO_LARGE");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[256];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > MaximumAckBytes)
            {
                return Rejected("LOCAL_ACK_TOO_LARGE");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        if (Encoding.UTF8.GetString(buffer.ToArray()) != "ok")
        {
            return Rejected("LOCAL_ACK_INVALID");
        }
        log.LogDebug("Local webhook acknowledged: {DeliveryId}", delivery.DeliveryId);
        return new LocalDeliveryOutcome(
            "ACCEPTED",
            null,
            Receipt(
                delivery,
                notification,
                "webhook:" + id,
                "LOCAL_WEBHOOK"
            )
        );
    }

    // SMTP 서버의 응답 문구는 저장하지 않고 상태 코드 범위만 안전한 원장 오류로 변환한다.
    internal static LocalDeliveryOutcome ClassifySmtpStatus(int code) =>
        code is >= 400 and <= 499
            ? Retry("LOCAL_SMTP_" + code)
            : Rejected("LOCAL_SMTP_" + code);

    private static LocalDeliveryOutcome Retry(string code) => new(
        "RETRY",
        code,
        null
    );

    private static LocalDeliveryOutcome Rejected(string code) => new(
        "REJECTED",
        code,
        null
    );

    private static string Escape(string text) => text.Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    private static string MessageHash(Delivery delivery, Notification notification) =>
        CollectionFormat.Hash(new
        {
            deliveryId = delivery.DeliveryId.ToString(CultureInfo.InvariantCulture),
            delivery.Channel,
            delivery.DestinationKey,
            subject = notification.Title,
            body = notification.Body
        });

    private static LocalReceiptView Receipt(
        Delivery delivery,
        Notification notification,
        string receiptId,
        string provider
    ) => new(
        delivery.DeliveryId.ToString(CultureInfo.InvariantCulture),
        receiptId,
        provider,
        delivery.Channel,
        delivery.DestinationKey,
        notification.Title,
        notification.Body,
        MessageHash(delivery, notification),
        CollectionFormat.UtcNow()
    );
}




