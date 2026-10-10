---
name: notipet
description: Alert the user on their Windows desktop through notipet - a sound plus a pop-up - when you finish a long-running task, need their input or a decision, are blocked, or hit an error they must see, and tell it when that moment is over. Use it when the user asks to be notified, pinged, or alerted, when work has run for several minutes, or right before you stop and wait for them. Do not use it for routine progress on short tasks.
---

# notipet: tell the user when they are needed (Codex)

notipet is a tray app on the user's Windows machine. It plays a sound and shows a notification. Use it so the user does not have to watch the terminal.

The command is:

```
notipet
```

(If that is not found: the installer puts notipet in `%LOCALAPPDATA%\NotipetApp\current\notipet.exe` and on PATH as `notipet`.)

## Run it outside the sandbox

notipet talks to a tray app running as the user, over `127.0.0.1`. **Inside the Codex sandbox it usually cannot see that app**: a sandboxed command may run under a different profile and without network, so it reports `not running` / `no runtime.json` even when notipet is up. So:

- Run every notipet command **outside the sandbox** - request escalated permissions for it, with a short justification such as "notify the user through the local notipet app".
- Run the executable **directly with its arguments** (the path above, then `send ...`), not wrapped in a longer PowerShell script. That is the form a user's `prefix_rule` can allow once, so later calls do not ask again.
- Never conclude notipet is down from a sandboxed result. If escalation is refused, say the alert could not be sent and carry on; do not retry in a loop.

## When to notify

Send **one** notification at the moment the user's attention actually matters:

- **You need them**: a question, a decision, approval, credentials you must not handle, or a choice between options. → `attention`
- **A long task finished**: builds, test suites, migrations, refactors, anything that ran more than a few minutes. → `success`
- **You are blocked or failed** and cannot continue without them. → `error`
- **Something is on fire** and must not be missed (data loss risk, a destructive operation about to run, production down). → `critical` - rings until acknowledged, so use it rarely.

Do **not** notify:

- for short tasks the user is plainly watching,
- after every step, file, or tool call,
- twice for the same moment. If notipet's Codex hooks are installed (below), they already announce "turn finished" (`Stop`) and "waiting for approval" (`PermissionRequest`) - only add a notification when you have something more specific to say.

## How

```
notipet send --title "<project>: <what happened>" --body "<one or two lines>" --level <level> --tag "<project>:<moment>" --project "<project>" --thread-title "<this conversation, in a few words>"
```

- `--level`: `info` | `success` | `attention` | `warn` | `error` | `critical`
- `--title`: short. Start with the project or repo name - the user may have several sessions running, and the pop-up shows only the title and body.
- `--body`: what happened and what (if anything) you need from them. One or two sentences. Plain text.
- `--tag`: a stable key for the moment, such as `<project>:needs-input` or `<project>:done`. Repeats of the same tag in this conversation within 30 seconds collapse into one sound; another conversation using the same tag is kept separate. Keep it - you need it to resolve the moment later.
- `--project`: the repository or project name. The user's recent-notifications window groups cards by it. If you leave it out, notipet uses the name of the repository you are in.
- `--thread-title`: what this conversation is about, in a few words ("auth refactor", "release 1.2 checklist"). Use the **same** words for every notification in one conversation. If the Codex app has its own name for the thread, notipet shows that instead.
- Write the title, body and thread title **in the language the user is writing to you in**.

You do not need to pass `--agent` or a thread id: notipet reads `CODEX_SESSION_ID` from your environment, and in the Codex app a card can then open this exact conversation. Add `--agent codex` only if the escalated command no longer carries your environment and the card shows up as "manual". `--project` and `--thread-title` are optional too - a notification without them still arrives, filed under "Other".

Examples:

```
notipet send --title "shop: tests passed" --body "412/412 passed; ready to merge when you are." --level success --tag "shop:done" --project shop --thread-title "checkout refactor"

notipet send --title "api: need a decision" --body "Two ways to rotate the signing keys - which do you prefer? Details in the thread." --level attention --tag "api:needs-input" --project api --thread-title "auth refactor"
```

## When it is over: resolve

An `attention`, `error` or `critical` alert can keep ringing until someone stops it - and the user may have answered from somewhere else (their phone, the Codex app), or you may have fixed the problem yourself. When the moment you alerted about **is over**, say so (outside the sandbox, like `send`):

```
notipet resolve
notipet resolve --tag "<the same tag you sent>"
```

- With no arguments it stops the alarms **this conversation** raised and closes their pop-ups. With `--tag` only that one. Nothing else is touched - other conversations' alarms keep ringing.
- Do it **as soon as** the moment is over: first thing when the user replies to something you alerted about, or right after you resolved the blocker yourself.
- It is safe to call blindly. If the user already stopped the alarm and closed the card, it does nothing and still exits 0 - do not check first, and do not retry.
- Do not resolve a moment that is still open (you are still waiting for their answer).
- A `PermissionRequest` alarm from the hooks ends by itself when your turn stops; you do not need to resolve it.

## Alerts at every turn end and approval prompt: notipet's hooks

Codex hooks ring by themselves when a turn ends (`Stop`) and when it waits for the user's approval (`PermissionRequest`) - a moment you cannot announce yourself, because you are blocked. They are not part of this skill; they live in the user's `~/.codex/hooks.json`.

- Only when the user asks for alerts like these, or asks to set notipet up: run `notipet doctor` (outside the sandbox) and look at its `codex hooks` line. If it is a warning, **ask the user first**, and only with their yes run `notipet install-hooks --codex --write`. It backs up the file, touches only notipet's own entries, and is safe to run again.
- Never set them up without the user's yes, and do not check on every task.
- Do not edit `hooks.json` or `config.toml` yourself for this - the command knows the format Codex reads. Never touch a `notify` setting.
- They work from the next new thread. Codex may ask the user to review and trust the new hooks (`/hooks`); tell them.
- To take them out: `notipet install-hooks --codex --remove`.

## Rules

- **Never put secrets, tokens, passwords, or large diffs in the body.** A notification can be read by anyone looking at the screen.
- The command always exits 0 even if notipet is not running - do not retry in a loop, and do not treat a missing notification as a reason to stop working.
- Send the notification **before** you stop to wait, not after.
- If the user says they are at their desk or asks you to be quiet, stop sending notifications for the rest of the session unless they ask again.
- Keep `critical` for things that genuinely must not be missed. It repeats until the user acknowledges it.

## Useful extras

- `notipet ping` - is notipet running? (outside the sandbox)
- `notipet desk on` / `desk off` - the user is / is not at the desk (long alarms become short while they are).
- `notipet history --limit 5` - what was sent recently.
