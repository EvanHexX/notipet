<!--
  notipet alerting instructions for agents without skill support.
  Paste the section below into AGENTS.md (Codex), CLAUDE.md (Claude Code), or
  any agent's standing instructions. Replace the path if notipet lives elsewhere
  (the installer puts `notipet` on PATH).
  An agent with the notipet skill (the Codex or Claude Code plugin, or
  `notipet install-skill`) does not need this. If your AGENTS.md mentions that
  skill, name it (`notipet`) rather than giving a path to its SKILL.md: the
  Codex plugin keeps it under plugins/cache/notipet/notipet/<version>/, so the
  path changes with every update (`notipet doctor` shows where it is now).
  The full versions are integrations/claude/skills/notipet/SKILL.md and
  integrations/codex/skills/notipet/SKILL.md.
-->

## Alerting the user (notipet)

The user runs notipet, a Windows tray app that plays a sound and shows a notification. Use it so they do not have to watch the terminal:

```
C:\src\notipet\bin\notipet.exe send --title "<project>: <what happened>" --body "<one or two lines>" --level <level> --tag "<project>:<moment>" --project "<project>" --thread-title "<this conversation, in a few words>"
```

If a skill named `notipet` is available, it is the full version of this section: use it, finding it by name. Without it, this section and the command above are all you need - never skip an alert because a skill file is missing.

Send **one** notification at the moment their attention matters:

- you need a decision, approval, or input → `--level attention`
- a long task (several minutes or more) finished → `--level success`
- you are blocked or something failed and you cannot continue → `--level error`
- something must not be missed (data-loss risk, destructive step about to run) → `--level critical`, rarely

Do not notify for short tasks, per step, or twice for the same moment. Start the title with the project name. `--project` groups the user's recent-notifications window (default: the repository you are in); keep `--thread-title` the same for every notification in one conversation. Which agent you are and the thread id are read from your environment (Claude Code or Codex) - pass `--agent` only if you run inside the other agent. Both flags are optional. Write in the language the user writes in. Never put secrets, tokens, or large diffs in the body. Send it before you stop to wait. The command always exits 0; do not retry it in a loop. If the user asks you to be quiet, stop for the rest of the session.

For a question you wait on, add `--until-reply`: the alert ends by itself when the user answers in this conversation.

In Codex, run notipet **outside the sandbox** (request escalated permissions): inside it, notipet cannot see the tray app and reports "not running" even when it is up. Call the exe directly with its arguments, not wrapped in a longer script.

When the moment you alerted about is over - the user answered (perhaps from their phone), or you fixed the problem yourself - run `C:\src\notipet\bin\notipet.exe resolve` (this conversation's alarms) or `resolve --tag "<the same tag>"`. It stops only those alarms and closes their pop-ups; if the user already did, it does nothing and still exits 0, so call it without checking.
