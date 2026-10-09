---
name: notipet
description: Alert the user on their Windows desktop through notipet - a sound plus a tray notification - when you finish a long-running task, need their input or a decision, are blocked, or hit an error they must see. Use it when the user asks to be notified, pinged, or alerted, when work has run for several minutes, or right before you stop and wait for them. Do not use it for routine progress on short tasks.
---

# notipet: tell the user when they are needed

notipet is a tray app on the user's Windows machine. It plays a sound and shows a notification. Use it so the user does not have to watch the terminal.

The command is:

```
{{NOTIPET}}
```

(If the path above is empty or wrong, `notipet` may be on PATH. Run `notipet ping` once to check.)

## When to notify

Send **one** notification at the moment the user's attention actually matters:

- **You need them**: a question, a decision, approval, credentials you must not handle, or a choice between options. → `attention`
- **A long task finished**: builds, test suites, migrations, refactors, anything that ran more than a few minutes. → `success`
- **You are blocked or failed** and cannot continue without them. → `error`
- **Something is on fire** and must not be missed (data loss risk, a destructive operation about to run, production down). → `critical` - rings until acknowledged, so use it rarely.

Do **not** notify:

- for short tasks the user is plainly watching,
- after every step, file, or tool call,
- twice for the same moment. If notipet hooks are already installed, they already announce "turn finished" and "waiting for input" - only add a notification when you have something more specific to say.

## How

```
{{NOTIPET}} send --title "<project>: <what happened>" --body "<one or two lines>" --level <level> --tag "<project>:<moment>" --project "<project>" --thread-title "<this conversation, in a few words>"
```

- `--level`: `info` | `success` | `attention` | `warn` | `error` | `critical`
- `--title`: short. Start with the project or repo name - the user may have several sessions running, and the pop-up shows only the title and body.
- `--body`: what happened and what (if anything) you need from them. One or two sentences. Plain text.
- `--tag`: a stable key for the moment, such as `<project>:needs-input` or `<project>:done`. Repeats of the same tag in this conversation within 30 seconds collapse into one sound; another conversation using the same tag is kept separate.
- `--project`: the repository or project name. The user's recent-notifications window groups cards by it. If you leave it out, notipet uses the name of the repository you are in.
- `--thread-title`: what this conversation is about, in a few words ("auth refactor", "release 1.2 checklist"). Use the **same** words for every notification in one conversation, so the cards read as one thread. If the desktop app has its own name for the thread, notipet shows that instead.
- Write the title, body and thread title **in the language the user is writing to you in**.

You do not need to say which agent you are or pass a thread id: notipet reads both from your environment (Claude Code or Codex), and in their desktop apps a card can then open this exact conversation. Add `--agent claude-code` or `--agent codex` only if the user asks, or if you are running inside the other agent. `--project` and `--thread-title` are optional too - a notification without them still arrives, filed under "Other".

Examples:

```
{{NOTIPET}} send --title "notipet: tests passed" --body "17/17 checks passed; ready to publish when you are." --level success --tag "notipet:done" --project notipet --thread-title "project grouping"

{{NOTIPET}} send --title "quota-scope: need a decision" --body "Two ways to fix the tray crash - which do you prefer? Details in the terminal." --level attention --tag "quota-scope:needs-input" --project quota-scope --thread-title "tray crash fix"

{{NOTIPET}} send --title "api: migration failed" --body "Step 3 of 5 failed on a unique constraint; nothing was committed. Stopped and waiting." --level error --tag "api:blocked" --project api --thread-title "orders migration"
```

## When it is over: resolve

An `attention`, `error` or `critical` alert can keep ringing until someone stops it - and the user may have answered from somewhere else (their phone, another window), or you may have fixed the problem yourself. When the moment you alerted about **is over**, say so:

```
{{NOTIPET}} resolve
{{NOTIPET}} resolve --tag "<the same tag you sent>"
```

- With no arguments it stops the alarms **this conversation** raised and closes their pop-ups. With `--tag` only that one. Nothing else is touched - other conversations' alarms keep ringing.
- Do it **as soon as** the moment is over: first thing when the user replies to something you alerted about, or right after you resolved the blocker yourself.
- It is safe to call blindly. If the user already stopped the alarm and closed the card, it does nothing and still exits 0 - do not check first, and do not retry.
- Do not resolve a moment that is still open (you are still waiting for their answer).

## Rules

- **Never put secrets, tokens, passwords, or large diffs in the body.** A notification can be read by anyone looking at the screen.
- The command always exits 0 even if notipet is not running - do not retry in a loop, and do not treat a missing notification as a reason to stop working.
- Send the notification **before** you stop to wait, not after.
- If the user says they are at their desk or asks you to be quiet, stop sending notifications for the rest of the session unless they ask again.
- Keep `critical` for things that genuinely must not be missed. It repeats until the user acknowledges it.

## Useful extras

- `{{NOTIPET}} ping` - is notipet running?
- `{{NOTIPET}} desk on` / `desk off` - the user is / is not at the desk (long alarms become short while they are).
- `{{NOTIPET}} history --limit 5` - what was sent recently.
