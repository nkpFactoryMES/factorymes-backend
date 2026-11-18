using System.Security.Claims;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Auth;

public sealed record LoginRequest(string Login, string Password);

public sealed record PasswordChange(string CurrentPassword, string NewPassword);

public sealed record UserSession(
    long UserId,
    string Login,
    string Name,
    string Stamp,
    string[] Roles
)
{
    public ClaimsPrincipal Principal() =>
        new(
        new ClaimsIdentity(new[]
    {
        new Claim(
            ClaimTypes.NameIdentifier,
            UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ),
        new Claim(ClaimTypes.Name, Name),
        new Claim("login", Login),
        new Claim("security_stamp", Stamp)
    }.Concat(Roles.Select(role => new Claim(ClaimTypes.Role, role))), "MesCookie")
    );

    public object Response() => new
    {
        userId = UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        login = Login,
        name = Name,
        roles = Roles
    };
}

// Cookie 인증 경계. 브라우저 입력과 저장된 계정·역할을 검증하고 해시·스탬프는 외부 로그에 노출하지 않는다.
public sealed class UserAuthentication(
    MesDbContext db,
    IPasswordHasher<UserAccount> hasher,
    ILogger<UserAuthentication>? logger = null
)
{
    private readonly ILogger<UserAuthentication> log = logger
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<UserAuthentication>.Instance;

    // 로그인 화면의 자격 증명을 받아 계정 잠금·해시·역할을 검증하고 Cookie용 세션을 반환한다. 비밀번호는 기록하지 않는다.
    public async Task<UserSession?> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "LoginAsync",
                "입력 처리 시작"
            );
            if (string.IsNullOrWhiteSpace(request.Login)
                || string.IsNullOrEmpty(request.Password)
                || request.Login.Length > 100
                || request.Password.Length > 256)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "LoginAsync",
                    "분기 일치 [string.IsNullOrWhiteSpace(request.Login) || string.IsNullOrEmpty...]"
                );
                log.LogWarning(
                    "인증 요청 거절: {Operation}, {FailureCode}",
                    "LoginAsync",
                    "AUTH_VALIDATION_REJECTED"
                );
                return null;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "LoginAsync",
                    "분기 불일치 [string.IsNullOrWhiteSpace(request.Login) || string.IsNullOrEmpty...]"
                );
            }

            var normalized = request.Login.Trim()
                        .ToUpperInvariant();
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            var users =
                await db.Set<UserAccount>()
                    .FromSqlInterpolated(
                $"SELECT * FROM USER_ACCOUNT WHERE NORMALIZED_LOGIN = {normalized} FOR UPDATE"
            )
                    .ToListAsync(cancellationToken);
            var user = users.SingleOrDefault();
            if (user is null || !user.Active || user.LockUntilUtc > DateTime.UtcNow)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "LoginAsync",
                    "분기 일치 [user is null || !user.Active || user.LockUntilUtc > DateTime.Utc...]"
                );
                log.LogWarning(
                    "로그인 거절: {FailureCode}, {ActorUserId}",
                    "ACCOUNT_UNAVAILABLE",
                    user?.UserId
                );
                return null;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "LoginAsync",
                    "분기 불일치 [user is null || !user.Active || user.LockUntilUtc > DateTime.Utc...]"
                );
            }

            var result = hasher.VerifyHashedPassword(
                user,
                user.PasswordHash,
                request.Password
            );
            if (result == PasswordVerificationResult.Failed)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "LoginAsync",
                    "분기 일치 [result == CredentialVerificationResult.Failed]"
                );
                log.LogWarning("로그인 자격 증명 검증 실패: {ActorUserId}", user.UserId);
                user.FailedCount++;
                if (user.FailedCount >= 5)
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "LoginAsync",
                        "분기 일치 [user.FailedCount >= 5]"
                    );
                    user.LockUntilUtc = DateTime.UtcNow.AddMinutes(15);
                }
                else
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "LoginAsync",
                        "분기 불일치 [user.FailedCount >= 5]"
                    );
                }

                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return null;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "LoginAsync",
                    "분기 불일치 [result == CredentialVerificationResult.Failed]"
                );
            }

            var roles =
                await db.Set<UserRole>()
                    .Where(x => x.UserId == user.UserId)
                    .Select(x => x.Role)
                    .ToArrayAsync(cancellationToken);
            if (roles.Length == 0 || roles.Any(x => x is not ("ADMIN" or "WORKER")))
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "LoginAsync",
                    "분기 일치 [roles.Length == 0 || roles.Any(x => x is not (\"ADMIN\" or \"WORKER...]"
                );
                log.LogWarning(
                    "인증 요청 거절: {Operation}, {FailureCode}",
                    "LoginAsync",
                    "AUTH_VALIDATION_REJECTED"
                );
                return null;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "LoginAsync",
                    "분기 불일치 [roles.Length == 0 || roles.Any(x => x is not (\"ADMIN\" or \"WORKER...]"
                );
            }

            log.LogDebug(
                "로그인 역할 검증 통과: {ActorUserId}, {RoleCount}",
                user.UserId,
                roles.Length
            );
            user.FailedCount = 0;
            user.LockUntilUtc = null;
            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "LoginAsync",
                    "분기 일치 [result == CredentialVerificationResult.SuccessRehashNeeded]"
                );
                user.PasswordHash = hasher.HashPassword(user, request.Password);
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "LoginAsync",
                    "분기 불일치 [result == CredentialVerificationResult.SuccessRehashNeeded]"
                );
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            log.LogInformation(
                "로그인 완료: {ActorUserId}, {RoleCount}",
                user.UserId,
                roles.Length
            );
            return new(
                user.UserId,
                user.Login,
                user.Name,
                user.SecurityStamp,
                roles
            );
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "LoginAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "LoginAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // Cookie의 사용자 식별자·보안 스탬프를 DB와 대조해 현재 유효한 계정과 권한만 복원한다.
    public async Task<UserSession?> ValidateAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ValidateAsync",
                "입력 처리 시작"
            );
            if (!long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ValidateAsync",
                    "분기 일치 [!long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifie...]"
                );
                log.LogWarning(
                    "인증 요청 거절: {Operation}, {FailureCode}",
                    "ValidateAsync",
                    "AUTH_VALIDATION_REJECTED"
                );
                return null;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ValidateAsync",
                    "분기 불일치 [!long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifie...]"
                );
            }

            var user =
                        await db.Set<UserAccount>()
                            .AsNoTracking()
                            .SingleOrDefaultAsync(
                        x => x.UserId == id,
                        cancellationToken
                    );
            if (user is null
                || !user.Active
                || user.LockUntilUtc > DateTime.UtcNow
                || user.SecurityStamp != principal.FindFirstValue("security_stamp"))
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ValidateAsync",
                    "분기 일치 [user is null || !user.Active || user.LockUntilUtc > DateTime.Utc...]"
                );
                log.LogWarning(
                    "인증 요청 거절: {Operation}, {FailureCode}",
                    "ValidateAsync",
                    "AUTH_VALIDATION_REJECTED"
                );
                return null;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ValidateAsync",
                    "분기 불일치 [user is null || !user.Active || user.LockUntilUtc > DateTime.Utc...]"
                );
            }

            var roles =
                        await db.Set<UserRole>()
                            .Where(x => x.UserId == id)
                            .Select(x => x.Role)
                            .ToArrayAsync(cancellationToken);
            if (roles.Length == 0
                || !roles.Order()
                    .SequenceEqual(
                principal.FindAll(ClaimTypes.Role)
                    .Select(x => x.Value)
                    .Order()
            ))
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ValidateAsync",
                    "분기 일치 [roles.Length == 0 || !roles.Order() .SequenceEqual( principal.Fi...]"
                );
                log.LogWarning(
                    "인증 요청 거절: {Operation}, {FailureCode}",
                    "ValidateAsync",
                    "AUTH_VALIDATION_REJECTED"
                );
                return null;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ValidateAsync",
                    "분기 불일치 [roles.Length == 0 || !roles.Order() .SequenceEqual( principal.Fi...]"
                );
            }

            return new(
                user.UserId,
                user.Login,
                user.Name,
                user.SecurityStamp,
                roles
            );
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "ValidateAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "ValidateAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 로그인 사용자의 현재 암호를 검증한 뒤 새 해시와 보안 스탬프를 저장해 이전 세션을 무효화한다.
    public async Task<bool> ChangePasswordAsync(
        ClaimsPrincipal principal,
        PasswordChange change,
        CancellationToken ct
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ChangePasswordAsync",
                "입력 처리 시작"
            );
            if (string.IsNullOrEmpty(change.NewPassword)
                || string.IsNullOrEmpty(change.CurrentPassword)
                || change.NewPassword.Length is < 12 or > 256
                || change.CurrentPassword.Length > 256)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ChangePasswordAsync",
                    "분기 일치 [string.IsNullOrEmpty(change.NewCredential) || string.IsNullOrEmp...]"
                );
                log.LogWarning(
                    "인증 요청 거절: {Operation}, {FailureCode}",
                    "ChangePasswordAsync",
                    "AUTH_VALIDATION_REJECTED"
                );
                return false;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ChangePasswordAsync",
                    "분기 불일치 [string.IsNullOrEmpty(change.NewCredential) || string.IsNullOrEmp...]"
                );
            }

            var id = long.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var user = await db.Set<UserAccount>()
                .SingleAsync(x => x.UserId == id, ct);
            if (hasher.VerifyHashedPassword(
                user,
                user.PasswordHash,
                change.CurrentPassword
            ) == PasswordVerificationResult.Failed)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ChangePasswordAsync",
                    "분기 일치 [hasher.VerifyHashedCredential( user, user.CredentialHash, change...]"
                );
                log.LogWarning(
                    "인증 요청 거절: {Operation}, {FailureCode}",
                    "ChangePasswordAsync",
                    "AUTH_VALIDATION_REJECTED"
                );
                return false;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ChangePasswordAsync",
                    "분기 불일치 [hasher.VerifyHashedCredential( user, user.CredentialHash, change...]"
                );
            }

            user.PasswordHash = hasher.HashPassword(user, change.NewPassword);
            user.SecurityStamp = Guid.NewGuid()
                .ToString("N");
            await db.SaveChangesAsync(ct);
            log.LogInformation("암호 변경 및 기존 세션 무효화 완료: {ActorUserId}", id);
            return true;
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "ChangePasswordAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "ChangePasswordAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }
}



