using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FactoryMES.Backend.Features.Notifications;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

// 실제 공급자 소스를 링크한다. HTTP는 가짜 응답, SMTP 오류는 실제 loopback TCP peer로 검사한다.
// 외부 수신 서버·실제 이메일·채팅 서비스는 사용하지 않으며 DB도 읽지 않는다.
var checks = new List<object>();
var passed = 0;
var failed = 0;
async Task Check(string label, Func<Task> action)
{
    try
    {
        await action();
        checks.Add(new {
            label,
            passed = true
        });
        passed++;
    }
    catch (Exception error)
    {
        checks.Add(new {
            label,
            passed = false,
            error = error.GetType().Name
        });
        Console.WriteLine("FAIL " + label + " " + error.GetType().Name);
        failed++;
    }
}
void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value)
{
    if (!value)
    {
        throw new InvalidOperationException("Contract assertion failed.");
    }
}
static Delivery Row() => new()
{
    DeliveryId = 42,
    NotificationId = 9,
    RecipientId = 3,
    Channel = "CHAT",
    DestinationKey = "local.chat.user.3",
    TargetRef = "local://capture/chat/3/SUCCESS",
    Attempts = 1,
    Status = "PENDING"
};
static Notification Notice() => new()
{
    NotificationId = 9,
    Title = "점검 알림 <!channel>",
    Body = "센서 <태그> & 냉각 계통을 확인하세요."
};
static HttpResponseMessage Reply(int status, string body = "ok") => new((HttpStatusCode)status)
{
    Content = new StringContent(
        body,
        Encoding.UTF8,
        "text/plain"
    )
};
static LocalDeliveryProvider Provider(Func<HttpRequestMessage, CancellationToken,
    Task<HttpResponseMessage>> send) => new(new Factory(send),
    NullLogger<LocalDeliveryProvider>.Instance);

await Check("Actual webhook uses fixed address headers escaped text and disabled formatting", async () =>
{
    var calls = 0;
    var provider = Provider(async (request, ct) =>
    {
        calls++;
        Require(request.RequestUri?.ToString() == LocalDeliveryPolicy.ChatEndpoint);
        Require(request.Method == HttpMethod.Post);
        Require(request.Headers.GetValues("X-FactoryMES-Delivery-Id").Single() == "42");
        Require(request.Headers.GetValues("X-FactoryMES-Scenario").Single() == "SUCCESS");
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var root = body.RootElement;
        Require(root.EnumerateObject().Count() == 4);
        Require(!root.GetProperty("mrkdwn").GetBoolean());
        Require(!root.GetProperty("unfurl_links").GetBoolean());
        Require(!root.GetProperty("unfurl_media").GetBoolean());
        var text = root.GetProperty("text").GetString()!;
        Require(text.Contains("&lt;!channel&gt;") && text.Contains("&amp;"));
        Require(!text.Contains("<!channel>"));
        return Reply(200);
    });
    var result = await provider.SendAsync(
        Row(),
        Notice(),
        CancellationToken.None
    );
    Require(calls == 1 && result.Outcome == "ACCEPTED" && result.Receipt is not null);
    Require(
        result.Receipt.Provider == "LOCAL_WEBHOOK" && result.Receipt.ReceiptId == "webhook:42"
    );
    Require(result.Receipt.AcceptedAt.Kind == DateTimeKind.Utc);
    Require(result.Receipt.Subject == Notice().Title && result.Receipt.Body == Notice().Body);
});

await Check("Webhook receipt hash binds original content recipient channel and delivery", async () =>
{
    var provider = Provider((_, _) => Task.FromResult(Reply(200)));
    var row = Row();
    var note = Notice();
    var result = await provider.SendAsync(
        row,
        note,
        CancellationToken.None
    );
    var expected = CollectionFormat.Hash(new
    {
        deliveryId = "42",
        row.Channel,
        row.DestinationKey,
        subject = note.Title,
        body = note.Body
    });
    Require(result.Receipt!.MessageHash == expected);
});

foreach (var code in new[] {
    429,
    500,
    502,
    503,
    504
})
{
    await Check("HTTP" + code + " is durable retry candidate", async () =>
    {
        var result = await Provider((_, _) => Task.FromResult(Reply(code)))
            .SendAsync(
                Row(),
                Notice(),
                CancellationToken.None
            );
        Require(result.Outcome == "RETRY" && result.ErrorCode == "LOCAL_HTTP_" + code);
        Require(result.Receipt is null);
    });
}
foreach (var code in new[] {
    201,
    202,
    204,
    301,
    302,
    307,
    308,
    400,
    401,
    403,
    404,
    409
})
{
    await Check("HTTP" + code + " does not fabricate acceptance", async () =>
    {
        var calls = 0;
        var result = await Provider((_, _) =>
        {
            calls++;
            return Task.FromResult(Reply(code));
        }).SendAsync(
            Row(),
            Notice(),
            CancellationToken.None
        );
        Require(result.Outcome == "REJECTED" && result.Receipt is null && calls == 1);
    });
}
foreach (var body in new[] {
    "",
    "OK",
    "ok\n",
    "{}",
    "paid-provider-accepted"
})
{
    await Check("Noncanonical200 body rejected: length" + body.Length, async () =>
    {
        var result = await Provider((_, _) => Task.FromResult(Reply(200, body)))
            .SendAsync(
                Row(),
                Notice(),
                CancellationToken.None
            );
        Require(result.Outcome == "REJECTED" && result.ErrorCode == "LOCAL_ACK_INVALID");
    });
}
await Check("Known Content-Length over1KiB rejected", async () =>
{
    var result = await Provider((_, _) => Task.FromResult(Reply(200, new string('x', 1025))))
        .SendAsync(
            Row(),
            Notice(),
            CancellationToken.None
        );
    Require(result.ErrorCode == "LOCAL_ACK_TOO_LARGE" && result.Receipt is null);
});
await Check("Unknown Content-Length stream is still bounded", async () =>
{
    var result = await Provider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new UnboundedLengthContent(new string('x', 2048))
    })).SendAsync(
        Row(),
        Notice(),
        CancellationToken.None
    );
    Require(result.ErrorCode == "LOCAL_ACK_TOO_LARGE" && result.Receipt is null);
});
foreach (var seconds in new[] {
    0,
    1,
    2,
    15,
    30,
    31,
    3600
})
{
    await Check("RetryAfter bounds " + seconds, async () =>
    {
        var result = await Provider((_, _) =>
        {
            var reply = Reply(429);
            reply.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            return Task.FromResult(reply);
        }).SendAsync(
            Row(),
            Notice(),
            CancellationToken.None
        );
        Require(result.RetryAfterSeconds == (seconds is >= 2 and <= 30 ? seconds : null));
    });
}
await Check("RetryAfter date is not an unbounded wait", async () =>
{
    var result = await Provider((_, _) =>
    {
        var reply = Reply(429);
        reply.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddHours(1));
        return Task.FromResult(reply);
    }).SendAsync(
        Row(),
        Notice(),
        CancellationToken.None
    );
    Require(result.RetryAfterSeconds is null);
});
await Check("HTTP connection error returns safe retry code only", async () =>
{
    var result = await Provider((_, _) => throw new HttpRequestException("MUST_NOT_LEAK_PRIVATE_BODY"))
        .SendAsync(
            Row(),
            Notice(),
            CancellationToken.None
        );
    Require(result.ErrorCode == "LOCAL_TRANSPORT_FAILURE" && result.Receipt is null);
});
foreach (var failure in new Exception[]
{
    new InvalidOperationException("MUST_NOT_LEAK_PROVIDER_BODY"),
    new FormatException("MUST_NOT_LEAK_PROVIDER_BODY"),
    new NotSupportedException("MUST_NOT_LEAK_PROVIDER_BODY")
})
{
    await Check("Unexpected " + failure.GetType().Name + " becomes durable permanent failure", async () =>
    {
        var result = await Provider((_, _) => throw failure)
            .SendAsync(
                Row(),
                Notice(),
                CancellationToken.None
            );
        Require(result.Outcome == "REJECTED" && result.ErrorCode == "LOCAL_PROVIDER_ERROR");
        Require(result.Receipt is null && result.RetryAfterSeconds is null);
    });
}
await Check("Unexpected response stream failure becomes durable permanent failure", async () =>
{
    var result = await Provider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ThrowingContent()
    })).SendAsync(
        Row(),
        Notice(),
        CancellationToken.None
    );
    Require(result.Outcome == "REJECTED" && result.ErrorCode == "LOCAL_PROVIDER_ERROR");
    Require(result.Receipt is null);
});
await Check("Allowed4000char body that expands above receiver limit is rejected before HTTP", async () =>
{
    var note = Notice();
    note.Body = new string('&', 4000);
    var calls = 0;
    var result = await Provider((_, _) =>
    {
        calls++;
        return Task.FromResult(Reply(200));
    }).SendAsync(
        Row(),
        note,
        CancellationToken.None
    );
    Require(result.Outcome == "REJECTED" && result.ErrorCode == "LOCAL_CONTENT_LIMIT");
    Require(calls == 0 && result.Receipt is null);
});
await Check("Provider total timeout does not fabricate Mock success", async () =>
{
    var result = await Provider(async (_, ct) =>
    {
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        return Reply(200);
    }).SendAsync(
        Row(),
        Notice(),
        CancellationToken.None
    );
    Require(
        result.Outcome == "RETRY" && result.ErrorCode == "LOCAL_TIMEOUT" && result.Receipt is null
    );
});
await Check("Caller cancellation propagates instead of consuming retry budget", async () =>
{
    using var stop = new CancellationTokenSource();
    stop.Cancel();
    try
    {
        await Provider(async (_, ct) =>
        {
            await Task.Delay(1000, ct);
            return Reply(200);
        }).SendAsync(
            Row(),
            Notice(),
            stop.Token
        );
        throw new InvalidOperationException("Cancellation was swallowed.");
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
});

foreach (var target in new[]
{
    "https://external.invalid/hooks/secret",
    "local://capture/chat/3/SUCCESS?url=https://external.invalid",
    "local://capture/chat/03/SUCCESS",
    "local://capture/chat/3/UNKNOWN",
    "local://capture/email/3/SUCCESS"
})
{
    await Check("Unapproved target rejected before network: " + target.Split('/').Last(), async () =>
    {
        var row = Row();
        row.TargetRef = target;
        var calls = 0;
        var result = await Provider((_, _) =>
        {
            calls++;
            return Task.FromResult(Reply(200));
        }).SendAsync(
            row,
            Notice(),
            CancellationToken.None
        );
        Require(result.Outcome == "REJECTED" && calls == 0);
    });
}
await Check("Recipient route mismatch rejected before network", async () =>
{
    var row = Row();
    row.DestinationKey = "local.chat.user.999";
    var result = await Provider((_, _) => throw new Exception("No network expected"))
        .SendAsync(
            row,
            Notice(),
            CancellationToken.None
        );
    Require(result.ErrorCode == "LOCAL_TARGET_REJECTED");
});
await Check("Subject CRLF rejected before MIME or HTTP", async () =>
{
    var note = Notice();
    note.Title = "Subject\r\nInjected: header";
    var result = await Provider((_, _) => throw new Exception("No network expected"))
        .SendAsync(
            Row(),
            note,
            CancellationToken.None
        );
    Require(result.ErrorCode == "LOCAL_TARGET_REJECTED");
});
await Check("Escaping expansion above receiver limit rejected", async () =>
{
    var note = Notice();
    note.Body = new string('<', 3000);
    var result = await Provider((_, _) => throw new Exception("No network expected"))
        .SendAsync(
            Row(),
            note,
            CancellationToken.None
        );
    Require(result.ErrorCode == "LOCAL_CONTENT_LIMIT");
});
foreach (var code in new[] {
    421,
    450,
    451,
    452,
    500,
    530,
    535,
    550,
    552,
    554
})
{
    await Check("Actual loopback SMTP status " + code + " maps to safe outcome", async () =>
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(stop.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII)
            {
                NewLine = "\r\n",
                AutoFlush = true
            };
            await writer.WriteLineAsync("220 loopback SMTP ready");
            while (!stop.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(stop.Token);
                if (line is null)
                {
                    break;
                }
                var reply = line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase)
                    ? code + " private reply body must not leak"
                    : line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase) ? "221 bye" : "250 ok";
                await writer.WriteLineAsync(reply);
                if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
        }, stop.Token);
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(
            "127.0.0.1",
            port,
            SecureSocketOptions.None,
            stop.Token
        );
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("sender@factorymes.invalid"));
        message.To.Add(MailboxAddress.Parse("recipient@factorymes.invalid"));
        message.Subject = "Loopback contract fixture";
        message.Body = new TextPart("plain") {
            Text = "Local only"
        };
        try
        {
            await smtp.SendAsync(message, stop.Token);
            throw new InvalidOperationException("Peer should reject MAIL.");
        }
        catch (SmtpCommandException error)
        {
            var result = LocalDeliveryProvider.ClassifySmtpStatus((int)error.StatusCode);
            Require(result.Outcome == (code < 500 ? "RETRY" : "REJECTED"));
            Require(result.ErrorCode == "LOCAL_SMTP_" + code && result.Receipt is null);
        }
        catch (ServiceNotAuthenticatedException)
        {
            // MailKit는 530을 일반 SMTP 명령 예외 대신 인증 예외로 변환한다.
            // 공급자도 이 예외를 LOCAL_SMTP_AUTH_REQUIRED 영구 실패로 분류한다.
            Require(code == 530);
            var result = LocalDeliveryProvider.ClassifySmtpStatus(code);
            Require(result.Outcome == "REJECTED" && result.Receipt is null);
        }
        finally
        {
            await smtp.DisconnectAsync(false, CancellationToken.None);
        }
        await peer;
    });
}

var report = args.Length > 0 ? Path.GetFullPath(args[0])
    : Path.Combine(AppContext.BaseDirectory, "delivery-provider-contracts.json");
Directory.CreateDirectory(Path.GetDirectoryName(report)!);
await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
{
    suite = "Actual local delivery provider source contracts",
    passed,
    failed,
    externalNetworkCalls = 0,
    smtpLoopbackWire = true,
    smtpMailpitIntegration = false,
    httpUsesFakeHandler = true,
    checks
}, new JsonSerializerOptions {
    WriteIndented = true
}));
Console.WriteLine($"Delivery provider contract checks: {passed} PASS / {failed} FAIL; {report}");
Environment.ExitCode = failed == 0 ? 0 : 1;

sealed class Factory(Func<HttpRequestMessage, CancellationToken,
    Task<HttpResponseMessage>> send) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        if (name != LocalDeliveryProvider.ClientName)
        {
            throw new InvalidOperationException("Unexpected client name.");
        }
        return new HttpClient(new Handler(send))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}
sealed class Handler(Func<HttpRequestMessage, CancellationToken,
    Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken ct) => send(request, ct);
}
sealed class UnboundedLengthContent(string content) : HttpContent
{
    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(content)).AsTask();
}




sealed class ThrowingContent : HttpContent
{
    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        throw new InvalidOperationException("MUST_NOT_LEAK_RESPONSE_BODY");
}

