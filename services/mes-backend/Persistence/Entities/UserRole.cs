namespace FactoryMES.Backend.Persistence.Entities;

// 사용자별 ADMIN/WORKER 역할: 인증 이후 업무 API의 권한과 조회 범위를 결정한다.
public sealed class UserRole
{
    public long UserId { get; set; }

    public string Role { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}
