namespace FactoryMES.Backend.Persistence.Entities;

// 내부 로그인 계정: 인증 서비스가 비밀번호 해시·활성 여부를 읽고 Cookie 주체를 만든다.
public sealed class UserAccount
{
    public long UserId { get; set; }

    public string Login { get; set; } = string.Empty;

    public string NormalizedLogin { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public bool Active { get; set; } = true;

    public string? Email { get; set; }

    public long FailedCount { get; set; } = 0;

    public DateTime? LockUntilUtc { get; set; }

    public string SecurityStamp { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public DateTime UpdatedAtUtc { get; set; }
}
