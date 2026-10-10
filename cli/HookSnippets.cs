namespace Notipet.Cli;

// The http-hook block `notipet install-hooks` prints. The command hooks are in
// ClaudeHooks and CodexHooks, which also write them.
internal static class HookSnippets
{
    private static string JsonPath(string path) => path.Replace("\\", "\\\\");

    // Requires server.port to be pinned in notipet's settings.json, because the
    // default is an ephemeral port and this URL has to stay put.
    public static string ClaudeHttpHook(int port) => $$"""
    {
      "hooks": {
        "Notification": [
          {
            "matcher": "agent_needs_input|agent_completed|permission_prompt|idle_prompt",
            "hooks": [
              { "type": "http", "url": "http://127.0.0.1:{{port}}/hooks/claude-code", "timeout": 5 }
            ]
          }
        ],
        "Stop": [
          {
            "hooks": [
              { "type": "http", "url": "http://127.0.0.1:{{port}}/hooks/claude-code", "timeout": 5 }
            ]
          }
        ],
        "UserPromptSubmit": [
          {
            "hooks": [
              { "type": "http", "url": "http://127.0.0.1:{{port}}/hooks/claude-code", "timeout": 5 }
            ]
          }
        ]
      }
    }
    """;

    // Codex's hooks are in CodexHooks: it writes hooks.json itself
    // (`install-hooks --codex --write`) and prints the same thing otherwise.
}
