using System;
using Microsoft.Win32;
using Notipet.Shared;

namespace Notipet.Core;

// The deep link that opens a notification's thread in the agent's desktop app.
//
//   Codex:  codex://threads/<uuid>                  documented (learn.chatgpt.com
//           commands reference); the id is the hook session_id / notify
//           thread-id. The ChatGPT desktop app keeps the codex:// scheme.
//   Claude: claude://code/continue?session=local_<id>  undocumented: read from
//           the Claude Desktop handler (v2.26454). The id is Desktop's own
//           session id (CLAUDE_CODE_HOST_SESSION_ID), NOT the hook session_id.
//
// Security: the URL is built here from ids that were validated on arrival;
// nothing from the wire is ever used as a URL or a scheme. Any local process
// with the token can post a notification, and a click must not turn that into
// launching an arbitrary protocol, or a codex:// link that pre-fills a prompt
// (?prompt=) or opens settings. No http(s) either: notipet has no network.
//
// Deliberately not used: claude://resume?session=<cli id>. It imports a CLI
// session into Desktop, auto-trusts its folder and un-archives it - side
// effects nobody expects from clicking a notification.
internal static class ThreadLinks
{
    public const string CodexScheme = "codex";
    public const string ClaudeScheme = "claude";

    public static Uri? For(string agent, string? threadId, string? hostSession)
    {
        if (agent == PayloadMapper.SourceCodex && AgentIdentity.IsUuid(threadId))
        {
            return new Uri($"{CodexScheme}://threads/{threadId!.ToLowerInvariant()}");
        }
        if (agent == PayloadMapper.SourceClaude && AgentIdentity.IsClaudeHostSession(hostSession))
        {
            return new Uri($"{ClaudeScheme}://code/continue?session={Uri.EscapeDataString(hostSession!)}");
        }
        return null;
    }

    // A link is only offered when something will answer it. Without a
    // registered handler, Windows shows "get an app to open this link".
    public static bool IsSchemeRegistered(string scheme)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(scheme);
            return key?.GetValue("URL Protocol") is not null;
        }
        catch
        {
            return false;
        }
    }

    public static bool RunSelfTest()
    {
        const string uuid = "019A2B3C-4D5E-7F00-8A9B-0C1D2E3F4A5B";

        var codex = For(PayloadMapper.SourceCodex, uuid, null);
        if (codex?.OriginalString != "codex://threads/019a2b3c-4d5e-7f00-8a9b-0c1d2e3f4a5b") return false;
        // Not a UUID: no link, rather than a link to nothing.
        if (For(PayloadMapper.SourceCodex, "8f3a1234567890", null) is not null) return false;
        if (For(PayloadMapper.SourceCodex, null, null) is not null) return false;

        var claude = For(PayloadMapper.SourceClaude, uuid, "local_6f1c2a7e-0b1d-4c55-9a77-1e2f3a4b5c6d");
        if (claude?.OriginalString != "claude://code/continue?session=local_6f1c2a7e-0b1d-4c55-9a77-1e2f3a4b5c6d") return false;
        // The CLI session id alone is not enough for Claude Desktop.
        if (For(PayloadMapper.SourceClaude, uuid, null) is not null) return false;
        if (For(PayloadMapper.SourceClaude, uuid, "local_a&prompt=x") is not null) return false;

        // Other agents and mismatched ids never produce a link.
        if (For(PayloadMapper.SourceManual, uuid, "local_abc") is not null) return false;
        if (For("mybot", uuid, null) is not null) return false;
        if (For(PayloadMapper.SourceCodex, "local_abc", null) is not null) return false;

        // Never anything that reaches the network.
        foreach (var link in new[] { codex, claude })
        {
            if (link!.Scheme is "http" or "https") return false;
        }
        return true;
    }
}
