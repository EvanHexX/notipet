using System;
using System.IO;
using System.Reflection;

namespace Notipet.Cli;

// Writes the notipet agent skill into an agent's skills folder, with this
// CLI's real path filled in so the agent does not depend on PATH. Two
// templates, both embedded in this exe: integrations/claude/skills/notipet and
// integrations/codex/skills/notipet. They differ where the agents do - Codex
// runs commands in a sandbox that cannot reach the tray app, so its skill
// says to run notipet outside it.
internal static class SkillInstaller
{
    private const string Placeholder = "{{NOTIPET}}";

    public static string ClaudeSkillPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills", "notipet", "SKILL.md");

    public static string CodexSkillPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "skills", "notipet", "SKILL.md");

    public static string Render(string cliPath, bool codex = false)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(codex ? "notipet.SKILL.codex.md" : "notipet.SKILL.md")
            ?? throw new InvalidOperationException("skill template missing from the build");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace(Placeholder, cliPath);
    }

    public static int Run(string[] args)
    {
        var cli = Environment.ProcessPath ?? "notipet";
        var codex = Program.HasFlag(args, "--codex");
        var content = Render(cli, codex);

        if (Program.HasFlag(args, "--print"))
        {
            Console.WriteLine(content);
            return 0;
        }

        var target = Program.OptionValue(args, "--path") is { } dir
            ? Path.Combine(dir, "notipet", "SKILL.md")
            : codex ? CodexSkillPath() : ClaudeSkillPath();

        // Our own previous install can be replaced freely; a file the user has
        // edited is theirs and needs --force.
        if (File.Exists(target))
        {
            var existing = File.ReadAllText(target);
            if (existing == content)
            {
                Console.WriteLine($"already up to date: {target}");
                return 0;
            }
            if (!Program.HasFlag(args, "--force") && !LooksLikeOurs(existing, codex))
            {
                Console.Error.WriteLine($"notipet: {target} exists and has been edited. Use --force to overwrite.");
                return Program.HasFlag(args, "--strict") ? 1 : 0;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
        Console.WriteLine($"installed: {target}");
        Console.WriteLine("The agent picks it up in its next session. It calls:");
        Console.WriteLine($"  {cli} send ...");
        return 0;
    }

    // A previous install of ours differs only in the CLI path it names.
    private static bool LooksLikeOurs(string existing, bool codex)
    {
        var template = Render(Placeholder, codex);
        var firstLine = template.Split('\n', 2)[0];
        return existing.StartsWith(firstLine, StringComparison.Ordinal)
               && existing.Contains("name: notipet", StringComparison.Ordinal)
               && existing.Length > template.Length / 2;
    }

    public static bool RunSelfTest()
    {
        var rendered = Render(@"C:\x\notipet.exe");
        if (rendered.Contains(Placeholder, StringComparison.Ordinal)) return false;
        if (!rendered.Contains(@"C:\x\notipet.exe send", StringComparison.Ordinal)) return false;
        if (!rendered.StartsWith("---", StringComparison.Ordinal)) return false;
        if (!rendered.Contains("name: notipet", StringComparison.Ordinal)) return false;
        if (!rendered.Contains(@"C:\x\notipet.exe resolve", StringComparison.Ordinal)) return false;

        // The Codex skill is its own: same commands, plus running them outside
        // the sandbox - which the Claude one must not tell Claude to do.
        var codex = Render(@"C:\x\notipet.exe", codex: true);
        if (codex.Contains(Placeholder, StringComparison.Ordinal) || !codex.Contains("name: notipet", StringComparison.Ordinal)) return false;
        if (!codex.Contains(@"C:\x\notipet.exe resolve", StringComparison.Ordinal)) return false;
        if (!codex.Contains("outside the sandbox", StringComparison.Ordinal) || rendered.Contains("outside the sandbox", StringComparison.Ordinal)) return false;

        // Installing into a scratch folder writes the file, and re-running is
        // idempotent.
        var dir = Path.Combine(Path.GetTempPath(), "notipet-skill-" + Guid.NewGuid().ToString("N"));
        try
        {
            var outWriter = Console.Out;
            Console.SetOut(TextWriter.Null);
            try
            {
                Run(new[] { "install-skill", "--path", dir });
                Run(new[] { "install-skill", "--path", dir });
                // Our own Claude install is replaced by the Codex one without --force.
                Run(new[] { "install-skill", "--path", dir, "--codex" });
            }
            finally
            {
                Console.SetOut(outWriter);
            }
            var file = Path.Combine(dir, "notipet", "SKILL.md");
            return File.Exists(file) && File.ReadAllText(file).Contains("outside the sandbox", StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
