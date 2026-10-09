# CLI 레퍼런스 — `notipet.exe`

`bin\notipet.exe`는 두 가지 일을 한다.

1. **에이전트 훅이 부르는 쉼(shim)** — 명령 없이 불리면 stdin이나 마지막 인자의 JSON을 알림으로 바꿔 데몬에 넘긴다.
2. **사람이 쓰는 관리 도구** — 알림 보내기, 상태 보기, 음소거, 자리 착석, 기록 보기, 데몬 시작/종료, 설치.

데몬(`NotipetTray.exe`)과는 다른 프로그램이다. NativeAOT로 빌드해 기동이 빠르다 — 훅은 에이전트의 임계 경로에 있어서 호출할 때마다 기동 비용을 낸다.

```
notipet <명령> [옵션]
notipet help [명령]            명령 목록 / 명령별 도움말
notipet <명령> --help           위와 같음
```

---

## 공통 규칙

| 규칙 | 내용 |
|---|---|
| **종료 코드는 기본적으로 항상 0** | 데몬이 꺼져 있거나 전달이 실패해도 0. 알림 도구가 죽었다고 에이전트 동작이 바뀌면 안 되기 때문이다. 진짜 종료 코드가 필요하면 `--strict` |
| 데몬 자동 기동 | `send` / `test` / `open`은 데몬이 꺼져 있으면 띄운다. 막으려면 `--no-launch` |
| `--json` | 사람용 한 줄 대신 서버 응답 원문 JSON. 스크립트용 |
| `--quiet` | 아무것도 출력하지 않음 |
| 출력 인코딩 | UTF-8로 고정. 한글 제목·본문이 물음표로 깨지지 않는다 |
| 데몬 찾기 | `%LOCALAPPDATA%\notipet\runtime.json`에서 포트와 토큰을 읽는다. 포트를 외울 필요가 없다 |

---

## 알림 보내기

### `send` (별칭 `alert`)

```
notipet send --title T --body B [--level L] [--tag T] [옵션]
```

| 옵션 | 설명 |
|---|---|
| `--title T` | 제목. **프로젝트 이름으로 시작하라** — 세션을 여러 개 돌릴 때 어디서 온 건지 알 수 있다 |
| `--body B` | 본문. **`--body -`면 stdin에서 읽는다** (파이프 입력) |
| `--level L` | `info` \| `success` \| `attention` \| `warn` \| `error` \| `critical` |
| `--tag T` | 중복 병합 키. **같은 대화(스레드)에서** 같은 태그가 30초 안에 또 오면 소리는 한 번. 다른 대화는 같은 태그라도 따로 |
| `--agent A` | `claude-code` \| `codex` \| `manual`. 별칭 `--source`. 카드의 에이전트 표시와 소스별 레이트 리밋에 쓰인다. 없으면 [자동 감지](#에이전트-안에서-부를-때--자동으로-채워지는-것) |
| `--project P` | 최근 알림 창의 그룹 이름. 없으면 현재 폴더가 속한 **저장소** 이름 |
| `--thread-title T` | 이 대화·작업의 짧은 이름 ("결제 리팩터링"). 한 대화에서는 같은 이름을 쓴다 |
| `--thread ID` | 스레드/세션 ID (별칭 `--session`). 보통은 자동이라 줄 필요 없다 |
| `--repeat M` | `once` \| `repeat` \| `until-ack` — 레벨 기본값을 덮어쓴다 |
| `--sound NAME` | 윈도우 별칭(`Notification.IM`, `SystemAsterisk` …) 또는 `sound.library`에 등록한 이름 |
| `--volume V` | 0~1. 설정의 볼륨에 곱해진다 |
| `--silent` | 소리 없이 알림만 |
| `--channels LIST` | `sound,notification` — 범위를 **좁히기만** 한다. 꺼 둔 채널을 켤 수는 없다 |
| `--ttl SEC` | 그 시간 안에 전달 못 하면 버린다 |
| `--cwd PATH` | 카드에 표시할 폴더. 기본은 현재 폴더 |
| `--fire-and-forget` | 응답을 기다리지 않고 바로 반환 |

출력:

```
sent [success]: sound delivered, notification delivered
sent [critical]: sound delivered, notification delivered  - shortened: at desk
not sent: deduped (same as ntp_20260921080300_0010)
not sent: muted, next allowed 17:18:01
```

`not sent`는 오류가 아니다 — 규칙이 막은 것이고, 이유가 같이 나온다. 이유 목록은 [USAGE.md 문제 해결](USAGE.md#7-문제-해결).

예:

```powershell
notipet send --title "api: 테스트 통과" --body "412/412" --level success --tag "api:tests"

# 명령 출력을 그대로 본문으로
dotnet test 2>&1 | Select-Object -Last 5 | notipet send --title "api: 테스트 결과" --body - --level info

# 긴 빌드 뒤에 결과에 따라
dotnet build; if ($?) { notipet send --title "빌드 성공" --level success } else { notipet send --title "빌드 실패" --level error }

# 소리 없이 기록만
notipet send --title "배포 시작" --body "staging" --silent
```

### 에이전트 안에서 부를 때 — 자동으로 채워지는 것

Claude Code나 Codex 안에서(스킬, 셸 도구, 훅) `notipet`을 부르면 CLI가 **그 에이전트가 넣어 둔 환경 변수**에서 다음을 직접 읽는다. 지정한 변수만 읽고, 환경 전체를 훑지 않는다.

| 채워지는 것 | Claude Code | Codex |
|---|---|---|
| 에이전트 (`--agent`가 없을 때) | `CLAUDE_CODE_SESSION_ID` 또는 `CLAUDECODE=1` | `CODEX_SESSION_ID` 또는 `CODEX_THREAD_ID` |
| 스레드 ID | `CLAUDE_CODE_SESSION_ID` (훅의 `session_id`와 같은 값) | `CODEX_SESSION_ID`(루트 스레드) → `CODEX_THREAD_ID` |
| Claude Desktop 세션 ID | `CLAUDE_CODE_HOST_SESSION_ID` (`local_…`) — 카드에서 **Claude에서 열기**가 이걸로 동작 | — |
| 프런트엔드 | `CLAUDE_CODE_ENTRYPOINT` | — |
| 프로젝트 (`--project`가 없을 때) | 훅이면 `CLAUDE_PROJECT_DIR`, 아니면 현재 폴더 → 위로 올라가며 `.git`을 찾아 **저장소 이름**. 워크트리는 원래 저장소로 | 같음 |

- **명시한 값이 항상 이긴다.** `--agent manual`이면 에이전트의 값은 하나도 가져오지 않는다.
- 두 에이전트의 변수가 **둘 다** 보이면(한 에이전트 안에서 다른 에이전트를 띄운 경우) 추측하지 않는다 — `--agent`를 줘야 한다.
- 어느 것도 없으면 `manual`, 스레드 없음, 프로젝트는 현재 폴더 이름 — 그래도 알림은 정상으로 간다.

### `test`

```
notipet test [--level L]
```

테스트 알림 한 건. 기본 레벨은 `attention`. 실제 알림과 같은 규칙을 거치므로 음소거 중이면 막힌다 — 그게 정상이다.

---

## 상태 보기

### `status`

```
notipet status [--json]
```

```
version      1.1.0
port         4954
pid          59796
uptime       4m 11s
muted        no
at desk      no
quiet hours  not active
presence     Active
alarms       0
history      4 kept
language     System
sound        mediaplayer (ok)
```

### `ping`

한 줄: `ok  version=1.1.0  port=4954  pid=59796`. 꺼져 있으면 `not running (no runtime.json)` 또는 `not running (stale runtime.json, pid N)` — 후자는 비정상 종료로 파일만 남은 상태이고, `notipet start`가 정리한다.

### `version`

CLI 버전과 실행 중인 데몬 버전.

### `doctor` — "소리가 안 났다"면 제일 먼저

```
[ok  ] daemon exe         C:\src\notipet\bin\NotipetTray.exe
[ok  ] daemon running     pid 59796, port 4954, v1.1.0
[ok  ] sound engine       mediaplayer
[ok  ] muted              no
[ok  ] quiet hours        not active
[ok  ] at desk            no
[warn] claude hooks       not configured - `notipet install-hooks --claude`
[warn] codex hooks        not configured - `notipet install-hooks --codex`
[warn] claude skill       not installed - `notipet install-skill`
[ok  ] cli                C:\src\notipet\bin\notipet.exe
```

`FAIL`은 알림이 안 오는 원인, `warn`은 설정하지 않은 것(또는 지금 소리가 줄어드는 상태)이다.

### `history`

```
notipet history [--limit N] [--short] [--json]
notipet history clear
```

```
10-08 04:37  attention codex        [shop] shop: 결정 필요
                             # 결제 리팩터링
                             Codex가 보낸 것처럼 - 다른 프로젝트
10-08 04:37  success   claude-code  [notipet] notipet: 그룹 테스트 x2
                             # 알람 프로그램 설계
09-21 17:03  info      manual       cli: muted  [not sent: muted]
```

**막힌 알림도 이유와 함께 나온다.** `[...]`는 프로젝트, `#`은 스레드 이름, `x2`는 중복 병합 횟수. `--short`는 첫 줄만 보여 준다. `history clear`는 전부 지운다(되돌릴 수 없음).

---

## 제어

| 명령 | 설명 |
|---|---|
| `notipet ack` | 울리는 알람 전부 정지 |
| `notipet ack --tag T` / `--id ID` | 특정 알람만 |
| `notipet resolve` | 에이전트 안에서: **이 대화가** 울린 알람만 정지 + 그 알림 창만 닫기 |
| `notipet resolve --tag T` | 이 대화의 그 태그 알림만. `--id ID`(알림 하나), `--thread ID`(다른 대화), `--agent A` |
| `notipet mute` | 무기한 음소거 (`critical`은 설정에 따라 통과) |
| `notipet mute 30m` / `mute 2h` | 그 시간 뒤 자동 해제. 출력: `muted until 17:18` |
| `notipet mute off` / `notipet unmute` | 해제 |
| `notipet desk` | 지금 상태 (`at desk` / `away`) |
| `notipet desk on` / `off` / `toggle` | 자리 착석 스위치. 트레이 메뉴의 "PC 앞에 있음"과 같다 |
| `notipet open` / `open recent` | 최근 알림 창 |
| `notipet open settings` | 설정 창 |

`ack`와 `resolve`의 차이: `ack`는 사람이 "시끄럽다, 꺼"라고 하는 것(선택자가 없으면 전부), `resolve`는 보낸 쪽이 "그 일은 끝났다"고 하는 것(지목한 것만, 아무것도 지목하지 않으면 거부). `resolve`는 데몬을 띄우지 않고, 이미 꺼졌거나 닫혔으면 `nothing to resolve`를 출력하고 0으로 끝난다 — 확인 없이 불러도 된다. 출력: `resolved 1: 1 alarm(s) stopped, 1 pop-up(s) closed`.

`desk on`이면 긴 알람이 짧아지고, 설정에서 켰다면 다른 소리로 대체된다. 자리를 뜰 때 `desk off`를 잊으면 자리를 비운 사이 오는 알림도 짧게 울린다는 점만 기억하면 된다.

---

## 데몬

| 명령 | 설명 |
|---|---|
| `notipet start` | 꺼져 있으면 띄운다. 이미 돌고 있으면 `already running` |
| `notipet stop` | 정상 종료를 요청하고 **프로세스가 끝날 때까지 기다린다** (`runtime.json`이 정리된다) |
| `notipet restart` | stop 후 start |

`stop`은 exe 잠금이 풀린 뒤에 반환하므로, 재배포 스크립트가 바로 이어서 파일을 덮어쓸 수 있다. `scripts\publish.ps1 -Restart`가 이걸 쓴다.

데몬을 **띄울 때**는 CLI 옆의 `NotipetTray.exe` → `runtime.json`의 경로 → `%LOCALAPPDATA%\notipet\daemon.path`(데몬이 뜰 때마다 기록) 순서로 찾는다. CLI 옆이 먼저인 이유: 강제 종료로 남은 `runtime.json`이 예전 개발 빌드를 가리킬 수 있고, 배포한 뒤에는 배포한 것을 띄워야 하기 때문이다. `doctor`와 `open`은 실행 중인 데몬의 경로를 먼저 본다.

---

## 설치

### `install-hooks`

```
notipet install-hooks [--claude] [--codex]
```

Claude Code / Codex 훅 설정 블록을 **출력만** 한다. 설정 파일은 건드리지 않는다. 옵션 없이 부르면 둘 다. 자세한 설명은 [USAGE.md 3절](USAGE.md#3-에이전트-연동).

### `install-skill`

```
notipet install-skill [--claude | --codex | --path DIR] [--print] [--force]
```

에이전트가 **스스로 판단해서** 알림을 보내게 하는 스킬을 설치한다. 이 CLI의 실제 경로가 채워진 `SKILL.md`를 쓴다.

| 옵션 | 위치 |
|---|---|
| (기본) `--claude` | `%USERPROFILE%\.claude\skills\notipet\SKILL.md` |
| `--codex` | `%USERPROFILE%\.codex\skills\notipet\SKILL.md` — **Codex용 스킬**(샌드박스 밖에서 실행하라는 안내가 들어 있다). `--path`와 함께 쓰면 그 폴더에 Codex용을 쓴다 |
| `--path DIR` | `DIR\notipet\SKILL.md` (예: 프로젝트의 `.claude\skills`) |
| `--print` | 파일로 쓰지 않고 출력만 |
| `--force` | 직접 고친 스킬 파일도 덮어씀 |

훅과 스킬의 차이, 같이 쓰는 법은 [SKILL.md](SKILL.md).

---

## 훅 모드 (명령 없음)

```
notipet --source claude-code          ← stdin으로 훅 JSON (Claude Code / Codex hooks)
notipet alert --source codex '{...}'  ← 마지막 인자가 JSON (Codex 레거시 notify)
```

- **stdout에 아무것도 쓰지 않는다.** 일부 훅 이벤트(UserPromptSubmit, SessionStart)는 stdout을 에이전트 컨텍스트에 그대로 넣기 때문이다. 확인이 필요하면 `--verbose`.
- stdin 읽기는 1초로 제한된다. 닫히지 않는 stdin이 에이전트를 붙잡지 않게.
- Codex `notify`의 JSON이 윈도우 명령줄 길이를 넘어 깨져도 크래시하지 않고 "Turn complete (payload unreadable)" 알림으로 떨어진다 (openai/codex#25141).
- 매핑 규칙(어떤 이벤트가 어떤 레벨이 되는지)은 [USAGE.md 4절](USAGE.md#4-신호가-어떻게-해석되는가).
- 훅 페이로드에 없는 것(Claude Desktop 세션 ID, 저장소 이름)은 [위의 자동 채우기](#에이전트-안에서-부를-때--자동으로-채워지는-것)로 보충한다. 훅은 에이전트의 환경을 물려받는다.
- Codex 레거시 `notify`는 `thread-id`만 스레드로 쓴다. `turn-id`는 턴마다 바뀌어서 스레드로 쓰면 카드가 턴마다 갈라진다.
- `Stop`·`UserPromptSubmit`·`SessionEnd`는 알림을 보내기 전에 **같은 세션의 끝난 알림**을 먼저 해제한다(`/v1/resolve`). 무엇이 해제되는지는 [api.md](api.md#post-hooksclaude-code). `UserPromptSubmit`은 알림을 보내지 않는다. 해제는 데몬이 떠 있을 때만 — 꺼져 있으면 울리는 것도 없다.

---

## 버전 1.3에서 바뀐 것

| 전 | 후 | 이유 |
|---|---|---|
| 알람은 사람만 끌 수 있었다 | `notipet resolve` — 보낸 쪽이 "끝났다"고 알리면 그 알람·창만 꺼진다 | 원격에서 답했거나 에이전트가 스스로 해결해도 빈 자리에서 계속 울렸다 |
| `Stop`은 알림만 | 같은 세션의 권한 요청 알람을 먼저 해제 | 폰에서 승인하고 작업이 끝나도 권한 알람이 계속 울렸다 |
| `UserPromptSubmit`은 쓰지 않음 | `install-hooks`에 추가(async). 지난 턴의 "완료"와 idle 알림을 해제, 알림은 없음 | |

## 버전 1.2에서 바뀐 것

| 전 | 후 | 이유 |
|---|---|---|
| — | `--agent`, `--project`, `--thread-title`, `--thread` | 최근 알림 창을 프로젝트별로 나누고, 카드에서 스레드를 바로 열기 위해 |
| 에이전트 안에서 불러도 `manual` | 환경 변수로 에이전트·스레드 자동 감지 | 스킬이 매번 `--source`를 줄 필요가 없다 |
| `--source claude`는 별개의 소스 | `claude-code`로 정규화 | 레이트 리밋 버킷과 설정 키가 갈라졌다 |
| Codex notify의 `turn-id`를 세션으로 사용 | `thread-id`만 | 턴 ID는 스레드가 아니다 |
| `history`가 소스와 제목만 | `[프로젝트]`, `# 스레드 이름` 추가 | |

## 버전 1.1에서 바뀐 것

| 전 | 후 | 이유 |
|---|---|---|
| `send`가 서버 JSON 원문 출력 | 사람용 한 줄. 원문은 `--json` | 읽을 수 있어야 쓴다 |
| 훅 모드도 JSON 출력 | 조용함 | 일부 훅은 stdout을 에이전트 컨텍스트에 주입한다 |
| `alert` + 뒤에 붙은 JSON이 무시됨 | 훅 페이로드로 인식 | Codex `notify`가 `alert` 뒤에 JSON을 붙이는 설정이 있었다 |
| 모르는 명령이면 조용히 훅 모드 | `unknown command` 오류 | 오타가 조용히 삼켜졌다 |
| — | `doctor`, `history`, `desk`, `open`, `start`, `stop`, `restart`, `unmute`, `version`, `install-skill`, `help <명령>` | |
| — | `send --body -`, `--silent`, `--channels`, `--ttl`, `--cwd` | |
