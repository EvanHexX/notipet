using System;
using Notipet.Shared;
using Notipet.Sound;

namespace Notipet.Windows;

// Display text for the internal identifiers the UI shows: levels, repeat
// modes, sound aliases, suppression reasons, delivery states. The identifiers
// themselves stay English on the wire and in settings.json; only their labels
// follow the chosen language.
internal static class UiText
{
    public static string Level(NotificationLevel level) => level switch
    {
        NotificationLevel.Success => Loc.T("Success", "완료"),
        NotificationLevel.Attention => Loc.T("Attention", "주의"),
        NotificationLevel.Warn => Loc.T("Warning", "경고"),
        NotificationLevel.Error => Loc.T("Error", "오류"),
        NotificationLevel.Critical => Loc.T("Critical", "긴급"),
        _ => Loc.T("Info", "정보")
    };

    public static string LevelDescription(NotificationLevel level) => level switch
    {
        NotificationLevel.Success => Loc.T("A turn or task finished", "턴·작업이 끝났을 때"),
        NotificationLevel.Attention => Loc.T("The agent is waiting for you - permission or input",
                                             "에이전트가 나를 기다릴 때 — 권한 승인, 입력 대기"),
        NotificationLevel.Warn => Loc.T("Something worth a look", "확인해 볼 만한 것"),
        NotificationLevel.Error => Loc.T("Something failed", "무언가 실패했을 때"),
        NotificationLevel.Critical => Loc.T("Must not be missed - repeats until acknowledged",
                                            "놓치면 안 되는 것 — 확인할 때까지 반복"),
        _ => Loc.T("General information", "일반 정보")
    };

    public static string Repeat(RepeatMode mode) => mode switch
    {
        RepeatMode.Repeat => Loc.T("Repeat", "반복"),
        RepeatMode.UntilAck => Loc.T("Until acknowledged", "확인할 때까지"),
        _ => Loc.T("Once", "1회")
    };

    public static string Sound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Loc.T("(default)", "(기본값)");
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) return Loc.T("No sound", "소리 없음");
        if (value.StartsWith("library:", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("My sound: ", "내 소리: ") + value["library:".Length..];
        }

        return value switch
        {
            "Notification.Default" => Loc.T("Default notification", "기본 알림"),
            "Notification.IM" => Loc.T("Message", "메시지"),
            "Notification.Looping.Alarm" => Loc.T("Alarm (5 s)", "알람 (5초)"),
            "Notification.Looping.Call" => Loc.T("Ringing call", "전화 벨"),
            "Notification.Reminder" => Loc.T("Reminder", "미리 알림"),
            "SystemAsterisk" => Loc.T("Asterisk", "별표"),
            "SystemExclamation" => Loc.T("Exclamation", "느낌표"),
            "SystemHand" => Loc.T("Critical stop", "중지"),
            "SystemNotification" => Loc.T("System notification", "시스템 알림"),
            "SystemQuestion" => Loc.T("Question", "질문"),
            "WindowsLogon" => Loc.T("Windows logon", "윈도우 로그온"),
            _ => value
        };
    }

    public static string Reason(string? reason) => reason switch
    {
        "muted" => Loc.T("Muted", "음소거"),
        "quiet_hours" => Loc.T("Quiet hours", "방해금지 시간대"),
        "focus_assist" => Loc.T("Windows Do Not Disturb", "윈도우 방해 금지"),
        "deduped" => Loc.T("Duplicate", "중복"),
        "rate_limited" => Loc.T("Too many at once", "너무 많음"),
        "source_disabled" => Loc.T("Source turned off", "소스 꺼짐"),
        "ttl_expired" => Loc.T("Expired", "만료"),
        "all_channels_disabled" => Loc.T("All channels off", "모든 채널 꺼짐"),
        null or "" => "",
        _ => reason
    };

    public static string Channel(string channel) => channel switch
    {
        "windows_sound" => Loc.T("Sound", "소리"),
        "tray_balloon" => Loc.T("Notification", "알림"),
        "windows_toast" => Loc.T("Toast", "토스트"),
        "mobile_push" => Loc.T("Phone", "휴대폰"),
        _ => channel
    };

    public static string Status(string status) => status switch
    {
        "delivered" => Loc.T("delivered", "전달"),
        "skipped" => Loc.T("skipped", "건너뜀"),
        "failed" => Loc.T("failed", "실패"),
        "disabled" => Loc.T("off", "꺼짐"),
        "pending" => Loc.T("pending", "대기"),
        _ => status
    };

    public static string Source(string? source) => source switch
    {
        PayloadMapper.SourceClaude => "Claude Code",
        PayloadMapper.SourceCodex => "Codex",
        PayloadMapper.SourceManual => Loc.T("Manual", "수동"),
        null or "" => "",
        _ => source
    };

    // The agent on a card's chip: never empty, so an unknown sender still
    // says who it was.
    public static string Agent(string? source)
    {
        var label = Source(source);
        return label.Length == 0 ? Loc.T("Unknown", "알 수 없음") : label;
    }

    // The group for cards that did not say which project they came from.
    public static string OtherProject() => Loc.T("Other", "기타");

    // Where a card's thread link goes.
    public static string OpenThreadIn(string? source) => source switch
    {
        PayloadMapper.SourceClaude => Loc.T("Open in Claude", "Claude에서 열기"),
        PayloadMapper.SourceCodex => Loc.T("Open in Codex", "Codex에서 열기"),
        _ => Loc.T("Open thread", "스레드 열기")
    };

    public static string Relative(DateTimeOffset at, DateTimeOffset now)
    {
        var elapsed = now - at;
        if (elapsed < TimeSpan.FromSeconds(45)) return Loc.T("just now", "방금");
        if (elapsed < TimeSpan.FromMinutes(60))
        {
            var minutes = Math.Max(1, (int)Math.Round(elapsed.TotalMinutes));
            return Loc.T($"{minutes} min ago", $"{minutes}분 전");
        }
        if (at.Date == now.Date)
        {
            var hours = (int)elapsed.TotalHours;
            return hours < 1 ? at.ToString("HH:mm") : Loc.T($"{hours} h ago", $"{hours}시간 전");
        }
        if (at.Date == now.Date.AddDays(-1)) return Loc.T("Yesterday ", "어제 ") + at.ToString("HH:mm");
        return at.ToString("M/d HH:mm");
    }

    public static bool RunSelfTest()
    {
        var original = Loc.IsKorean;
        try
        {
            var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

            Loc.SetLanguage("KO");
            if (Level(NotificationLevel.Critical) != "긴급") return false;
            if (Relative(now.AddSeconds(-10), now) != "방금") return false;
            if (Relative(now.AddMinutes(-5), now) != "5분 전") return false;
            if (Relative(now.AddHours(-3), now) != "3시간 전") return false;
            if (!Relative(now.AddDays(-1), now).StartsWith("어제", StringComparison.Ordinal)) return false;
            if (Sound("none") != "소리 없음") return false;
            if (Sound("library:ding") != "내 소리: ding") return false;
            if (Reason("focus_assist") != "윈도우 방해 금지") return false;
            if (OtherProject() != "기타") return false;
            if (Agent(null) != "알 수 없음") return false;
            if (Agent("mybot") != "mybot") return false;
            if (OpenThreadIn(PayloadMapper.SourceCodex) != "Codex에서 열기") return false;

            Loc.SetLanguage("EN");
            if (OtherProject() != "Other") return false;
            if (Agent(PayloadMapper.SourceClaude) != "Claude Code") return false;
            if (OpenThreadIn(PayloadMapper.SourceClaude) != "Open in Claude") return false;
            if (Level(NotificationLevel.Critical) != "Critical") return false;
            if (Relative(now.AddMinutes(-5), now) != "5 min ago") return false;
            if (Sound("Notification.Looping.Alarm") != "Alarm (5 s)") return false;
            // Unknown identifiers pass through rather than vanishing.
            if (Reason("brand_new_reason") != "brand_new_reason") return false;
            if (Sound("Some.Custom.Alias") != "Some.Custom.Alias") return false;
            return true;
        }
        finally
        {
            Loc.SetLanguage(original ? "KO" : "EN");
        }
    }
}
