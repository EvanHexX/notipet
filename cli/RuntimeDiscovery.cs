using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Notipet.Shared;

namespace Notipet.Cli;

internal sealed record Daemon(RuntimeInfo Info)
{
    public string BaseUrl => Info.BaseUrl;
}

// Finds the running daemon via %LOCALAPPDATA%\notipet\runtime.json, validates
// that the file is not a leftover from a crash, and optionally starts one.
internal static class RuntimeDiscovery
{
    private const string AppFolderName = "notipet";

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    // The session-scoped file comes first: with two logon sessions (console plus
    // RDP) there are two daemons sharing one %LOCALAPPDATA%, and a CLI should
    // talk to the one in its own session.
    private static string[] CandidatePaths()
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        return new[]
        {
            Path.Combine(DataDir, $"runtime-{sessionId}.json"),
            Path.Combine(DataDir, "runtime.json")
        };
    }

    public static RuntimeInfo? ReadFile()
    {
        foreach (var path in CandidatePaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var info = JsonSerializer.Deserialize(File.ReadAllText(path), NotipetJson.Compact.RuntimeInfo);
                if (info is not null) return info;
            }
            catch
            {
                // A corrupt or half-written file is simply not a daemon.
            }
        }
        return null;
    }

    // Four escalating checks. The third is the one that matters: a live process
    // whose start time does not match ours is PID reuse, not our daemon, and
    // talking to it would mean talking to an unrelated program.
    public static async Task<Daemon?> FindAsync(TimeSpan timeout)
    {
        var info = ReadFile();
        if (info is null) return null;
        if (info.Pid <= 0) return null;

        try
        {
            using var process = Process.GetProcessById(info.Pid);
            if (process.HasExited) return null;

            if (DateTimeOffset.TryParse(info.ProcessStartTimeUtc, out var recorded))
            {
                var actual = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                if (Math.Abs((actual - recorded).TotalSeconds) > 1) return null;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch
        {
            // Cannot inspect it (access denied); let the health probe decide.
        }

        var health = await GetAsync(info, "/v1/health", NotipetJson.Compact.HealthResponse, timeout).ConfigureAwait(false);
        if (health is null) return null;
        // A different instance means the file describes a daemon that is gone
        // and something else now holds the port.
        if (!string.Equals(health.InstanceId, info.InstanceId, StringComparison.Ordinal)) return null;

        return new Daemon(info);
    }

    // Starting the daemon from a hook is convenient, and the alternative -
    // losing the first notification after every reboot - is worse. --no-launch
    // exists for anyone who disagrees.
    public static async Task<Daemon?> FindOrLaunchAsync(bool allowLaunch, TimeSpan budget)
    {
        var found = await FindAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        if (found is not null) return found;
        if (!allowLaunch) return null;

        var exePath = DaemonPath();
        if (exePath is null) return null;

        try
        {
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        }
        catch
        {
            return null;
        }

        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(150).ConfigureAwait(false);
            var daemon = await FindAsync(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
            if (daemon is not null) return daemon;
        }
        return null;
    }

    public static async Task<T?> GetAsync<T>(RuntimeInfo info, string path, JsonTypeInfo<T> typeInfo, TimeSpan timeout)
        where T : class
    {
        try
        {
            using var client = CreateClient(info, timeout);
            using var response = await client.GetAsync(path).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize(body, typeInfo);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<(bool Ok, int Status, string Body)> PostAsync<TRequest>(
        RuntimeInfo info, string path, TRequest payload, JsonTypeInfo<TRequest> typeInfo, TimeSpan timeout)
    {
        try
        {
            using var client = CreateClient(info, timeout);
            var json = JsonSerializer.Serialize(payload, typeInfo);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(path, content).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (response.IsSuccessStatusCode, (int)response.StatusCode, body);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    // Any verb, optional JSON body. For the routes that take no payload
    // (DELETE /v1/history, POST /v1/shutdown).
    public static async Task<(bool Ok, int Status, string Body)> SendAsync(
        RuntimeInfo info, HttpMethod method, string path, string? json, TimeSpan timeout)
    {
        try
        {
            using var client = CreateClient(info, timeout);
            using var request = new HttpRequestMessage(method, path);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (response.IsSuccessStatusCode, (int)response.StatusCode, body);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    // Where the daemon executable lives. To launch one, the daemon shipped
    // next to this CLI wins: runtime.json may be stale (the daemon was killed)
    // and point at an old dev build, and a publish must start what it
    // published. To describe the running one (doctor), its recorded path wins.
    public static string? DaemonPath(bool preferRunning = false)
    {
        var recorded = ReadFile()?.ExePath;
        var beside = Path.Combine(AppContext.BaseDirectory, "NotipetTray.exe");
        if (preferRunning && !string.IsNullOrWhiteSpace(recorded) && File.Exists(recorded)) return recorded;
        if (File.Exists(beside)) return beside;
        if (!string.IsNullOrWhiteSpace(recorded) && File.Exists(recorded)) return recorded;

        // Left by the daemon on its last start; survives it exiting.
        try
        {
            var breadcrumb = Path.Combine(DataDir, "daemon.path");
            var last = File.Exists(breadcrumb) ? File.ReadAllText(breadcrumb).Trim() : null;
            if (!string.IsNullOrWhiteSpace(last) && File.Exists(last)) return last;
        }
        catch
        {
        }
        return null;
    }

    // Pinned to the loopback base URL from runtime.json. The CLI has no other
    // HTTP destination, by design.
    private static HttpClient CreateClient(RuntimeInfo info, TimeSpan timeout)
    {
        var client = new HttpClient { BaseAddress = new Uri(info.BaseUrl), Timeout = timeout };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", info.Token);
        return client;
    }

    public static bool RunSelfTest()
    {
        // DataDir must be under LOCALAPPDATA and named for the app.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!DataDir.StartsWith(local, StringComparison.OrdinalIgnoreCase)) return false;
        if (!DataDir.EndsWith(AppFolderName, StringComparison.OrdinalIgnoreCase)) return false;

        // The session-scoped path is preferred over the generic one.
        var candidates = CandidatePaths();
        if (candidates.Length != 2) return false;
        if (!candidates[0].Contains("runtime-", StringComparison.Ordinal)) return false;
        if (!candidates[1].EndsWith("runtime.json", StringComparison.Ordinal)) return false;

        // Finding nothing must be a null, never an exception, even when the
        // daemon has never run on this machine.
        var task = FindAsync(TimeSpan.FromMilliseconds(200));
        if (!task.Wait(TimeSpan.FromSeconds(5))) return false;

        return true;
    }
}
