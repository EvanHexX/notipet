# Security

notipet listens on `127.0.0.1` only and requires a bearer token (stored in `%LOCALAPPDATA%\notipet\runtime.json`). What that protects against, and what it cannot (any process running as the same user can read the token), is written down in [docs/modules/discovery_auth.md](docs/modules/discovery_auth.md).

Please report a vulnerability privately through GitHub's **Report a vulnerability** (Security → Advisories) on this repository rather than in a public issue. Include the notipet version (`notipet version`) and steps to reproduce.

Things that are in scope:

- anything that lets a web page, another user account, or a network peer reach the local API;
- anything that turns a notification into launching something other than the two deep-link forms notipet builds itself (`codex://threads/<uuid>`, `claude://code/continue?session=local_<id>`);
- reading files outside the ones documented in `docs/api.md` (Codex `session_index.jsonl`, Claude `~/.claude/sessions/*.json`), or writing user settings from a test.
