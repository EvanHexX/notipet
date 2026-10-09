namespace Notipet.Cli;

// The config blocks `notipet install-hooks` prints. Kept here as one source of
// truth so the printed snippets and the files under integrations/ cannot drift.
internal static class HookSnippets
{
    private static string JsonPath(string path) => path.Replace("\\", "\\\\");

    // `async: true` keeps notipet off the agent's critical path entirely: the
    // timeout is not enforced and the turn does not wait for us.
    public static string ClaudeCommandHook(string exePath) => $$"""
    {
      "hooks": {
        "Notification": [
          {
            "matcher": "agent_needs_input|agent_completed|permission_prompt|idle_prompt",
            "hooks": [
              { "type": "command", "command": "{{JsonPath(exePath)}}",
                "args": ["--source", "claude-code"], "timeout": 10, "async": true }
            ]
          }
        ],
        "Stop": [
          {
            "hooks": [
              { "type": "command", "command": "{{JsonPath(exePath)}}",
                "args": ["--source", "claude-code"], "timeout": 10, "async": true }
            ]
          }
        ],
        "SubagentStop": [
          {
            "matcher": ".*",
            "hooks": [
              { "type": "command", "command": "{{JsonPath(exePath)}}",
                "args": ["--source", "claude-code"], "timeout": 10, "async": true }
            ]
          }
        ],
        "UserPromptSubmit": [
          {
            "hooks": [
              { "type": "command", "command": "{{JsonPath(exePath)}}",
                "args": ["--source", "claude-code"], "timeout": 10, "async": true }
            ]
          }
        ]
      }
    }
    """;

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

    // Codex's modern hooks rather than its legacy `notify`: notify is a single
    // slot that something else may already own, it only ever fires on
    // agent-turn-complete, and its argv payload can overflow the Windows command
    // line. Hooks give PermissionRequest and deliver on stdin.
    //
    // UserPromptSubmit only ends things (the last turn's "finished", the idle
    // reminder) and is async, so a prompt never waits on notipet.
    public static string CodexHooks(string exePath) => $"""
    [[hooks.Stop]]
    [[hooks.Stop.hooks]]
    type = "command"
    command = '{exePath}'
    args = ["--source", "codex"]
    timeout = 10

    [[hooks.PermissionRequest]]
    [[hooks.PermissionRequest.hooks]]
    type = "command"
    command = '{exePath}'
    args = ["--source", "codex"]
    timeout = 10

    [[hooks.UserPromptSubmit]]
    [[hooks.UserPromptSubmit.hooks]]
    type = "command"
    command = '{exePath}'
    args = ["--source", "codex"]
    timeout = 10
    async = true
    """;
}
