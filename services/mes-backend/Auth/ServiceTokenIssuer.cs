using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryMES.Foundation;
using Microsoft.IdentityModel.Tokens;

namespace FactoryMES.Backend.Auth;

public sealed record TokenRequest(
    string ClientId,
    string ClientSecret,
    string Audience
);

// 업무 백엔드와 내부 서비스 사이의 서명 토큰 발급 경계. 허용된 대상과 범위만 발급한다.
public sealed class ServiceTokenIssuer : IDisposable
{
    private readonly ILogger<ServiceTokenIssuer> log;

    private readonly RSA rsa = RSA.Create();

    private readonly Dictionary<string, string> secrets;

    public ServiceTokenIssuer(
        IConfiguration configuration,
        ILogger<ServiceTokenIssuer>? logger = null
    )
    {
        log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ServiceTokenIssuer>
            .Instance;
        rsa.ImportFromPem(
            SecretFiles.Read(configuration["Security:PrivateKeyFile"]
            ?? "/run/secrets/service_private_key")
        );
        secrets =
            JsonSerializer.Deserialize<Dictionary<string, string>>(
            SecretFiles.Read(configuration["Security:ClientSecretsFile"]
            ?? "/run/secrets/service_clients")
        )!;
    }

    // 내부 서비스의 허용된 클라이언트·자격 증명·대상을 확인하고 제한된 수명의 서명 토큰을 발급한다.
    public object? Issue(TokenRequest request)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "Issue",
                "입력 처리 시작"
            );
            if (!secrets.TryGetValue(request.ClientId, out var expected))
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "Issue",
                    "분기 일치 [!secrets.TryGetValue(request.ClientId, out var expected)]"
                );
                log.LogWarning("서비스 인증 거절: {FailureCode}", "CLIENT_OR_CREDENTIAL_REJECTED");
                return null;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "Issue",
                    "분기 불일치 [!secrets.TryGetValue(request.ClientId, out var expected)]"
                );
            }

            if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(expected),
                        Encoding.UTF8.GetBytes(request.ClientSecret)
                    ))
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "Issue",
                    "분기 일치 [!CryptographicOperations.FixedTimeEquals( Encoding.UTF8.GetBytes...]"
                );
                log.LogWarning("서비스 인증 거절: {FailureCode}", "CLIENT_OR_CREDENTIAL_REJECTED");
                return null;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "Issue",
                    "분기 불일치 [!CryptographicOperations.FixedTimeEquals( Encoding.UTF8.GetBytes...]"
                );
            }

            var audiences = request.ClientId switch
            {
                "integration-gateway" => new[]
                {
                    "mes-backend",
                    "equipment-connector",
                    "ai-connector"
                },
                "equipment-connector" => new[]
                {
                    "mes-backend",
                    "integration-gateway"
                },
                "ai-connector" => new[]
                {
                    "mes-backend"
                },
                "jobs-worker" => new[]
                {
                    "mes-backend"
                },
                "verification" => new[]
                {
                    "mes-backend",
                    "integration-gateway",
                    "equipment-connector",
                    "ai-connector",
                    "jobs-worker"
                },
                _ => Array.Empty<string>()
            };
            if (!audiences.Contains(request.Audience, StringComparer.Ordinal))
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "Issue",
                    "분기 일치 [!audiences.Contains(request.Audience, StringComparer.Ordinal)]"
                );
                log.LogWarning("서비스 인증 거절: {FailureCode}", "CLIENT_OR_CREDENTIAL_REJECTED");
                return null;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "Issue",
                    "분기 불일치 [!audiences.Contains(request.Audience, StringComparer.Ordinal)]"
                );
            }

            string[] scopes = request.ClientId switch
            {
                "integration-gateway" => [
                    "diagnostics:read",
                    "integration:config:read",
                    "telemetry:lease:issue",
                    "equipment:configure",
                    "equipment:connection:test",
                    "analysis:execute",
                    "analysis:connection:test"
                ],
                "equipment-connector" => [
                    "diagnostics:read",
                    "telemetry:write",
                    "integration:runtime:write",
                    "integration:config:resolve",
                    "telemetry:lease:request"
                ],
                "ai-connector" => [
                    "diagnostics:read",
                    "analysis:input:read",
                    "analysis:result:write",
                    "integration:runtime:write"
                ],
                "jobs-worker" => [
                    "diagnostics:read",
                    "jobs:connections:watch",
                    "jobs:outbox:dispatch",
                    "jobs:deliveries:dispatch",
                    "jobs:reporting:run",
                    "jobs:retention:run",
                    "analysis:request"
                ],
                _ => [
                    "diagnostics:read"
                ]
            };
            log.LogInformation(
                "서비스 인증 승인: {ClientId}, {Audience}, {ScopeCount}",
                request.ClientId,
                request.Audience,
                scopes.Length
            );
            var now = DateTime.UtcNow;
            var claims = new List<Claim>
        {
            new("client_id", request.ClientId),
            new("sub", request.ClientId),
            new("jti", Guid.NewGuid()
                .ToString()),
            new(
                "iat",
                new DateTimeOffset(now).ToUnixTimeSeconds()
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64
            )
        };
            claims.AddRange(scopes.Select(scope => new Claim("scope", scope)));
            var token =
                new JwtSecurityToken(
                    "factorymes-backend",
                    request.Audience,
                    claims,
                    now,
                    now.AddMinutes(5),
                    new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
                );
            return new
            {
                accessToken = new JwtSecurityTokenHandler().WriteToken(token),
                tokenType = "Bearer",
                expiresIn = 300,
                scope = scopes
            };
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "Issue");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "Issue",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 업무 백엔드에서 설비 게이트웨이를 호출할 때 사용할 최소 범위의 내부 토큰을 만든다.
    public string GatewayToken()
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "GatewayToken",
                "입력 처리 시작"
            );
            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken(
                "factorymes-backend",
                "integration-gateway",
                [
                    new Claim("client_id", "mes-backend"),
                    new Claim("scope", "integration:config:apply"),
                    new Claim("jti", Guid.NewGuid().ToString())
                ],
                now,
                now.AddMinutes(1),
                new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
            );
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "GatewayToken");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "GatewayToken",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 업무 백엔드에서 AI 게이트웨이를 호출할 때 사용할 전용 범위의 내부 토큰을 만든다.
    public string AnalysisGatewayToken()
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "AnalysisGatewayToken",
                "입력 처리 시작"
            );
            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken(
                "factorymes-backend",
                "integration-gateway",
                [
                    new Claim("client_id", "mes-backend"),
                    new Claim("scope", "analysis:dispatch"),
                    new Claim("jti", Guid.NewGuid().ToString())
                ],
                now,
                now.AddMinutes(1),
                new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
            );
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "AnalysisGatewayToken");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "AnalysisGatewayToken",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    public void Dispose() => rsa.Dispose();
}



