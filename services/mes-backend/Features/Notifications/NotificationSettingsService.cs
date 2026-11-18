using System.Security.Claims;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Notifications;

public sealed class NotificationSettingsService(
    MesDbContext db,
    ILogger<NotificationSettingsService> log
)
{
    private static void Admin(ClaimsPrincipal user)
    {
        if (!user.IsInRole("ADMIN"))
        {
            throw new OrderFault(403, "Forbidden", "관리자 권한이 필요합니다.");
        }
    }

    // 계정 선택 목록에는 비밀번호·이메일·security stamp를 노출하지 않는다.
    public async Task<PageResult<NotificationSettingUserView>> UsersAsync(
        ClaimsPrincipal actor,
        int? limit,
        string? cursor
    )
    {
        Admin(actor);
        var size = limit ?? 100;
        if (size is < 1 or > 100)
        {
            throw new OrderFault(400, "InvalidLimit", "목록은 1~100개입니다.");
        }
        var before = cursor is null ? long.MaxValue : OrderService.ParseId(cursor);
        var users = await db.Set<UserAccount>().AsNoTracking()
            .Where(x => x.UserId < before).OrderByDescending(x => x.UserId)
            .Take(size + 1).ToArrayAsync();
        var ids = users.Take(size).Select(x => x.UserId).ToArray();
        var roles = await db.Set<UserRole>().AsNoTracking()
            .Where(x => ids.Contains(x.UserId)).ToArrayAsync();
        log.LogDebug("알림 설정 사용자 목록 조회: {ActorId}, {Count}", OrderService.Actor(actor), ids.Length);
        return new PageResult<NotificationSettingUserView>(
            users.Take(size).Select(x => UserView(x, roles.Where(r => r.UserId == x.UserId)
                .Select(r => r.Role).ToArray())).ToArray(),
            users.Length > size ? OrderService.Id(users[size - 1].UserId) : null
        );
    }

    public Task<NotificationSettingsView> GetMineAsync(ClaimsPrincipal user) =>
        DetailAsync(OrderService.Actor(user));

    public Task<NotificationSettingsView> GetAsync(string userId, ClaimsPrincipal user)
    {
        Admin(user);
        return DetailAsync(OrderService.ParseId(userId));
    }

    private async Task<NotificationSettingsView> DetailAsync(long userId)
    {
        var account = await db.Set<UserAccount>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId)
            ?? throw new OrderFault(404, "UserNotFound", "사용자를 찾을 수 없습니다.");
        var roles = await db.Set<UserRole>().AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => x.Role).ToArrayAsync();
        var settings = await db.Set<NotificationChannelSetting>().AsNoTracking()
            .Where(x => x.UserId == userId).ToArrayAsync();
        var allowed = NotificationRouting.EventKinds
            .Where(x => roles.Contains("ADMIN") || x.StartsWith("ALARM_", StringComparison.Ordinal));
        log.LogDebug("알림 설정 상세 조회: {UserId}, {StoredCount}", userId, settings.Length);
        return new NotificationSettingsView(
            UserView(account, roles),
            allowed.Select(kind =>
            {
                var setting = settings.SingleOrDefault(x => x.EventKind == kind);
                return setting is null
                    ? new NotificationSettingView(kind, "OFF", "OFF", 0, null)
                    : SettingView(setting);
            }).ToArray()
        );
    }

    // 사용자 행 잠금으로 최초 생성도 직렬화한다. expectedVersion은 재전송을 포함해 매번 검사한다.
    // 설정·수정자·사유 감사는 같은 트랜잭션에 저장하며 이전 Outbox/Delivery는 건드리지 않는다.
    public async Task<NotificationSettingView> UpdateAsync(
        string userId,
        string eventKind,
        NotificationSettingUpdate input,
        ClaimsPrincipal user
    )
    {
        Admin(user);
        var actor = OrderService.Actor(user);
        var id = OrderService.ParseId(userId);
        log.LogDebug("알림 설정 변경 검증: {ActorId}, {UserId}, {ExpectedVersion}",
            actor, id, input.ExpectedVersion);
        if (!NotificationRouting.ValidEvent(eventKind) || !NotificationRouting.ValidMode(input.EmailMode)
            || !NotificationRouting.ValidMode(input.ChatMode) || input.ExpectedVersion < 0
            || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length > 300)
        {
            log.LogWarning("알림 설정 거절: {FailureCode}", "NOTIFICATION_SETTING_VALIDATION");
            throw new OrderFault(422, "NotificationSettingValidation", "이벤트·모드·버전·변경 사유를 확인해 주세요.");
        }
        await using var tx = await db.Database.BeginTransactionAsync();
        var account = (await db.Set<UserAccount>().FromSqlInterpolated(
            $"SELECT * FROM USER_ACCOUNT WHERE USER_ID = {id} FOR UPDATE"
        ).ToListAsync()).SingleOrDefault()
            ?? throw new OrderFault(404, "UserNotFound", "사용자를 찾을 수 없습니다.");
        var admin = await db.Set<UserRole>().AnyAsync(x => x.UserId == id && x.Role == "ADMIN");
        if (!account.Active || (!admin && eventKind.StartsWith("AI_", StringComparison.Ordinal)))
        {
            log.LogWarning("알림 설정 거절: {FailureCode}", "NOTIFICATION_SETTING_TARGET");
            throw new OrderFault(422, "NotificationSettingTarget", "활성 여부와 대상자의 업무 권한을 확인해 주세요.");
        }
        var setting = await db.Set<NotificationChannelSetting>()
            .SingleOrDefaultAsync(x => x.UserId == id && x.EventKind == eventKind);
        if ((setting?.Version ?? 0) != input.ExpectedVersion)
        {
            log.LogWarning("알림 설정 거절: {FailureCode}", "VERSION_CONFLICT");
            throw new OrderFault(409, "VersionConflict", "설정이 변경되었습니다. 최신값을 조회해 주세요.");
        }
        var oldEmail = setting?.EmailMode ?? "OFF";
        var oldChat = setting?.ChatMode ?? "OFF";
        var oldVersion = setting?.Version ?? 0;
        if (setting is null)
        {
            setting = new NotificationChannelSetting { UserId = id, EventKind = eventKind };
            db.Add(setting);
            log.LogDebug("새 알림 설정 생성: {UserId}, {EventKind}", id, eventKind);
        }
        else
        {
            log.LogDebug("기존 알림 설정 갱신: {UserId}, {EventKind}, {Version}", id, eventKind, oldVersion);
        }
        setting.EmailMode = input.EmailMode;
        setting.ChatMode = input.ChatMode;
        setting.UpdatedBy = actor;
        // 같은 모드 재저장도 행위를 기록하고 버전을 증가시킨다(기존 행만 수정 상태로 표시).
        if (oldVersion > 0)
        {
            db.Entry(setting).Property(x => x.UpdatedBy).IsModified = true;
        }
        db.Add(new AuditEvent
        {
            ActorId = actor,
            Principal = "USER:" + OrderService.Id(actor),
            Action = "notification.setting.updated",
            EntityType = "NOTIFICATION_CHANNEL_SETTING",
            EntityId = OrderService.Id(id) + ":" + eventKind,
            Detail = CollectionFormat.Canonical(new
            {
                eventKind,
                oldVersion,
                newVersion = checked(oldVersion + 1),
                oldEmailMode = oldEmail,
                oldChatMode = oldChat,
                emailMode = input.EmailMode,
                chatMode = input.ChatMode,
                reason = input.Reason.Trim()
            }),
            OccurredAtUtc = CollectionFormat.UtcNow()
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        log.LogInformation("알림 설정 변경 완료: {ActorId}, {UserId}, {EventKind}, {Version}",
            actor, id, eventKind, setting.Version);
        return SettingView(setting);
    }

    private static NotificationSettingUserView UserView(UserAccount account, string[] roles) => new(
        OrderService.Id(account.UserId),
        account.Login,
        account.Name,
        roles.Order(StringComparer.Ordinal).ToArray(),
        account.Active
    );

    private static NotificationSettingView SettingView(NotificationChannelSetting setting) => new(
        setting.EventKind,
        setting.EmailMode,
        setting.ChatMode,
        setting.Version,
        setting.UpdatedAtUtc
    );
}
