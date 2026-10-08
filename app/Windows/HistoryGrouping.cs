using System;
using System.Collections.Generic;
using System.Linq;
using Notipet.Core;

namespace Notipet.Windows;

// One project's cards in the recent window.
internal sealed record HistoryGroup(string Key, string? Project, IReadOnlyList<HistoryEntry> Entries, IReadOnlyList<string> Paths)
{
    public bool IsOther => Project is null;
}

// Splits the recent list into projects. Pure, so the self-test can pin the
// ordering rules without opening a window.
internal static class HistoryGrouping
{
    // A key no real project name can produce, so a repository literally called
    // "Other" (or "기타") never merges with the cards that have no project.
    public const string OtherKey = "\0other";

    // Groups keep the store's order: the group with the newest card first,
    // and newest-first inside each group. "Other" always goes last - it is
    // the leftovers, not a project. Case does not split a project: an LLM
    // writing "Notipet" and a hook deriving "notipet" are the same repository.
    public static IReadOnlyList<HistoryGroup> Group(IReadOnlyList<HistoryEntry> entries)
    {
        var order = new List<string>();
        var byKey = new Dictionary<string, (string? Name, List<HistoryEntry> Items, List<string> Paths)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var name = string.IsNullOrWhiteSpace(entry.Envelope.Project) ? null : entry.Envelope.Project!.Trim();
            var key = name ?? OtherKey;
            if (!byKey.TryGetValue(key, out var group))
            {
                group = (name, new List<HistoryEntry>(), new List<string>());
                byKey[key] = group;
                order.Add(key);
            }
            group.Items.Add(entry);

            var cwd = entry.Envelope.SourceCwd;
            if (!string.IsNullOrWhiteSpace(cwd) && !group.Paths.Contains(cwd!, StringComparer.OrdinalIgnoreCase)) group.Paths.Add(cwd!);
        }

        return order
            .OrderBy(key => key == OtherKey ? 1 : 0)   // stable: keeps first-seen order otherwise
            .Select(key => new HistoryGroup(key, byKey[key].Name, byKey[key].Items, byKey[key].Paths))
            .ToList();
    }

    public static bool RunSelfTest()
    {
        HistoryEntry E(string? project, string title, string? cwd = null) => new()
        {
            Envelope = new NotificationEnvelope { Title = title, Project = project, SourceCwd = cwd }
        };

        // Newest first, as the store hands them over.
        var list = new[]
        {
            E("shop", "s3", @"E:\Work\shop"),
            E(null, "o2"),
            E("Notipet", "n2", @"C:\src\notipet"),
            E("Other", "literal other"),
            E("shop", "s2", @"E:\Work\shop\api"),
            E("notipet", "n1", @"C:\src\notipet"),
            E("  ", "o1"),
        };
        var groups = Group(list);

        // shop (newest card) -> Notipet -> the repo called "Other" -> leftovers.
        if (groups.Count != 4) return false;
        if (groups[0].Project != "shop" || groups[1].Project != "Notipet" || groups[2].Project != "Other") return false;
        if (!groups[3].IsOther || groups[2].IsOther) return false;

        // Case does not split a project; order inside a group is preserved.
        if (groups[1].Entries.Count != 2 || groups[1].Entries[0].Envelope.Title != "n2") return false;
        // Blank counts as missing.
        if (groups[3].Entries.Count != 2 || groups[3].Entries[0].Envelope.Title != "o2") return false;
        // Distinct folders are kept for the header tooltip.
        if (groups[0].Paths.Count != 2 || groups[1].Paths.Count != 1) return false;

        // Unnamed threads are labelled by the random END of their id: two Codex
        // (UUIDv7) threads from the same minute share their first 8 digits.
        if (HistoryWindow.ShortId("0199a213-81c0-7800-8a9b-0c1d2e3f4a5b") == HistoryWindow.ShortId("0199a213-9f44-7a11-9d00-77aa66bb55cc")) return false;
        if (HistoryWindow.ShortId("0199a213-81c0-7800-8a9b-0c1d2e3f4a5b") != "#2e3f4a5b") return false;
        if (HistoryWindow.ShortId("s1") != "#s1" || HistoryWindow.ShortId(null) is not null) return false;

        // Only leftovers: a single Other group. Nothing: no groups.
        if (Group(new[] { E(null, "x") }) is not [{ IsOther: true }]) return false;
        if (Group(Array.Empty<HistoryEntry>()).Count != 0) return false;
        return true;
    }
}
