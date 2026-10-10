using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Notipet.Update;

// Updates, through Velopack and the GitHub Releases of the public repository.
//
// The one place in app/ that talks to the outside world (AGENTS.md, "Local
// only"). It only ever does so when asked: the user clicks "Check for
// updates", or turned on updates.autoCheck (off by default, set in Settings
// only) and a day has passed. Checking never downloads; downloading and
// installing is always a separate click - an alarm daemon that replaced
// itself mid-alarm would lose the alarm and the Recent list with it.
//
// A build that was not installed by Velopack (bin\ from publish.ps1, a dev
// build) cannot update itself; IsInstalled says so and the UI shows that.
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class Updater
{
    public const string RepositoryUrl = "https://github.com/EvanHexX/notipet";

    // For testing an update end to end without publishing a release: a
    // folder with `vpk pack` output. Read once at start; never persisted.
    public const string FeedOverrideVariable = "NOTIPET_UPDATE_FEED";

    private readonly UpdateManager? _manager;
    private readonly SemaphoreSlim _busy = new(1, 1);

    public Updater()
    {
        try
        {
            var feed = Environment.GetEnvironmentVariable(FeedOverrideVariable);
            _manager = string.IsNullOrWhiteSpace(feed)
                ? new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false))
                : new UpdateManager(new SimpleFileSource(new System.IO.DirectoryInfo(feed)));
        }
        catch (Exception ex)
        {
            CrashLog.Write("Updater", ex);
        }
    }

    public bool IsInstalled => _manager?.IsInstalled == true;

    public UpdateState State { get; private set; } = new();

    // Raised on whatever thread finished the work.
    public event Action<UpdateState>? Changed;

    public async Task<UpdateState> CheckAsync()
    {
        if (_manager is null || !IsInstalled) return Set(State with { Phase = UpdatePhase.NotInstalled });
        if (!await _busy.WaitAsync(0).ConfigureAwait(false)) return State;
        try
        {
            Set(State with { Phase = UpdatePhase.Checking, Error = null });
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            return Set(new UpdateState
            {
                Phase = info is null ? UpdatePhase.UpToDate : UpdatePhase.Available,
                Available = info,
                AvailableVersion = info?.TargetFullRelease.Version.ToString(),
                CheckedAt = DateTimeOffset.Now
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write("Updater.Check", ex);
            return Set(State with { Phase = UpdatePhase.Failed, Error = ex.Message, CheckedAt = DateTimeOffset.Now });
        }
        finally
        {
            _busy.Release();
        }
    }

    // Downloads the update found by the last check, then hands over to
    // Velopack, which closes this process, swaps the files and starts the new
    // version. `beforeExit` runs just before that, to put things away.
    public async Task<UpdateState> DownloadAndRestartAsync(Action beforeExit)
    {
        if (_manager is null || State.Available is not { } info) return State;
        if (!await _busy.WaitAsync(0).ConfigureAwait(false)) return State;
        try
        {
            Set(State with { Phase = UpdatePhase.Downloading, Progress = 0, Error = null });
            await _manager.DownloadUpdatesAsync(info, p => Set(State with { Progress = p })).ConfigureAwait(false);
            Set(State with { Phase = UpdatePhase.Restarting });
            beforeExit();
            _manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
            return State;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Updater.Apply", ex);
            return Set(State with { Phase = UpdatePhase.Failed, Error = ex.Message });
        }
        finally
        {
            _busy.Release();
        }
    }

    private UpdateState Set(UpdateState state)
    {
        State = state;
        try { Changed?.Invoke(state); } catch (Exception ex) { CrashLog.Write("Updater.Changed", ex); }
        return state;
    }
}

internal enum UpdatePhase { Idle, NotInstalled, Checking, UpToDate, Available, Downloading, Restarting, Failed }

internal sealed record UpdateState
{
    public UpdatePhase Phase { get; init; } = UpdatePhase.Idle;
    public UpdateInfo? Available { get; init; }
    public string? AvailableVersion { get; init; }
    public int Progress { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
}
