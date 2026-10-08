using System;
using System.IO;

namespace Notipet.Shared;

// Who sent a notification and from where: the agent, the project, and the
// thread. Shared by both exes, and pure - anything that touches the file system
// is passed in - so the self-test can pin every rule.
//
// Rule: none of this may ever reject a notification. A missing or malformed
// value becomes null, and the recent window shows the card under "Other".
public static class AgentIdentity
{
    public const int MaxProject = 80;
    public const int MaxThreadId = 128;
    public const int MaxThreadTitle = 120;
    public const int MaxClient = 40;

    // One spelling per agent. An LLM writing `--source claude` must land in the
    // same rate-limit bucket, settings entry and colour as the hooks do.
    public static string NormalizeAgent(string? id)
    {
        var value = id?.Trim().ToLowerInvariant() ?? "";
        return value switch
        {
            "" => PayloadMapper.SourceManual,
            "claude" or "claude-code" or "claude_code" or "claudecode" or "claude code" => PayloadMapper.SourceClaude,
            "codex" or "openai-codex" or "codex-cli" or "openai codex" => PayloadMapper.SourceCodex,
            _ => value
        };
    }

    // A thread id ends up inside a URL, so it is validated, never truncated.
    public static bool IsThreadId(string? value)
    {
        if (value is not { Length: > 0 and <= MaxThreadId }) return false;
        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-')) return false;
        }
        return true;
    }

    public static bool IsUuid(string? value) => value is { Length: 36 } && Guid.TryParseExact(value, "D", out _);

    // Claude Desktop names its sessions local_<id>; its claude:// handler
    // accepts exactly ^local_[A-Za-z0-9-]{1,64}$.
    public static bool IsClaudeHostSession(string? value)
    {
        const string prefix = "local_";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = value.AsSpan(prefix.Length);
        if (rest.Length is < 1 or > 64) return false;
        foreach (var c in rest)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
        }
        return true;
    }

    // Display text on one line, bounded. Null when there is nothing to show.
    public static string? Clean(string? value, int max)
    {
        var flat = PayloadMapper.Summarize(value, max);
        return flat.Length == 0 ? null : flat;
    }

    // The repository a working directory belongs to, as a display name.
    //
    // gitProbe(dir) reports what `dir\.git` is: null when absent, "" for a
    // directory (a normal repository root), or the gitdir path a .git FILE
    // points at (a linked worktree or submodule). Without a probe only the pure
    // rules apply, which is what the daemon does - it never touches the disk
    // on the notification path.
    //
    // ceiling (the user's profile folder, from the CLI) is where the walk up
    // stops, like git's GIT_CEILING_DIRECTORIES: a dotfiles repository at
    // C:\Users\u must not file every non-repository folder below it under "u".
    // A walk that STARTS at the ceiling still checks it.
    public static string? ProjectFromPath(string? path, Func<string, string?>? gitProbe = null, string? ceiling = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var dir = path.Trim().TrimEnd('\\', '/');
        if (dir.Length == 0) return null;

        // Claude Desktop keeps worktrees at <repo>\.claude\worktrees\<name>;
        // the worktree's own folder name is random and means nothing.
        var worktree = IndexOfAny(dir, @"\.claude\worktrees\", "/.claude/worktrees/");
        if (worktree > 0) dir = dir[..worktree];

        if (gitProbe is not null)
        {
            // Walk up to the repository root, so an agent that cd'd into a
            // subfolder still files under its repository. Stops at the ceiling
            // (see above), and after 12 levels whatever happens.
            var current = dir;
            for (var depth = 0; depth < 12 && !string.IsNullOrEmpty(current); depth++)
            {
                if (depth > 0 && IsAtOrAbove(current, ceiling)) break;

                string? marker;
                try { marker = gitProbe(current); }
                catch { marker = null; }

                if (marker == "") return Leaf(current);
                if (marker is not null) return Leaf(MainRepoFromGitdir(marker, current) ?? current);

                var parent = ParentOf(current);
                if (parent is null || parent.Length >= current.Length) break;
                current = parent;
            }
        }

        return Leaf(dir);
    }

    // `gitdir: E:\repo\.git\worktrees\name` -> E:\repo. Anything else (a
    // submodule's ..\.git\modules\x) keeps the folder that holds the .git file.
    private static string? MainRepoFromGitdir(string gitdir, string holder)
    {
        var value = gitdir.Trim();
        if (value.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) value = value["gitdir:".Length..].Trim();
        if (value.Length == 0) return null;
        try
        {
            if (!Path.IsPathRooted(value)) value = Path.GetFullPath(Path.Combine(holder, value));
        }
        catch
        {
            return null;
        }

        var marker = IndexOfAny(value, @"\.git\worktrees\", "/.git/worktrees/");
        return marker > 0 ? value[..marker] : null;
    }

    private static string? Leaf(string dir)
    {
        var leaf = PayloadMapper.LastSegment(dir);
        // A drive root is not a project: "C:" would be a group of unrelated
        // cards. Nor is a relative "." or "..". Those belong under Other.
        if (leaf.Length == 0 || leaf is "/" or "." or ".." || (leaf.Length == 2 && leaf[1] == ':')) return null;
        return Clean(leaf, MaxProject);
    }

    // Is `dir` the ceiling itself, or one of its ancestors?
    private static bool IsAtOrAbove(string dir, string? ceiling)
    {
        if (string.IsNullOrWhiteSpace(ceiling)) return false;
        var a = dir.TrimEnd('\\', '/') + "\\";
        var b = ceiling.Trim().TrimEnd('\\', '/').Replace('/', '\\') + "\\";
        return b.StartsWith(a.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ParentOf(string dir)
    {
        var trimmed = dir.TrimEnd('\\', '/');
        var index = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        if (index <= 0) return null;
        // Keep "E:\" a root rather than turning it into "E:".
        return index == 2 && trimmed[1] == ':' ? trimmed[..3] : trimmed[..index];
    }

    private static int IndexOfAny(string value, string windows, string posix)
    {
        var index = value.IndexOf(windows, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? index : value.IndexOf(posix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool RunSelfTest()
    {
        if (NormalizeAgent("Claude") != PayloadMapper.SourceClaude) return false;
        if (NormalizeAgent(" claude_code ") != PayloadMapper.SourceClaude) return false;
        if (NormalizeAgent("CODEX") != PayloadMapper.SourceCodex) return false;
        if (NormalizeAgent(null) != PayloadMapper.SourceManual) return false;
        if (NormalizeAgent("MyBot") != "mybot") return false;

        if (!IsThreadId("019a2b3c-4d5e-7f00-8a9b-0c1d2e3f4a5b")) return false;
        if (!IsThreadId("8f3a1234567890")) return false;
        // Anything that could change a URL's meaning is refused outright.
        foreach (var bad in new[] { "", "a b", "a/b", "a?b", "a&b", "a%2F", "a#b", new string('x', MaxThreadId + 1) })
        {
            if (IsThreadId(bad)) return false;
        }

        if (!IsUuid("019a2b3c-4d5e-7f00-8a9b-0c1d2e3f4a5b")) return false;
        if (IsUuid("019a2b3c4d5e7f008a9b0c1d2e3f4a5b")) return false;
        if (IsUuid("not-a-uuid")) return false;

        if (!IsClaudeHostSession("local_6f1c2a7e-0b1d-4c55-9a77-1e2f3a4b5c6d")) return false;
        if (IsClaudeHostSession("local_")) return false;
        if (IsClaudeHostSession("local_a/b")) return false;
        if (IsClaudeHostSession("cse_abc")) return false;
        if (IsClaudeHostSession("local_" + new string('a', 65))) return false;

        // Pure rules.
        if (ProjectFromPath(@"C:\src\notipet") != "notipet") return false;
        if (ProjectFromPath(@"C:\src\notipet\") != "notipet") return false;
        if (ProjectFromPath("/home/u/proj") != "proj") return false;
        if (ProjectFromPath(@"C:\src\notipet\.claude\worktrees\brave-otter") != "notipet") return false;
        if (ProjectFromPath(@"C:\src\notipet\.claude\worktrees\brave-otter\app") != "notipet") return false;
        if (ProjectFromPath(null) is not null) return false;
        if (ProjectFromPath("   ") is not null) return false;
        // A drive root is nobody's project.
        if (ProjectFromPath(@"C:\") is not null) return false;
        if (ProjectFromPath("C:") is not null) return false;
        if (ProjectFromPath("/") is not null) return false;

        // With a probe: a subfolder files under its repository.
        string? Probe(string dir) => dir switch
        {
            @"C:\src\notipet" => "",
            @"C:\Users\u\.codex\worktrees\ab12\Shop" => @"gitdir: E:\Work\Shop\.git\worktrees\Shop",
            @"E:\Work\Lib\sub" => @"gitdir: ..\.git\modules\sub",
            _ => null
        };
        if (ProjectFromPath(@"C:\src\notipet\app\Windows", Probe) != "notipet") return false;
        // A linked worktree resolves to the main repository.
        if (ProjectFromPath(@"C:\Users\u\.codex\worktrees\ab12\Shop\src", Probe) != "Shop") return false;
        // A submodule stays itself.
        if (ProjectFromPath(@"E:\Work\Lib\sub", Probe) != "sub") return false;
        // Outside any repository: the folder itself.
        if (ProjectFromPath(@"D:\scratch\notes", Probe) != "notes") return false;
        // A probe that throws is the same as no repository.
        if (ProjectFromPath(@"D:\x\y", _ => throw new IOException("denied")) != "y") return false;

        // A versioned home folder is a ceiling, not a project for everything
        // below it - but a repository inside it, or the home folder itself
        // when you work right there, still count.
        string? Dotfiles(string dir) => dir switch
        {
            @"C:\Users\u" => "",
            @"C:\Users\u\src\shop" => "",
            _ => null
        };
        const string home = @"C:\Users\u";
        if (ProjectFromPath(@"C:\Users\u\Desktop\scratch", Dotfiles, home) != "scratch") return false;
        if (ProjectFromPath(@"C:\Users\u\src\shop\api", Dotfiles, home) != "shop") return false;
        if (ProjectFromPath(@"C:\Users\u", Dotfiles, home) != "u") return false;
        // Without a ceiling it is plain git semantics.
        if (ProjectFromPath(@"C:\Users\u\Desktop\scratch", Dotfiles) != "u") return false;

        // Relative leftovers are nobody's project either.
        if (ProjectFromPath(".") is not null || ProjectFromPath(@"..\") is not null) return false;

        if (Clean("  two\nlines  ", 50) != "two lines") return false;
        if (Clean("   ", 50) is not null) return false;
        if (Clean(new string('p', 200), MaxProject)!.Length != MaxProject) return false;
        return true;
    }
}
