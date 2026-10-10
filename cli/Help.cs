using System;
using System.Collections.Generic;
using System.Linq;

namespace Notipet.Cli;

// `notipet help` and `notipet help <command>` / `notipet <command> --help`.
// Kept in one table so the overview and the per-command pages cannot disagree.
internal static class Help
{
    private sealed record Entry(string Name, string Usage, string Summary, string Details);

    private static readonly Entry[] Commands =
    {
        new("send", "notipet send --title T --body B [--level L] [--tag T] [options]",
            "Send a notification.",
            """
              --title T          headline (start with the project name)
              --body B           text; `--body -` reads it from stdin
              --level L          info | success | attention | warn | error | critical
              --tag T            dedupe key: same tag, same thread, within 30 s = one sound
              --agent A          claude-code | codex | manual (alias: --source)
              --project P        group name in the recent window (default: repo of cwd)
              --thread-title T   short, stable name of this conversation/task
              --thread ID        thread/session id (alias: --session)
              --repeat M         once | repeat | until-ack
              --sound NAME       Windows alias (Notification.IM) or a library name
              --volume V         0..1, multiplied by the configured volume
              --silent           notification only, no sound
              --channels LIST    sound,notification (narrows, never enables)
              --ttl SEC          drop it if it could not be delivered in time
              --open URI         a click on the card opens this instead of the thread:
                                 http(s) on this PC, or a scheme listed in
                                 settings.json links.allowedSchemes (else ignored,
                                 with a warning). A URI, never a command
              --cwd PATH         shown on the card (default: current directory)
              --json             print the raw response
              --quiet            print nothing
              --fire-and-forget  do not wait for the response

            Run inside Claude Code or Codex, the agent and thread id are filled in
            from their environment (CLAUDE_CODE_SESSION_ID, CODEX_SESSION_ID ...),
            and a card in the recent window can open the thread in the desktop app.
            Every one of these is optional: missing ones file the card under "Other".

            Examples:
              notipet send --title "api: tests passed" --body "412/412" --level success
              notipet send --title "api: need a decision" --body "..." --level attention --project api --thread-title "auth refactor"
              dotnet test 2>&1 | notipet send --title "api: test output" --body - --level info
            """),
        new("test", "notipet test [--level L]", "Send a test notification (default: attention).", ""),
        new("status", "notipet status [--json]", "Daemon state: muted, at desk, quiet hours, alarms, sound engine.", ""),
        new("ping", "notipet ping", "One line: is the daemon up, which port, which pid.", ""),
        new("version", "notipet version", "CLI and daemon versions.", ""),
        new("doctor", "notipet doctor",
            "Check everything that matters when an alert did not arrive.",
            "Checks the daemon, the sound engine, mute/quiet hours, and whether Claude Code / Codex hooks and the skill are installed."),
        new("history", "notipet history [--limit N] [--short] [--json] | notipet history clear",
            "Show recent notifications, including ones that were held back and why.", ""),
        new("ack", "notipet ack [--id ID | --tag TAG | --all]", "Stop sounding alarms (no argument stops all).", ""),
        new("resolve", "notipet resolve [--tag TAG] [--id ID] [--thread ID] [--agent A]",
            "Say a moment you alerted about is over: stop its alarm, close its pop-up.",
            """
              (none)        inside Claude Code / Codex: every alarm this conversation raised
              --tag TAG     only the notification sent with this tag (in this conversation)
              --id ID       exactly one notification (the id `send --json` printed);
                            nothing else is added to it, not even this conversation
              --thread ID   a conversation other than the current one (alias: --session)
              --agent A     claude-code | codex | manual (default: the agent you are in)

            Only what is named, never everything (that is `ack`). Whatever the user
            already stopped or closed is skipped: "nothing to resolve" is not an
            error, so call it without checking first. Exits 0 unless --strict.

            Examples:
              notipet resolve
              notipet resolve --tag "api:needs-input"
            """),
        new("mute", "notipet mute [30m | 2h | off]", "Mute for a while, until unmuted, or turn mute off.", ""),
        new("unmute", "notipet unmute", "Same as `notipet mute off`.", ""),
        new("desk", "notipet desk [on | off | toggle]",
            "Tell notipet you are (not) at your desk. At the desk, long alarms are shortened.",
            "With no argument, prints the current state."),
        new("open", "notipet open [recent | settings]", "Open a notipet window (starts the daemon if needed).", ""),
        new("start", "notipet start", "Start the tray daemon if it is not running.", ""),
        new("stop", "notipet stop", "Quit the tray daemon and wait for it to exit.", ""),
        new("restart", "notipet restart", "Stop, then start.", ""),
        new("install-hooks", "notipet install-hooks [--claude] [--codex [--write | --remove]] [--command EXE]",
            "Print hook config for Claude Code and/or Codex, or set Codex's up.",
            """
              --codex --write   add notipet's hooks to %USERPROFILE%\.codex\config.toml (backup first,
                                only notipet's block, safe to repeat; replaces what older versions wrote)
              --codex --remove  take them out again
              --command EXE     the notipet.exe the hooks run (default: this one)
            """),
        new("install-skill", "notipet install-skill [--claude | --codex | --path DIR] [--command CMD] [--print] [--force]",
            "Install the notipet agent skill so the agent can decide when to alert you.",
            """
              --claude      %USERPROFILE%\.claude\skills\notipet\SKILL.md (default)
              --codex       %USERPROFILE%\.codex\skills\notipet\SKILL.md
              --path DIR    DIR\notipet\SKILL.md, e.g. a project's .claude\skills
              --command CMD what the skill runs (default: this exe's full path)
              --print       print the skill instead of writing it
              --force       overwrite a skill file you have edited
            """),
    };

    public static bool Has(string? name) => name is not null && Commands.Any(c => c.Name == name);

    public static void Print(string? command)
    {
        var entry = command is null ? null : Commands.FirstOrDefault(c => c.Name == command);
        if (entry is not null)
        {
            Console.WriteLine(entry.Usage);
            Console.WriteLine();
            Console.WriteLine(entry.Summary);
            if (!string.IsNullOrWhiteSpace(entry.Details))
            {
                Console.WriteLine();
                Console.WriteLine(entry.Details.TrimEnd());
            }
            return;
        }

        Console.WriteLine("notipet - tell you when an AI coding agent needs you");
        Console.WriteLine();
        Console.WriteLine("Usage: notipet <command> [options]");
        Console.WriteLine();
        foreach (var group in Groups)
        {
            Console.WriteLine(group.Title);
            foreach (var name in group.Names)
            {
                var c = Commands.First(x => x.Name == name);
                Console.WriteLine($"  {c.Name,-15}{c.Summary}");
            }
            Console.WriteLine();
        }
        Console.WriteLine("Hook mode: with no command, notipet reads an agent hook payload from stdin");
        Console.WriteLine("(Claude Code / Codex hooks) or a trailing JSON argument (Codex notify).");
        Console.WriteLine();
        Console.WriteLine("Common options:  --strict (non-zero exit on failure; default is always 0)");
        Console.WriteLine("                 --no-launch (do not start the daemon)   --json   --quiet");
        Console.WriteLine();
        Console.WriteLine("`notipet help <command>` for details. Docs: docs/CLI.md");
    }

    private static readonly (string Title, string[] Names)[] Groups =
    {
        ("Notify", new[] { "send", "test" }),
        ("Inspect", new[] { "status", "ping", "version", "doctor", "history" }),
        ("Control", new[] { "ack", "resolve", "mute", "unmute", "desk", "open" }),
        ("Daemon", new[] { "start", "stop", "restart" }),
        ("Set up", new[] { "install-hooks", "install-skill" }),
    };

    public static bool RunSelfTest()
    {
        // Every command is in exactly one overview group, and every group
        // entry exists - so a new command cannot be added and forgotten here.
        var grouped = Groups.SelectMany(g => g.Names).ToList();
        if (grouped.Count != grouped.Distinct().Count()) return false;
        if (!Commands.Select(c => c.Name).OrderBy(n => n).SequenceEqual(grouped.OrderBy(n => n))) return false;
        return Has("send") && !Has("nonsense") && !Has(null);
    }
}
