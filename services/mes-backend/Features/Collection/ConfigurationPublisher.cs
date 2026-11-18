using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FactoryMES.Backend.Auth;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Collection;

// Backend 원본 설정의 Outbox를 읽어 게이트웨이 Redis 갱신 API로 전달한다.
// HTTP 전송 동안 DB 행 잠금을 유지하지 않으며 ACK 바인딩 확인 후 전달 상태를 저장한다.
public sealed class ConfigurationPublisher(
    IServiceScopeFactory scopes,
    IHttpClientFactory clients,
    ServiceTokenIssuer issuer,
    ILogger<ConfigurationPublisher> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
                var now = CollectionFormat.UtcNow();
                var rows = await db.Set<OutboxEvent>()
                    .Where(x => x.Type == "mes.integration.config.changed.v1"
                        && x.Status != "DELIVERED"
                        && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= now))
                    .OrderBy(x => x.OutboxId).Take(20).ToListAsync(stoppingToken);
                logger.LogDebug("Configuration publisher polled {Count} pending events", rows
                    .Count);
                foreach (var row in rows)
                {
                    logger.LogDebug(
                        "Publishing config event {OutboxId}, attempt {Attempt}",
                        row.OutboxId,
                        row.Attempts + 1
                    );
                    row.Attempts++;
                    try
                    {
                        using var client = clients.CreateClient("configuration-publisher");
                        using var request = new HttpRequestMessage(HttpMethod.Post,
                            "http://integration-gateway:8080/internal/v1/integration/config-changes");
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                            issuer.GatewayToken());
                        request.Content = new StringContent(
                            row.Payload,
                            System.Text.Encoding.UTF8,
                            "application/json"
                        );
                        using var response = await client.SendAsync(request, stoppingToken);
                        response.EnsureSuccessStatusCode();
                        var ack = await response.Content
                            .ReadFromJsonAsync<ConfigEventAck>(stoppingToken);
                        if (ack?.EventId != row.EventId || ack.TargetId != row.AggregateId
                            || ack.Outcome is not ("APPLIED" or "ALREADY_APPLIED" or "SUPERSEDED"))
                            throw new InvalidOperationException("Invalid config acknowledgement");
                        row.Status = "DELIVERED";
                        row.DeliveredAtUtc = CollectionFormat.UtcNow();
                        row.LastError = null;
                    }
                    catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                    {
                        row.Status = "FAILED";
                        row.LastError = error.GetType().Name;
                        row.NextAttemptAtUtc = CollectionFormat.UtcNow().AddSeconds(Math.Min(30,
                            row.Attempts * 2));
                    }
                    await db.SaveChangesAsync(stoppingToken);
                    if (row.Status == "DELIVERED")
                    {
                        logger.LogInformation(
                            "Configuration delivery committed: outbox {OutboxId}, version {Version}, attempt {Attempt}",
                            row.OutboxId,
                            row.AggregateVersion,
                            row.Attempts
                        );
                    }
                    else
                    {
                        logger.LogWarning(
                            "Configuration delivery scheduled for retry: outbox {OutboxId}, attempt {Attempt}",
                            row.OutboxId,
                            row.Attempts
                        );
                    }
                }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Configuration publisher retry: {ErrorType}", error.GetType()
                    .Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}


