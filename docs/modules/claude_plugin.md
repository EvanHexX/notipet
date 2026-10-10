# claude_plugin

## Purpose

Claude Code에 notipet을 붙이는 플러그인. **훅 4개와 Claude용 스킬을 한 번에** 설치한다. 사용자가 `~/.claude/settings.json`에 훅을 넣고 스킬을 따로 까는 일을 대신한다.

Codex 플러그인([codex_plugin.md](codex_plugin.md))과 달리 **훅을 플러그인에 넣는다.** Claude Code 플러그인 훅은 CLI·데스크톱 모두에서 돌고, Codex처럼 정의 해시로 신뢰를 다시 받는 절차가 없으며, 공개 디렉터리 제약도 이유가 되지 않는다(아래 Important Constraints).

플러그인은 notipet 앱을 포함하지 않는다. 앱은 설치 파일([installer.md](installer.md))로 깔고, 플러그인은 그 앱의 CLI를 PATH에서 부른다.

## Related Files

- [integrations/claude/plugin/.claude-plugin/plugin.json](../../integrations/claude/plugin/.claude-plugin/plugin.json) — 매니페스트. `version`은 앱 버전과 같다
- [integrations/claude/plugin/hooks/hooks.json](../../integrations/claude/plugin/hooks/hooks.json) — **생성물** (`notipet install-hooks --plugin`)
- [integrations/claude/plugin/skills/notipet/SKILL.md](../../integrations/claude/plugin/skills/notipet/SKILL.md) — **생성물**. 원본은 [integrations/claude/skills/notipet/SKILL.md](../../integrations/claude/skills/notipet/SKILL.md)
- [.claude-plugin/marketplace.json](../../.claude-plugin/marketplace.json) — 이 저장소를 Claude Code 마켓플레이스로 만든다
- [cli/ClaudeHooks.cs](../../cli/ClaudeHooks.cs) — 훅 정의 한 벌: 출력, settings.json 넣기·빼기(`--write`/`--remove`), 플러그인 hooks.json(`--plugin`), 자체 테스트
- [cli/Program.cs](../../cli/Program.cs) `DoctorAsync` — 플러그인과 settings.json 훅(또는 따로 깐 스킬)이 둘 다 있으면 경고
- [scripts/check-claude-plugin.ps1](../../scripts/check-claude-plugin.ps1) — 아래 제약을 기계로 확인하고 `claude plugin validate`도 돌린다. `pack.ps1`이 릴리스마다 부른다

## Public APIs

```
# 사용자 (셸, 또는 Claude Code 안에서 /plugin ...)
claude plugin marketplace add EvanHexX/notipet
claude plugin install notipet@notipet            # → ~/.claude/plugins/cache/notipet/notipet/<version>/
claude plugin marketplace update notipet         # 새 버전
claude plugin uninstall notipet@notipet

# 플러그인 없이, 또는 옮겨 갈 때
notipet install-hooks --claude --write [--command EXE]   # settings.json에 (백업, notipet 항목만, 반복해도 같음)
notipet install-hooks --claude --remove                  # settings.json에서 뺀다 — 플러그인으로 옮길 때
notipet install-hooks --claude                           # 출력만

# 유지보수
notipet install-hooks --plugin                           # 플러그인 hooks.json 내용
.\scripts\check-claude-plugin.ps1 [-Fix] [-Cli <notipet.exe>]
claude plugin marketplace add C:\src\notipet             # 작업 폴더를 그대로 마켓플레이스로
claude --plugin-dir C:\src\notipet\integrations\claude\plugin   # 설치 없이 한 세션만
```

플러그인 ID `notipet@notipet`, 스킬은 `notipet:notipet`으로 보인다. 설치하면 settings.json에 `enabledPlugins["notipet@notipet"] = true`와 `extraKnownMarketplaces.notipet`이 생긴다.

## Internal Flow

1. 이벤트(Notification: `agent_needs_input|agent_completed|permission_prompt|idle_prompt`, Stop, SubagentStop, UserPromptSubmit)가 나면 Claude Code가 플러그인 `hooks/hooks.json`의 핸들러를 실행한다.
2. 핸들러는 **exec form**: `"command": "notipet.exe", "args": ["--source", "claude-code"]`. `args`가 있으면 Claude Code는 셸 없이 `command`를 PATH에서 찾아 직접 실행한다 — Windows에서 Git Bash가 있든 없든 같다. 페이로드 JSON은 stdin.
3. `async: true`라 턴이 기다리지 않는다(타임아웃도 적용되지 않음).
4. `notipet.exe --source claude-code`가 알리거나 끝난 알람을 정리한다([cli_shim.md](cli_shim.md)).

스킬은 `notipet`(PATH)을 부르고, 없으면 설치 경로를 쓰라고 안내한다.

## State-Data Flow

```
저장소
  .claude-plugin/marketplace.json ── source ──> integrations/claude/plugin/
                                                 .claude-plugin/plugin.json
                                                 hooks/hooks.json          (생성물: install-hooks --plugin)
                                                 skills/notipet/SKILL.md   (생성물: install-skill --command notipet)
      │ claude plugin install (복사)
      ▼
%USERPROFILE%\.claude\plugins\cache\notipet\notipet\<version>\   ← ${CLAUDE_PLUGIN_ROOT}
%USERPROFILE%\.claude\settings.json
  enabledPlugins["notipet@notipet"], extraKnownMarketplaces.notipet   ← 플러그인
  hooks.{Notification,Stop,SubagentStop,UserPromptSubmit}             ← --write (플러그인과 함께 두지 않는다)
  settings.json.bak-notipet-<시각>                                     ← --write/--remove가 바꿀 때마다
```

## Important Constraints

이 PC의 Claude Code 2.1.263과 공식 문서(code.claude.com/docs: plugins-reference, hooks, plugins/marketplace-reference, 2026-10)로 확인했다.

- **플러그인 훅과 settings.json 훅은 둘 다 돈다.** 문서: 같은 핸들러가 여러 settings 파일에 있으면 한 번만 돌지만, "플러그인의 복사본은 따로 돈다". 그래서 플러그인을 쓰면 settings.json의 notipet 훅을 뺀다(`--remove`). `doctor`가 둘 다 있으면 경고하고, `--write`도 플러그인이 켜져 있으면 경고한다. 따로 깐 `~/.claude/skills/notipet`도 같은 이유로 지운다.
- **exec form(`args`)을 쓴다.** 셸 형식은 Windows에서 Git Bash, 없으면 PowerShell로 돌아 변수 문법이 달라진다. exec form은 셸이 없다. 단, exec form은 `.cmd`/`.bat` 같은 셸 스크립트를 실행하지 못하므로 `command`는 진짜 exe여야 한다. (Codex 훅에는 `args`가 없어 명령줄 하나로 쓴다 — 혼동하지 말 것.)
- **`notipet.exe`는 PATH에서 찾는다.** 플러그인 파일 하나가 모든 PC에서 쓰여야 하므로 사용자별 절대 경로를 못 넣는다. exec form은 `${CLAUDE_PLUGIN_ROOT}` 같은 자리표시자만 치환하고 `%LOCALAPPDATA%`는 펼치지 않는다. 설치 파일이 `%LOCALAPPDATA%\NotipetApp\current`를 사용자 PATH에 넣는다. 설치 직후 이미 떠 있던 Claude 앱은 새 PATH를 모를 수 있다 — 앱을 다시 시작한다.
- **`version`은 앱 버전과 같다.** Claude Code는 `version`이 바뀔 때까지 사용자를 그 버전에 묶는다. 플러그인 내용을 바꾸면 반드시 올린다. 마켓플레이스 항목에는 `version`을 쓰지 않는다(`plugin.json`이 이긴다).
- 플러그인 이름 `notipet`과 마켓플레이스 이름 `notipet`이 ID다. 바꾸면 기존 설치가 남남이 된다. `claude-`, `anthropic-`로 시작하는 이름은 예약돼 있다.
- 플러그인 루트에 `bin/`을 두지 않는다 — claude.ai와 Cowork는 그런 플러그인을 설치하지 않는다.
- `hooks/hooks.json`은 최상위 `"hooks"` 래퍼가 있어야 한다(없으면 로드 실패). 생성물이므로 손으로 고치지 않는다: [cli/ClaudeHooks.cs](../../cli/ClaudeHooks.cs)를 고치고 `check-claude-plugin.ps1 -Fix`.
- 같은 저장소의 `.agents/plugins/marketplace.json`(Codex)과 `.claude-plugin/marketplace.json`(Claude)은 공존한다. Codex는 `.agents/...`를 먼저 읽으므로(`marketplace.rs`의 경로 순서) Claude 파일을 추가해도 Codex 마켓플레이스는 그대로다 — `codex plugin list`로 확인했다.

## Known Problems

- 플러그인 훅이 `notipet.exe`를 못 찾으면(앱 미설치, PATH가 낡은 Claude 앱) 훅이 실패한다. Claude Code의 동작은 바뀌지 않지만(Notification/Stop은 막을 수 없고 async다) 알림이 없다. `notipet doctor`, Claude 앱 재시작.
- 플러그인 스킬은 `notipet`(PATH)을 부른다. 예전 절대 경로로 허용해 둔 권한 규칙은 `notipet`에 대해 한 번 더 물을 수 있다.

## Regression Notes

- `notipet --self-test`의 `ClaudeHooks`: 빈 파일에 넣기, 다른 설정·훅·순서 보존, 예전 항목(셸 형식, bin 경로) 제자리 교체, 반복, 빼기, 거부, 플러그인 파일 모양, `enabledPlugins` 판정, 임시 설정 폴더에서 끝까지.
- `.\scripts\check-claude-plugin.ps1` 모두 ok, `claude plugin validate`(플러그인·마켓플레이스) 통과.
- 격리 실행: 임시 폴더에서 `claude -p "..." --no-session-persistence --setting-sources project --plugin-dir integrations\claude\plugin` → settings.json 훅 없이 플러그인 훅만으로 `claude-code` 완료 알림.
- 전환: 플러그인 설치 → `install-hooks --claude --remove` → 스킬 폴더 삭제 → `doctor`의 `claude hooks`/`claude skill`이 `plugin notipet@notipet`.

## Rejected Approaches

- **셸 형식 + `%LOCALAPPDATA%`/`$LOCALAPPDATA` 경로**: Git Bash와 PowerShell의 변수 문법이 달라 셸에 따라 깨진다.
- **플러그인에 `.cmd` 런처를 두고 exec form으로 실행**: exec form은 `.cmd`를 실행하지 못한다(문서).
- **settings.json만 (`--write`)**: 동작은 하지만 업데이트가 플러그인처럼 따라오지 않는다. 플러그인을 못 쓰는 경우의 대안으로 남겼다.
- **플러그인과 settings.json 훅을 함께**: 두 번 운다.

## TODO

- 공개 디렉터리 제출(Anthropic) — `icon` 등 디렉터리 필드
- 스킬 권한 규칙 안내(`notipet` 허용) 문서화
