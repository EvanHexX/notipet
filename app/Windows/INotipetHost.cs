using Notipet.Core;
using Notipet.Settings;
using Notipet.Shared;

namespace Notipet.Windows;

// What the windows need from the tray controller. An interface rather than a
// bag of callbacks, so each window states its dependencies in one place and
// the controller owns every side effect (saving, trimming, tray refresh).
internal interface INotipetHost
{
    AppSettings Settings { get; }
    HistoryStore History { get; }
    System.Drawing.Icon? AppIcon { get; }
    int? ApiPort { get; }
    string Version { get; }

    // Call after mutating Settings; the host saves and redraws the tray.
    void SettingsChanged();

    // Call after Settings.Language changed; the host re-localises everything.
    void LanguageChanged();

    // Call after Settings.Theme changed; the host re-themes every open window,
    // the pop-ups and the tray menu.
    void ThemeChanged();

    void PreviewLevel(NotificationLevel level);
    void PreviewAtDeskSound();
    void StopPreview();
    void SendTest();

    void ShowHistory();
    void ShowSettings(string? page = null);

    int ClearHistory();
    bool RemoveHistoryEntry(string id);

    bool AutostartEnabled { get; }

    // Updates. UpdateStateChanged is raised on the UI thread.
    Notipet.Update.UpdateState UpdateState { get; }
    bool UpdatesSupported { get; }
    void CheckForUpdates();
    void InstallUpdate();
    event System.Action? UpdateStateChanged;
    bool SetAutostart(bool enabled);

    void OpenDataFolder();
    void OpenFolder(string path);

    // The deep link that opens this card's thread in the agent's desktop app,
    // or null when there is none (no thread id, the app is not installed, or
    // the agent has no link format). Built from validated ids only.
    System.Uri? ThreadLink(HistoryEntry entry);
    void OpenThread(HistoryEntry entry);

    // The link the sender asked the card to open (send --open), or null when
    // it gave none or the scheme is no longer allowed. It wins over the thread
    // for a click on the card.
    System.Uri? SenderLink(HistoryEntry entry);
    void OpenSenderLink(HistoryEntry entry);

    // Which projects and threads ring (Settings.Alerts, Rules/AlertScope).
    // on: true/false sets a rule, null removes it. The host saves, and the
    // open Settings page follows.
    void SetThreadAlerts(HistoryEntry entry, bool? on);
    void SetProjectAlerts(string project, bool? on);
    event System.Action? AlertScopeChanged;
}
