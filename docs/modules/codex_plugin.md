# codex_plugin

## Purpose

Codex에 notipet을 붙이는 두 부분을 다룬다.

1. **플러그인 (스킬만)** — `integrations/codex/plugin`. Codex가 스스로 판단해 알리고(`send`), 끝난 알람을 끄게(`resolve`) 하는 스킬. 공개 플러그인 목록에 올릴 수 있는 형태(훅·앱·MCP 없음)를 유지한다.
2. **훅** — `notipet install-hooks --codex --write`가 `~/.codex/config.toml`에 notipet 블록을 넣는다. 턴 완료(`Stop`)와 승인 대기(`PermissionRequest`)는 에이전트가 멈춰 있는 순간이라 스킬로는 알릴 수 없고 훅만 잡는다. 스킬은 사용자가 이런 알림을 원할 때 **동의를 받아** 이 명령을 실행하라고 안내한다.

플러그인도 훅도 notipet 앱을 포함하지 않는다. 앱은 설치 파일([installer.md](installer.md))로 깔고, 둘 다 그 앱의 CLI(`notipet.exe`)를 부른다.

## Related Files

- [integrations/codex/plugin/.codex-plugin/plugin.json](../../integrations/codex/plugin/.codex-plugin/plugin.json) — 매니페스트. `version`은 앱 버전과 같다
- [integrations/codex/plugin/skills/notipet/SKILL.md](../../integrations/codex/plugin/skills/notipet/SKILL.md) — **생성물.** 원본은 [integrations/codex/skills/notipet/SKILL.md](../../integrations/codex/skills/notipet/SKILL.md)
- [.agents/plugins/marketplace.json](../../.agents/plugins/marketplace.json) — 이 저장소를 Codex 마켓플레이스로 만든다
- [cli/CodexHooks.cs](../../cli/CodexHooks.cs) — 훅 블록, config.toml 넣기·빼기, 예전 위치 정리, 자체 테스트
- [cli/Program.cs](../../cli/Program.cs) — `install-hooks`, `doctor`의 `codex hooks` 줄, `RestoreSystemRoot`
- [cli/SkillInstaller.cs](../../cli/SkillInstaller.cs) — `install-skill --command notipet`이 플러그인 스킬을 만든다
- [scripts/check-codex-plugin.ps1](../../scripts/check-codex-plugin.ps1) — 아래 제약을 기계로 확인한다. `pack.ps1`이 릴리스마다 부른다

## Public APIs

```
# 플러그인 (사용자)
codex plugin marketplace add EvanHexX/notipet        # 이 저장소(GitHub)를 마켓플레이스로
codex plugin add notipet@notipet                      # → ~/.codex/plugins/cache/notipet/notipet/<version>/
codex plugin marketplace upgrade notipet              # 새 버전
codex plugin remove notipet@notipet

# 훅
notipet install-hooks --codex --write [--command EXE]   # config.toml에 notipet 블록 (백업, 반복해도 같음)
notipet install-hooks --codex --remove                  # 다시 뺀다
notipet install-hooks --codex                           # 넣을 블록을 출력만
notipet doctor                                          # [ok] codex hooks ...config.toml

# 유지보수
codex plugin marketplace add C:\src\notipet           # 작업 폴더를 그대로 마켓플레이스로
codex plugin add notipet@notipet                      # 고친 뒤 다시 실행해야 캐시가 바뀐다
.\scripts\check-codex-plugin.ps1 [-Fix] [-Cli <notipet.exe>]
```

플러그인 ID는 `notipet@notipet`(플러그인 `notipet` @ 마켓플레이스 `notipet`). 설정에는 `[marketplaces.notipet]`와 `[plugins."notipet@notipet"] enabled = true`가 생긴다.

## Internal Flow

**훅 설치** (`install-hooks --codex --write`):
1. `$CODEX_HOME\config.toml`(기본 `%USERPROFILE%\.codex\config.toml`)을 줄 단위로 읽는다.
2. notipet 블록을 찾아 뺀다: `[[hooks.X]]` 표 중 `command`가 전부 `notipet.exe`(또는 예전 플러그인의 `notipet-hook.cmd`)를 부르는 것, 바로 위의 `# notipet ...` 주석, 뒤의 빈 줄 하나. 다른 명령과 섞인 블록은 건드리지 않는다.
3. 안전 확인: 빼고 나서도 `[hooks…]` 아래에 notipet.exe가 남았거나(섞인 블록, 인라인 표), 훅이 `Stop = [...]`·`hooks.Stop = ...`처럼 값으로 적혀 있으면(표를 덧붙이면 TOML 오류) **고치지 않고 멈춘다**(종료 코드 1).
4. 새 블록을 **뺀 자리에 그대로**(없었으면 파일 끝에) 넣는다. 결과가 원래와 같으면 쓰지 않는다("already up to date"). 다르면 `config.toml.bak-notipet-<시각>`을 만들고 쓴다.
5. 1.5.1 개발 중 한때 쓰던 `hooks.json`에 notipet 항목이 있으면 백업 후 그것만 뺀다(파일에 notipet 것만 있었으면 지운다).

블록 모양 (상태 메시지는 Windows 표시 언어가 한국어면 한국어):

```toml
# notipet hooks - written by `notipet install-hooks --codex --write`, which --remove undoes. Leave `notify` alone.
[[hooks.Stop]]
[[hooks.Stop.hooks]]
type = "command"
command = '"C:\Users\<user>\AppData\Local\NotipetApp\current\notipet.exe" --source codex'
timeout = 10
statusMessage = 'Notipet · 턴 완료 알림 및 권한 알람 정리'

[[hooks.PermissionRequest]]   … (같은 꼴)
[[hooks.UserPromptSubmit]]    … async = true
```

**훅 실행**: Codex가 이벤트마다 `%ComSpec% /C "<command>"`로 실행하고 페이로드 JSON을 stdin으로 준다. `notipet.exe --source codex`가 알리거나 끝난 알람을 정리한다([cli_shim.md](cli_shim.md)). stdout에는 아무것도 쓰지 않는다.

**스킬**: Codex가 플러그인 스킬로 읽는다. 스킬은 `notipet`(PATH)을 부르고, 없으면 설치 경로를 쓰라고 안내한다. 한 파일이 모든 PC에서 쓰여야 하므로 사용자별 절대 경로를 넣을 수 없다. 훅 안내 절은 "사용자가 원할 때만, 동의를 받고, 직접 편집하지 말고 명령으로"다.

## State-Data Flow

```
저장소
  .agents/plugins/marketplace.json ── source.path ──> integrations/codex/plugin/
                                                        .codex-plugin/plugin.json
                                                        skills/notipet/SKILL.md   (생성물)
                                                        assets/icon.png
      │ codex plugin add (복사)
      ▼
%USERPROFILE%\.codex\plugins\cache\notipet\notipet\<version>\   ← Codex는 원본이 아니라 여기서 읽는다
%USERPROFILE%\.codex\config.toml
  [marketplaces.notipet] / [plugins."notipet@notipet"]          ← 플러그인
  # notipet hooks ... + [[hooks.Stop]] [[hooks.PermissionRequest]] [[hooks.UserPromptSubmit]]   ← install-hooks --write
  [hooks.state."<config.toml 경로>:stop:0:0"] trusted_hash      ← 사용자가 훅을 신뢰하면 Codex가 쓴다
  config.toml.bak-notipet-<시각>                                 ← --write/--remove가 바꿀 때마다
```

## Important Constraints

아래는 Codex 소스(openai/codex `main`, 2026-10)와 이 PC의 Codex 데스크톱(codex-cli 0.162.0-alpha.17.2)으로 확인한 것이다. Codex가 바뀌면 여기부터 다시 확인한다. 근거는 Known Problems 아래 "확인한 곳".

**플러그인은 스킬만**
- 공개 플러그인 목록은 **훅이나 앱 참조(`apps`/`.app.json`)가 든 플러그인을 받지 않는다.** 스킬만 든 플러그인은 MCP 심사 자료(데모 영상 등) 없이 스킬 검사만 통과하면 된다. 그래서 플러그인 폴더에는 `.codex-plugin`, `skills`, `assets`만 둔다(점검 스크립트가 확인).
- 스킬만 든 플러그인에 나중에 MCP 서버를 더할 수는 없다(새 플러그인이 된다).
- 매니페스트는 `.codex-plugin/plugin.json`(레거시 형식)을 쓴다. 이 PC의 Codex에서 설치·로드를 확인한 형식이다. 루트 `plugin.json`(이식형)으로 바꾸면 Codex는 그 형식에서 훅을 읽지 않는데(`loader.rs`), 스킬만이라 상관은 없지만 확인하지 않은 형식이므로 바꾸려면 다시 검증한다.
- 플러그인 이름 `notipet`과 마켓플레이스 이름 `notipet`은 플러그인 ID다. 바꾸면 기존 설치가 남남이 된다.
- `codex plugin add`는 폴더를 `cache\<marketplace>\<plugin>\<version>\`로 **복사**한다. 캐시는 버전별이므로 **플러그인 내용을 바꾸면 `version`을 올린다**. notipet은 앱 버전(csproj `<Version>`)과 같게 둔다.
- 공개 목록에 올리면 플러그인 ID가 바뀐다(마켓플레이스가 다르다). 그때 저장소 마켓플레이스로 설치한 사용자는 같은 스킬이 두 번 보이므로, 옮겨 가라고 안내해야 한다.

**훅은 config.toml에, notipet 명령으로만**
- **`~/.codex/hooks.json`이 아니라 `config.toml`.** 이 PC의 Codex에서 `codex exec`는 config.toml의 훅은 실행했지만 hooks.json의 훅은 실행하지 않았다. 앱 서버의 `hooks/list`는 둘 다 보여 줬다. main 소스는 둘을 똑같이 다루지만, 설치된 버전에서 확인된 쪽을 쓴다.
- **에이전트가 직접 편집하지 않는다.** 스킬은 `install-hooks --codex --write`를 실행하라고만 한다. LLM이 TOML을 고치면 파일을 깨거나(Codex가 못 뜬다) 무시되는 필드(`args`)를 넣고, 세션마다 다르게 고쳐 나중에 일괄로 바로잡을 수 없다.
- **사용자 동의 없이 넣지 않는다.** 스킬은 사용자가 알림을 원할 때만 `doctor`로 확인하고 물어본다. 이 명령은 샌드박스 밖 실행이라 Codex가 승인을 받는데, 그 승인이 곧 동의다.
- 핸들러 필드는 `type`, `command`, `commandWindows`, `timeout`(초), `async`, `statusMessage`, `additionalContextLimit`뿐이다(`hook_config.rs`). **`args`는 없고, 있어도 경고 없이 버려진다.** 인수는 명령줄 안에 쓴다.
- 명령은 `%ComSpec% /C "<명령줄>"`로, 세션 cwd에서, 창 없이 실행된다(`command_runner.rs`). exe 경로는 따옴표로 감싼다.
- **훅 환경이 비어 있을 수 있다.** 이 PC의 `codex exec`는 훅을 환경 변수 하나 없이 실행했다(`set` 출력이 비었다). `SystemRoot`가 없으면 Winsock이 소켓을 못 만들어 CLI가 "데몬이 꺼져 있다"고 했다. 그래서 CLI가 시작할 때 `SystemRoot`/`windir`을 Windows 폴더(셸 API)로 되살린다(`RestoreSystemRoot`). 데스크톱 앱의 훅은 환경이 채워져 있었다.
- Codex는 같은 이벤트의 훅을 출처(사용자 설정, 프로젝트 설정, 플러그인)와 상관없이 **전부, 동시에** 실행하고 중복을 거르지 않는다. notipet 블록이 두 곳에 있으면 두 번 운다. `--write`가 예전 위치를 정리하고 `doctor`가 경고하는 이유다.
- `notify`는 건드리지 않는다(AGENTS.md). 다른 것이 쓰고 있을 수 있는 단일 슬롯이다.

**신뢰(trust) — 업데이트 때 가장 중요한 것**
- Codex는 훅마다 **키**(`<config.toml 경로>:<event>:<그룹 번호>:<핸들러 번호>`)와 **해시**(이벤트·matcher·핸들러 설정 — 명령, timeout, async, statusMessage — 의 정규화 값, `discovery.rs` `hook_hash`)를 둔다. 신뢰하면 `[hooks.state."<키>"] trusted_hash`에 저장된다.
- 그러므로 **블록의 어느 글자든**(상태 문구, timeout, 명령 경로) 바꾸거나 블록의 위치(그룹 번호)를 옮기면, 그 훅은 "modified"/"untrusted"가 되어 신뢰를 요구하는 환경에서는 **다시 신뢰할 때까지 돌지 않는다.** 그래서 `--write`는 블록을 원래 자리에 다시 넣고, 내용이 같으면 파일을 건드리지 않는다. `CodexHooks.Block`의 문구를 바꾸는 것은 모든 사용자에게 재신뢰를 요구하는 일이다 — 릴리스 노트에 적는다.
- 설치판 경로(`%LOCALAPPDATA%\NotipetApp\current\notipet.exe`)는 업데이트해도 같으므로 앱 업데이트는 재신뢰를 부르지 않는다.
- 이 PC의 데스크톱 앱은 `hooks/list`가 "untrusted"라고 보여 주는 config.toml 훅도 실행했다. CLI(`codex exec`)는 신뢰되지 않은 훅을 건너뛴다(`/hooks`로 신뢰, 또는 `--dangerously-bypass-hook-trust`).

## Known Problems

- **`codex exec`(CLI)에서 확인한 차이**: 플러그인 훅은 돌지 않았고(1.5.1 개발 중 훅을 넣은 플러그인으로 시험 — 공식 문서도 "데스크톱 앱 전용"), hooks.json 훅도 돌지 않았으며, config.toml 훅은 돌았지만 환경 변수 없이 실행됐다.
- 플러그인 스킬은 `notipet`(PATH)을 부른다. 설치 직후 이미 떠 있던 Codex는 바뀐 PATH를 모를 수 있다 — Codex를 다시 시작하거나 스킬 안내대로 설치 경로를 쓴다. 예전 절대 경로로 허용해 둔 `prefix_rule`은 `notipet`에 대해 한 번 더 물을 수 있다.
- `--write`는 TOML 파서 없이 줄 단위로 일한다. 사용자가 notipet 블록 안을 손으로 고쳐 다른 명령을 섞으면 그 블록은 건드리지 않고 멈춘다.
- 이 PC의 Codex 레거시 `notify`(computer-use)는 `os error 206`(명령줄이 너무 김)으로 실패하고 있다(openai/codex#25141). notipet과 무관하지만 `notify`를 쓰지 않는 이유가 재현된 것이다.

확인한 곳:
- 문서: <https://learn.chatgpt.com/docs/plugins>, <https://learn.chatgpt.com/docs/hooks>, <https://developers.openai.com/plugins/build/plugins>, <https://developers.openai.com/plugins/deploy/submission>(훅·앱 참조가 든 플러그인은 제출 불가, 스킬만은 가능)
- 소스(openai/codex): `codex-rs/config/src/hook_config.rs`(필드), `codex-rs/hooks/src/engine/command_runner.rs`(cmd /C, 환경 재구성), `codex-rs/hooks/src/engine/discovery.rs`(층별 hooks.json·config.toml, `hook_hash`, 신뢰 판정), `codex-rs/core-plugins/src/loader.rs`, `codex-rs/core-plugins/src/manifest.rs`
- 이 PC: `codex plugin marketplace add/add/remove`, 앱 서버 `hooks/list`, `codex exec --ephemeral`에 `-c hooks.Stop=…`로 넣은 기록용 훅

## Regression Notes

- `notipet --self-test`의 `CodexHooks`: 넣기·반복·제자리 교체·빼기·예전 형식 정리·거부 사례·hooks.json 정리·임시 CODEX_HOME에서 끝까지.
- `.\scripts\check-codex-plugin.ps1` 모두 ok(`pack.ps1`도 같은 검사).
- 실제 설정 사본에 `--write` → Python `tomllib`로 읽힌다, `notify`·다른 설정 그대로, 두 번째는 "already up to date".
- 빈 환경에서 `notipet --source codex`에 Stop 페이로드 → 알림 전달(`RestoreSystemRoot` 전에는 "daemon not running").
- 데스크톱 앱 새 스레드 → 완료 알림, 권한 필요한 명령 → 승인 대기 알람, 승인 후 턴이 끝나면 알람 정지.
- `notipet doctor`의 `codex hooks`: config.toml이면 ok, hooks.json에 남아 있으면 경고.

## Rejected Approaches

- **플러그인에 훅 넣기** (1.5.1 개발 중 만들었다가 뺐다): 공개 목록에 못 올리고, 데스크톱 앱에서만 돌며(`codex exec`에서 확인), `hooks.json`을 고칠 때마다 재신뢰가 필요하다.
- **`~/.codex/hooks.json`에 쓰기** (JSON이라 편집이 안전했다): 이 PC의 Codex가 `codex exec`에서 실행하지 않았다.
- **스킬이 에이전트에게 config.toml을 직접 고치게 하기**: 파일을 깨거나 무시되는 필드를 넣을 수 있고, 결과가 세션마다 다르며, 공개 심사에서 "훅 제한 우회"로 보일 수 있다. 편집은 notipet 명령이 하고 스킬은 동의를 받아 그 명령을 부른다.
- **훅을 아예 빼고 스킬만**: 승인 대기 순간에 에이전트는 멈춰 있어 스스로 알릴 수 없고(Codex에서는 notipet 실행 자체가 또 승인을 요구한다), 완료 알림은 모델이 잊을 수 있다.
- **TOML 파서 의존성 추가**: CLI는 의존성 없는 NativeAOT exe로 둔다. 블록을 통째로 넣고 빼며, 위험한 모양이면 멈춘다.

## TODO

- 스레드별로 훅 알림 켜기/끄기(훅은 그대로, notipet이 스레드 ID로 거른다)
- `install-hooks --claude --write`
- 공개 플러그인 목록 제출(조직 인증 필요). 제출하면 ID 이동 안내
- `Interrupt` 이벤트(사용자가 턴을 끊음)로 그 스레드 알람 정리 — 블록 문구가 바뀌어 재신뢰가 필요하므로 다른 변경과 묶어서
