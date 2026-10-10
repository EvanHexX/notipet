# codex_plugin

## Purpose

Codex에 notipet을 붙이는 플러그인. 훅 3개(턴 완료·권한 대기·새 입력)와 Codex용 스킬을 한 번에 설치한다. 사용자가 `~/.codex/config.toml`에 훅을 손으로 넣고, 스킬을 따로 설치하던 일을 대신한다.

**플러그인을 쓰면 config.toml의 notipet 훅은 필요 없다 — 지워야 한다.** Codex는 같은 이벤트의 훅을 출처(사용자 설정, 프로젝트 설정, 플러그인)와 상관없이 전부 실행하고 중복을 거르지 않는다. 둘 다 있으면 notipet이 두 번 불린다(태그가 같아 소리는 중복 병합으로 한 번이지만, 기록이 두 배가 되고 resolve도 두 번 돈다). 따로 설치한 `~/.codex/skills/notipet`도 같은 이유로 지운다.

플러그인은 notipet 앱을 포함하지 않는다. 앱은 설치 파일([installer.md](installer.md))로 깔고, 플러그인은 그 앱을 부른다.

## Related Files

- [integrations/codex/plugin/.codex-plugin/plugin.json](../../integrations/codex/plugin/.codex-plugin/plugin.json) — 매니페스트. `version`은 앱 버전과 같다
- [integrations/codex/plugin/hooks/hooks.json](../../integrations/codex/plugin/hooks/hooks.json) — 훅 정의. **고치면 모든 사용자가 훅을 다시 신뢰해야 한다** (아래 Important Constraints)
- [integrations/codex/plugin/scripts/notipet-hook.cmd](../../integrations/codex/plugin/scripts/notipet-hook.cmd) — 훅이 부르는 스크립트. notipet.exe를 찾아 그대로 넘긴다
- [integrations/codex/plugin/skills/notipet/SKILL.md](../../integrations/codex/plugin/skills/notipet/SKILL.md) — **생성물.** 원본은 [integrations/codex/skills/notipet/SKILL.md](../../integrations/codex/skills/notipet/SKILL.md)
- [.agents/plugins/marketplace.json](../../.agents/plugins/marketplace.json) — 이 저장소를 Codex 마켓플레이스로 만든다
- [scripts/check-codex-plugin.ps1](../../scripts/check-codex-plugin.ps1) — 아래 제약을 기계로 확인한다. `pack.ps1`이 릴리스마다 부른다
- [cli/SkillInstaller.cs](../../cli/SkillInstaller.cs) — `install-skill --command notipet`이 플러그인 스킬을 만든다
- [cli/Program.cs](../../cli/Program.cs) `DoctorAsync` — 플러그인과 config.toml 훅이 둘 다 있으면 경고

## Public APIs

```
# 사용자
codex plugin marketplace add EvanHexX/notipet        # 이 저장소(GitHub)를 마켓플레이스로
codex plugin add notipet@notipet                      # 설치 → ~/.codex/plugins/cache/notipet/notipet/<version>/
codex plugin marketplace upgrade notipet              # 새 버전 받기
codex plugin remove notipet@notipet                   # 제거

# 유지보수 (저장소 작업 폴더를 그대로 마켓플레이스로)
codex plugin marketplace add C:\src\notipet
codex plugin add notipet@notipet                      # 고친 뒤 다시 실행해야 캐시가 바뀐다

.\scripts\check-codex-plugin.ps1 [-Fix] [-Cli <notipet.exe>]
notipet install-skill --codex --command notipet --path integrations\codex\plugin\skills --force
```

플러그인 ID는 `notipet@notipet`(플러그인 이름 `notipet` @ 마켓플레이스 이름 `notipet`). 설정에는 `[marketplaces.notipet]`와 `[plugins."notipet@notipet"] enabled = true`가 생긴다.

## Internal Flow

1. Codex가 턴을 끝내거나(Stop), 권한을 묻거나(PermissionRequest), 사용자가 입력하면(UserPromptSubmit) 플러그인의 `hooks/hooks.json`에서 해당 훅을 찾는다.
2. Windows에서는 `commandWindows`를 쓴다. 그 안의 `${PLUGIN_ROOT}`를 설치된 플러그인 폴더 경로로 **글자 그대로 치환**하고, `%ComSpec% /C "<command>"`로 실행한다. 페이로드 JSON은 stdin으로 들어온다.
3. `notipet-hook.cmd`가 notipet.exe를 찾는다: `NOTIPET_CLI`(개발 빌드용) → `%LOCALAPPDATA%\NotipetApp\current\notipet.exe`(설치판) → PATH의 `notipet.exe`. 못 찾으면 아무것도 출력하지 않고 0으로 끝난다.
4. `notipet.exe --source codex`가 stdin 페이로드를 읽어 알리거나 끝난 알람을 정리한다([cli_shim.md](cli_shim.md)). 훅 모드는 stdout에 아무것도 쓰지 않는다.

스킬은 Codex가 플러그인 스킬로 읽는다. 스킬은 `notipet`(PATH)을 부르고, 없으면 설치 경로를 쓰라고 안내한다. 한 파일이 모든 PC에서 써야 하므로 사용자별 절대 경로를 넣을 수 없다.

## State-Data Flow

```
저장소
  .agents/plugins/marketplace.json  ── source.path ──>  integrations/codex/plugin/
                                                          .codex-plugin/plugin.json   (name, version, skills, interface)
                                                          hooks/hooks.json            (Stop, PermissionRequest, UserPromptSubmit)
                                                          scripts/notipet-hook.cmd
                                                          skills/notipet/SKILL.md     (생성물)
                                                          assets/icon.png
       │ codex plugin add (복사)
       ▼
%USERPROFILE%\.codex\plugins\cache\notipet\notipet\<version>\     ← ${PLUGIN_ROOT}. Codex는 여기서 읽는다(원본이 아니라)
%USERPROFILE%\.codex\config.toml
  [marketplaces.notipet]            source_type, source
  [plugins."notipet@notipet"]       enabled
  [hooks.state."notipet@notipet:hooks/hooks.json:stop:0:0"]   trusted_hash   ← 사용자가 신뢰하면 생긴다
```

## Important Constraints

아래는 Codex 소스(openai/codex `main`, 2026-10)와 이 PC의 Codex 데스크톱(codex-cli 0.162.0-alpha.17.2)으로 확인한 것이다. Codex가 바뀌면 여기부터 다시 확인한다. 근거 파일은 Known Problems 아래 "확인한 곳"에 있다.

**형식**
- **매니페스트는 `.codex-plugin/plugin.json`(레거시 형식)이어야 한다.** 플러그인 루트에 `plugin.json`이 있으면 Codex는 이식형(Agent Plugin) 형식으로 읽고, 그 형식은 **훅을 하나도 읽지 않는다**(`loader.rs`: `PluginManifestFormat::AgentPlugin`이면 hook source가 빈 목록). 공식 문서는 새 패키지에 이식형을 권하지만, 훅이 필요한 notipet은 따를 수 없다.
- 훅 파일 기본 위치는 `hooks/hooks.json`. 매니페스트에 `hooks`를 적으면 기본 위치 대신 그것만 읽는다(더해지지 않는다). 우리는 적지 않는다.
- `hooks.json` 최상위에는 `description`과 `hooks`만 올 수 있다(`deny_unknown_fields`). 다른 키가 있으면 **파일 전체가 거부된다**.
- 핸들러 필드는 `type`, `command`, `commandWindows`(별칭 `command_windows`), `timeout`(초), `async`, `statusMessage`, `additionalContextLimit`뿐이다. **`args` 필드는 없고, 있어도 경고 없이 버려진다.** 인자는 명령줄 안에 쓴다. (예전 `install-hooks` 출력과 이 PC의 config.toml이 `args`를 썼는데, 버려지고 있었다. 그래도 동작한 것은 CLI가 페이로드 모양으로 Codex를 알아보기 때문이다.)
- Windows에서는 `commandWindows`가 있으면 `command` 대신 쓴다. `command`는 `"true"`로 둔다 — notipet은 Windows 전용이고, 다른 OS에서 설치돼도 조용히 아무것도 하지 않게.

**실행**
- 명령은 `%ComSpec% /C "<명령줄>"`(보통 cmd.exe)로, 세션의 cwd에서, 세션 환경을 다시 채운 새 환경으로, 창 없이 실행된다(`command_runner.rs`). 그래서 `%LOCALAPPDATA%` 같은 cmd 변수는 펼쳐진다.
- `${PLUGIN_ROOT}`, `${PLUGIN_DATA}`(그리고 `${CLAUDE_PLUGIN_ROOT}`, `${CLAUDE_PLUGIN_DATA}`)는 Codex가 명령 문자열에서 **글자 그대로** 바꾼다. 환경 변수로도 들어온다. 경로에 공백이 있을 수 있으니 따옴표로 감싼다: `"\"${PLUGIN_ROOT}\\scripts\\notipet-hook.cmd\" --source codex"`.
- 같은 이벤트의 훅은 모든 출처에서 모아 **동시에** 실행한다. 순서 보장도, 중복 제거도 없다.
- 타임아웃 기본은 600초. 우리는 10초. `async = true`는 턴을 기다리게 하지 않는다(UserPromptSubmit에만).
- Stop·PermissionRequest 훅의 stdout은 Codex가 결정(JSON)으로 읽을 수 있다. 그래서 스크립트와 CLI 훅 모드는 stdout에 아무것도 쓰지 않는다.
- `.cmd`는 CRLF여야 한다(LF면 cmd.exe가 레이블·블록을 잘못 읽는다). `.gitattributes`가 `*.cmd`를 CRLF로 고정한다. ASCII만 쓴다(cmd는 배치 파일을 콘솔 코드 페이지로 읽는다).

**신뢰(trust) — 업데이트 때 가장 중요한 것**
- Codex는 훅마다 **키**와 **해시**를 둔다. 키는 `notipet@notipet:hooks/hooks.json:<event>:<그룹 번호>:<핸들러 번호>`, 해시는 이벤트·matcher·핸들러 설정(명령, timeout, async, statusMessage — Windows에서는 `commandWindows`가 `command` 자리에 들어간 뒤)의 정규화 값이다(`discovery.rs` `hook_hash`). 사용자가 신뢰하면 `config.toml`의 `[hooks.state."<키>"] trusted_hash`에 저장된다.
- 그러므로 **`hooks.json`의 그 어떤 글자를 바꿔도**(상태 메시지 문구, 타임아웃, 이벤트 추가, 순서 변경) 해시나 키가 달라져 그 훅은 "modified"/"untrusted"가 되고, **사용자가 다시 신뢰할 때까지 실행되지 않는다.** notipet이 조용히 멈추는 것이 사용자에게 보이는 증상이다.
- 해시에는 `${PLUGIN_ROOT}` 치환 **전의** 문자열이 들어가고, 키에는 버전이 없다. 그래서 **플러그인 버전만 올라가면 다시 신뢰할 필요가 없다.** 스크립트(`notipet-hook.cmd`) 내용도 해시에 들어가지 않는다 — 바뀔 수 있는 로직은 스크립트에 둔다.
- `check-codex-plugin.ps1`이 `hooks.json`의 SHA-256을 기록해 두고, 바뀌면 실패한다. 의도한 변경이면 스크립트의 값을 갱신하고 **릴리스 노트에 "Codex 훅을 다시 신뢰해야 합니다"를 쓴다.**
- 신뢰 검토는 Codex CLI의 `/hooks` 또는 데스크톱 앱의 훅 설정에서 한다. `codex exec --dangerously-bypass-hook-trust`는 그 실행에서만 건너뛴다.

**설치·업데이트**
- `codex plugin add`는 플러그인 폴더를 `~/.codex/plugins/cache/<marketplace>/<plugin>/<version>/`로 **복사**한다. Codex는 원본이 아니라 이 복사본을 읽는다. 저장소를 고쳐도 다시 `add`(또는 Git 마켓플레이스면 `marketplace upgrade`)하기 전에는 바뀌지 않는다.
- 캐시 폴더가 버전별이므로 **플러그인 내용을 바꾸면 `version`을 올린다.** notipet은 `plugin.json`의 `version`을 앱 버전(csproj `<Version>`)과 같게 둔다. 점검 스크립트가 확인한다.
- 플러그인 이름 `notipet`과 마켓플레이스 이름 `notipet`을 바꾸면 플러그인 ID가 달라져 기존 설치·신뢰가 모두 남남이 된다. 바꾸지 않는다.
- 마켓플레이스 `source.path`는 저장소 루트 기준, `./`로 시작, 루트 밖으로 못 나간다.
- 공식 문서상 훅이 든 플러그인은 공개 플러그인 디렉터리에 올릴 수 없다. 배포는 이 저장소를 마켓플레이스로 추가하는 방식뿐이다.

**훅 대상**
- notipet이 쓰는 이벤트: `Stop`(완료 알림 + 이 스레드 권한 알람 정리), `PermissionRequest`(권한 대기 알람), `UserPromptSubmit`(지난 턴 완료 알림 정리, async). 무엇을 알리고 무엇을 끝내는지는 `PayloadMapper.PlanForHookEvent`가 정한다 — 이벤트를 더하거나 빼려면 그쪽과 함께 고친다.
- Codex의 레거시 `notify`는 건드리지 않는다(AGENTS.md). 다른 것이 이미 쓰고 있을 수 있는 단일 슬롯이다.

## Known Problems

- **`codex exec`(CLI)는 플러그인 훅을 실행하지 않았다.** 이 PC(0.162.0-alpha.17.2)에서 신뢰를 넣어 주거나 `--dangerously-bypass-hook-trust`를 줘도 플러그인 훅은 돌지 않았고, 같은 조건에서 config.toml 훅은 돌았다. 공식 문서도 "플러그인 훅은 데스크톱 앱에 수동 설치한 플러그인에서만 지원"이라고 한다. CLI만 쓰는 사용자는 config.toml 훅(`notipet install-hooks --codex`)을 쓴다.
- **신뢰 표시와 실제 실행이 어긋난다.** `hooks/list`(app-server)는 이 PC의 config.toml 훅을 "untrusted"로 보여 주는데, 데스크톱 앱에서는 실제로 실행됐다. 데스크톱 앱이 신뢰를 어떻게 다루는지는 공개되지 않았다. 플러그인 훅이 안 울리면 먼저 신뢰부터 확인한다.
- 플러그인 스킬은 `notipet`(PATH)을 부른다. 설치 직후 이미 떠 있던 Codex는 바뀐 PATH를 모를 수 있다 — Codex를 다시 시작하거나 스킬 안내대로 설치 경로를 쓴다. 사용자가 예전 절대 경로로 허용해 둔 `prefix_rule`은 `notipet`에 대해 한 번 더 물을 수 있다.
- 이 PC의 Codex 레거시 `notify`(computer-use)는 `os error 206`(명령줄이 너무 김)으로 실패하고 있다(openai/codex#25141). notipet과 무관하지만, `notify`를 쓰지 않는 이유가 실제로 재현된 것이다.

확인한 곳:
- 문서: <https://learn.chatgpt.com/docs/plugins>, <https://learn.chatgpt.com/docs/hooks>, <https://developers.openai.com/plugins/build/plugins>
- 소스(openai/codex): `codex-rs/config/src/hook_config.rs`(필드), `codex-rs/hooks/src/engine/command_runner.rs`(cmd /C 실행), `codex-rs/hooks/src/engine/discovery.rs`(`${…}` 치환, `hook_hash`, 신뢰 판정), `codex-rs/core-plugins/src/loader.rs`(`hooks/hooks.json` 기본값, 이식형은 훅 없음), `codex-rs/core-plugins/src/manifest.rs`(매니페스트 필드)
- 이 PC: `codex plugin marketplace add/add` 결과, app-server `hooks/list` 응답, `codex exec --ephemeral` 시험

## Regression Notes

- `.\scripts\check-codex-plugin.ps1` — 모두 ok. (`pack.ps1`이 같은 검사를 하므로 릴리스 전에 반드시 한 번 돈다.)
- 설치: `codex plugin marketplace add <저장소>` → `codex plugin add notipet@notipet` → 캐시 폴더에 다섯 파일, config.toml에 `[plugins."notipet@notipet"]`.
- 실제 동작: 데스크톱 앱에서 새 스레드 → 짧은 요청 → 완료 알림이 오고, `notipet history`에 `codex` 출처로 남는다. 권한이 필요한 명령을 시켜 권한 대기 알람 → 승인하면 턴이 끝나며 알람이 멈춘다.
- `notipet doctor`의 `codex hooks`가 `plugin …\hooks\hooks.json`. config.toml에도 notipet 훅이 있으면 경고.
- notipet을 제거한 PC에서 훅이 돌아도 Codex에 오류가 보이지 않는다(스크립트가 조용히 0).

## Rejected Approaches

- **hooks.json에서 notipet.exe를 직접 부르기** (`"%LOCALAPPDATA%\\NotipetApp\\current\\notipet.exe" --source codex`): 동작은 하지만 경로나 찾는 순서를 바꿀 때마다 `hooks.json`이 바뀌어 모든 사용자가 다시 신뢰해야 한다. 설치하지 않은 PC에서는 cmd가 "인식할 수 없는 명령" 오류를 낸다.
- **PATH의 `notipet`만 부르기**: 설치 직후 이미 떠 있던 Codex는 새 PATH를 모른다.
- **플러그인 안에 notipet.exe를 넣기**: 앱(트레이 데몬)과 CLI 버전이 따로 놀고, 업데이트 경로가 둘이 된다. 플러그인은 설치된 앱을 부르기만 한다.
- **이식형 매니페스트(루트 `plugin.json`)**: 훅을 읽지 않는다.
- **config.toml 훅과 함께 쓰기**: 모든 훅이 두 번 돈다.

## TODO

- 데스크톱 앱이 플러그인 훅 신뢰를 어떻게 다루는지 공식 문서가 나오면 Known Problems 정리
- `Interrupt` 이벤트(사용자가 턴을 끊음)로 그 스레드 알람 정리 — `PayloadMapper`와 함께, 그리고 신뢰 재요청을 감수할 가치가 있을 때
- Claude Code 플러그인
