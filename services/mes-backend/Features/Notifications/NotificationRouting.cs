using System.Globalization;
using System.Text.Json;

namespace FactoryMES.Backend.Features.Notifications;

// 생성 당시 설정을 Outbox에 담는다. 소비자는 이 값만 사용하고 현재 설정은 다시 읽지 않는다.
public sealed record NotificationRoutingSnapshot(
    string UserId,
    long? SettingVersion,
    string EmailMode,
    string ChatMode
);

public static class NotificationRouting
{
    public static readonly string[] EventKinds =
    [
        "ALARM_OPENED",
        "ALARM_RECOVERED",
        "ALARM_CLOSED",
        "ALARM_FORCE_CLOSED",
        "AI_COMPLETED",
        "AI_SCHEDULED"
    ];

    public static bool ValidEvent(string? value) => value is not null
        && EventKinds.Contains(value, StringComparer.Ordinal);

    public static bool ValidMode(string? value) => value is "OFF" or "MOCK" or "LOCAL";

    // optional 필드는 함께 존재해야 한다. null·중복 사용자·범위 밖 ID·잘못된 버전은 거절한다.
    // validateOnly 호출도 동일한 순수 검증을 통과해야 하며 원장이나 네트워크에는 접근하지 않는다.
    public static NotificationRoutingSnapshot[]? ReadSnapshot(
        JsonElement data,
        long[] recipients
    )
    {
        var hasRouting = data.TryGetProperty("routing", out var routing);
        var hasEvent = data.TryGetProperty("eventKind", out var eventKind);
        if (!hasRouting && !hasEvent)
        {
            return null;
        }
        if (!hasRouting || !hasEvent || eventKind.ValueKind != JsonValueKind.String
            || !ValidEvent(eventKind.GetString()) || routing.ValueKind != JsonValueKind.Array
            || routing.GetArrayLength() != recipients.Length || recipients.Length is < 1 or > 100)
        {
            throw new InvalidOperationException("ROUTING_SNAPSHOT_REJECTED");
        }

        var result = new List<NotificationRoutingSnapshot>();
        var seen = new HashSet<long>();
        foreach (var item in routing.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("userId", out var idValue)
                || idValue.ValueKind != JsonValueKind.String
                || !long.TryParse(idValue.GetString(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var userId)
                || userId <= 0 || idValue.GetString() != userId.ToString(CultureInfo.InvariantCulture)
                || !seen.Add(userId) || !recipients.Contains(userId)
                || !item.TryGetProperty("settingVersion", out var versionValue)
                || !item.TryGetProperty("emailMode", out var emailValue)
                || emailValue.ValueKind != JsonValueKind.String
                || !ValidMode(emailValue.GetString())
                || !item.TryGetProperty("chatMode", out var chatValue)
                || chatValue.ValueKind != JsonValueKind.String
                || !ValidMode(chatValue.GetString()))
            {
                throw new InvalidOperationException("ROUTING_SNAPSHOT_REJECTED");
            }
            long? version = null;
            if (versionValue.ValueKind != JsonValueKind.Null)
            {
                if (versionValue.ValueKind != JsonValueKind.Number
                    || !versionValue.TryGetInt64(out var number) || number < 1)
                {
                    throw new InvalidOperationException("ROUTING_VERSION_REJECTED");
                }
                version = number;
            }
            if (version is null && (emailValue.GetString() != "OFF"
                || chatValue.GetString() != "OFF"))
            {
                throw new InvalidOperationException("ROUTING_VERSION_REJECTED");
            }
            result.Add(new NotificationRoutingSnapshot(
                idValue.GetString()!,
                version,
                emailValue.GetString()!,
                chatValue.GetString()!
            ));
        }
        return result.ToArray();
    }
}
