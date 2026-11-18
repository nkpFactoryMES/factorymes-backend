using FactoryMES.Contracts;

namespace FactoryMES.Backend.Features.Notifications;

public static class NotificationSettingsEndpoints
{
    // 기존 업무 그룹의 Cookie·CSRF·Problem 필터 안에 등록한다. 관리 API는 ADMIN만 허용한다.
    public static void MapPublic(RouteGroupBuilder group)
    {
        group.MapGet("/notification-settings/users", async (
            NotificationSettingsService service,
            HttpContext http,
            int? limit,
            string? cursor
        ) => await service.UsersAsync(http.User, limit, cursor)).RequireAuthorization("Admin");
        group.MapGet("/notification-settings/users/{userId}", async (
            string userId,
            NotificationSettingsService service,
            HttpContext http
        ) => await service.GetAsync(userId, http.User)).RequireAuthorization("Admin");
        group.MapGet("/notification-settings/me", async (
            NotificationSettingsService service,
            HttpContext http
        ) => await service.GetMineAsync(http.User));
        group.MapPut("/notification-settings/users/{userId}/{eventKind}", async (
            string userId,
            string eventKind,
            NotificationSettingUpdate input,
            NotificationSettingsService service,
            HttpContext http
        ) => await service.UpdateAsync(
            userId,
            eventKind,
            input,
            http.User
        )).RequireAuthorization("Admin");
    }
}
