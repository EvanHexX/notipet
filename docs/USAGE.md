# 사용법

notipet을 설치하고, Claude Code / Codex와 연결하고, 일상적으로 쓰는 방법.

API 계약 전체는 [api.md](api.md), 설정 스키마 전체는 [settings.md](settings.md)에 있다. 이 문서는 "어떻게 쓰는가"만 다룬다.

---

## 1. 설치와 첫 실행

```powershell
cd C:\src\notipet
.\scripts\publish.ps1
```

`bin\`에 두 개가 생긴다.

| 파일 | 역할 |
|---|---|
| `NotipetTray.exe` | 트레이에 상주하는 데몬. 소리를 내고 알림을 띄운다 |
| `notipet.exe` | 에이전트 훅이 부르는 CLI. 사람이 직접 쓰기도 한다 |

> 이름이 비슷하지만 다른 프로그램이다. 윈도우 파일명이 대소문자를 구분하지 않아서 데몬을 `NotipetTray`로 부른다.

데몬을 띄우고 확인한다:

```powershell
.\bin\NotipetTray.exe
.\bin\notipet.exe ping     # ok  version=1.0.0  port=5523  pid=7032
.\bin\notipet.exe test     # 소리가 나고 트레이 알림이 떠야 한다
```

여기서 소리가 안 나면 [문제 해결](#7-문제-해결)로.

부팅할 때마다 자동으로 띄우려면 **트레이 아이콘 우클릭 → Windows 시작 시 실행**을 체크한다.

새 버전으로 교체할 때는 실행 중인 데몬을 멈추고 배포한 뒤 다시 띄운다 — 한 줄로:

```powershell
.\scripts\publish.ps1 -Restart
```

---

## 2. 데몬을 찾는 방식 — 포트를 외울 필요가 없다

데몬은 뜰 때 임의의 빈 포트를 잡고, 포트와 인증 토큰을 여기에 쓴다:

```
%LOCALAPPDATA%\notipet\runtime.json
```

CLI가 그 파일을 읽어서 알아서 찾아간다. **그래서 훅 설정에 포트를 적을 필요가 없다.**

포트를 고정해야 하는 경우는 하나뿐이다 — Claude Code의 `http` 훅([경로 B](#경로-b--claude-code-cli-없이-직접-http))을 쓸 때. 그때는 `settings.json`에서 `server.port`를 고정한다.

데몬이 꺼져 있으면 CLI가 **알아서 띄운다.** 재부팅 후 첫 알림을 놓치는 것보다 낫다는 판단이고, 싫으면 `--no-launch`를 붙인다.

---

## 3. 에이전트 연동

두 가지 방법이 있고, 같이 쓰는 게 가장 좋다.

- **훅** (이 절) — 에이전트 **프로그램**이 턴 종료·권한 대기 같은 이벤트마다 자동으로 부른다. 놓치는 일이 없다.
- **스킬** — 에이전트(**LLM**)가 "이건 사용자가 알아야 한다"고 판단할 때 구체적인 내용으로 보낸다. `notipet install-skill` 한 줄. 설명은 **[SKILL.md](SKILL.md)**.

훅은 스니펫을 출력해서 붙여넣는 게 가장 빠르다:

```powershell
.\bin\notipet.exe install-hooks
```

아래는 그 내용을 풀어 쓴 것이다. 전체 예시 파일은 [../integrations/](../integrations/)에 있다.

### 경로 A — Claude Code, CLI 경유 (권장)

`%USERPROFILE%\.claude\settings.json`:

```json
{
  "hooks": {
    "Notification": [
      { "matcher": "agent_needs_input|agent_completed|permission_prompt|idle_prompt",
        "hooks": [ { "type": "command",
                     "command": "C:\\src\\notipet\\bin\\notipet.exe",
                     "args": ["--source", "claude-code"],
                     "timeout": 10, "async": true } ] }
    ],
    "Stop": [
      { "hooks": [ { "type": "command",
                     "command": "C:\\src\\notipet\\bin\\notipet.exe",
                     "args": ["--source", "claude-code"],
                     "timeout": 10, "async": true } ] }
    ],
    "SubagentStop": [
      { "matcher": ".*",
        "hooks": [ { "type": "command",
                     "command": "C:\\src\\notipet\\bin\\notipet.exe",
                     "args": ["--source", "claude-code"],
                     "timeout": 10, "async": true } ] }
    ]
  }
}
```

알아둘 것:

- JSON이라 윈도우 경로에 `\\`를 쓴다.
- **`async: true`가 중요하다.** 타임아웃이 적용되지 않고 훅이 에이전트의 턴을 붙잡지 않는다.
- `Notification` / `Stop` / `SubagentStop`은 설계상 에이전트를 블로킹할 수 없다. notipet이 Claude Code의 동작을 바꿀 일은 없다.
- 이미 `hooks` 블록이 있으면 Claude Code가 병합한다. 통째로 덮어쓰지 않아도 된다.

### 경로 B — Claude Code, CLI 없이 직접 HTTP

Claude Code는 `http` 훅 타입을 네이티브로 지원한다. 중간 프로세스가 없어서 가장 가볍다.

**먼저 포트를 고정한다.** `%LOCALAPPDATA%\notipet\settings.json`:

```json
{ "server": { "port": 47811 } }
```

데몬을 재시작한 뒤:

```json
{
  "hooks": {
    "Notification": [
      { "matcher": "agent_needs_input|agent_completed|permission_prompt|idle_prompt",
        "hooks": [ { "type": "http",
                     "url": "http://127.0.0.1:47811/hooks/claude-code",
                     "timeout": 5 } ] }
    ],
    "Stop": [
      { "hooks": [ { "type": "http",
                     "url": "http://127.0.0.1:47811/hooks/claude-code",
                     "timeout": 5 } ] }
    ]
  }
}
```

> 데몬은 `/hooks/*`에도 베어러 토큰을 요구한다. Claude Code가 토큰 헤더를 보내도록 설정할 수 없다면 경로 A를 쓴다 — CLI가 `runtime.json`에서 토큰을 읽어 대신 붙여준다.

### 경로 C — Codex

`%USERPROFILE%\.codex\config.toml`에 **추가**한다:

```toml
[[hooks.Stop]]
[[hooks.Stop.hooks]]
type = "command"
command = 'C:\src\notipet\bin\notipet.exe'
args = ["--source", "codex"]
timeout = 10

[[hooks.PermissionRequest]]
[[hooks.PermissionRequest.hooks]]
type = "command"
command = 'C:\src\notipet\bin\notipet.exe'
args = ["--source", "codex"]
timeout = 10
```

> ### ⚠️ 기존 `notify = [...]` 줄을 건드리지 않는다
>
> `notify`는 **슬롯이 하나뿐이다.** 이 머신에서는 이미 Codex 자체의 computer-use 통합이 쓰고 있고, 덮어쓰면 그게 조용히 깨진다.
>
> 게다가 hooks 쪽이 모든 면에서 낫다:
> - `notify`는 이벤트가 `agent-turn-complete` **하나뿐**이다. "사용자 입력 대기" 이벤트 추가 요청은 not-planned로 닫혔다. hooks는 `PermissionRequest`(= 에이전트가 나를 기다리는 중)를 준다 — 정작 듣고 싶은 신호가 그거다.
> - hooks는 페이로드를 stdin으로 준다. `notify`는 마지막 argv 인자로 주는데, `input-messages`가 프롬프트 히스토리 전체로 불어나 **윈도우 커맨드라인 길이 제한을 넘는 버그**(openai/codex#25141, 미해결)가 있다.
>
> notipet은 레거시 `notify` 형식도 알아듣긴 한다. 그 슬롯이 비어 있고 그쪽을 쓰고 싶다면 동작은 한다.

### 연동 확인

에이전트에서 짧은 작업을 하나 돌려보고, 끝날 때 소리가 나는지 본다. 안 나면:

```powershell
.\bin\notipet.exe status          # 데몬이 살아 있는가
curl.exe -s -H "Authorization: Bearer $((Get-Content "$env:LOCALAPPDATA\notipet\runtime.json" | ConvertFrom-Json).token)" `
  "$((Get-Content "$env:LOCALAPPDATA\notipet\runtime.json" | ConvertFrom-Json).baseUrl)/v1/history?limit=10"
```

히스토리에 기록이 있는데 소리가 안 났다면 `suppressedReason`에 이유가 적혀 있다.

---

## 4. 신호가 어떻게 해석되는가

CLI를 거치든 HTTP로 직접 오든 같은 매퍼([../shared/PayloadMapper.cs](../shared/PayloadMapper.cs))를 통과한다.

```
Claude Code / Codex  ──훅 발생──>  notipet.exe (CLI)  ──HTTP POST──>  NotipetTray.exe
                                        또는 직접 HTTP                      │
                                                                    소리 + 트레이 알림
```

### 이벤트 → 레벨

| 에이전트가 보낸 것 | notipet 레벨 | 기본 소리 |
|---|---|---|
| `agent_needs_input`, `permission_prompt`, `idle_prompt` | **attention** | 알람음 2회 반복 |
| `agent_completed` | success | 1회 |
| `Stop` (턴 완료) | success | 1회 |
| `PermissionRequest` (Codex) | **attention** | 알람음 2회 반복 |
| `SubagentStop`, `SessionEnd` | info | 1회 |
| `StopFailure` | error | 3회 반복 |
| 모르는 이벤트 | info | 1회 + 본문에 원본 이벤트명 |

마지막 줄이 설계 의도다. 에이전트가 페이로드 형식을 바꾸는 날에도 notipet은 **조용히 멈추지 않고** 소리를 내면서 "이런 이벤트를 봤다"고 알려준다.

### 실제 변환 예시

Claude Code가 권한 승인을 기다릴 때 stdin으로 보내는 것:

```json
{ "session_id": "8f3a1234567890",
  "hook_event_name": "Notification",
  "notification_type": "agent_needs_input",
  "cwd": "C:\\src\\notipet",
  "notification_data": { "tool_name": "Bash" } }
```

notipet이 만드는 알림:

```
제목: Claude Code - notipet
본문: Waiting for your input (Bash)
레벨: attention              → 알람음 2회
태그: claude-code:8f3a12345678:Notification:agent_needs_input
```

**제목에 저장소 이름이 들어간다.** 세션을 세 개 동시에 돌릴 때 어느 쪽이 부르는지 알아야 하기 때문이다.

**태그는 중복 병합 키다.** 같은 권한 프롬프트로 훅이 3번 터져도 소리는 1번만 나고 히스토리의 `count`만 올라간다.

### 레벨별 기본 동작

| 레벨 | 기본 소리 | 반복 |
|---|---|---|
| `info` | `Notification.Default` | 1회 |
| `success` | `Notification.IM` | 1회 |
| `attention` | `Notification.Looping.Alarm` (5초) | 2회 (사이 0.7초 공백) |
| `warn` | `SystemExclamation` | 1회 |
| `error` | `SystemHand` (2초) | 3회 (사이 0.7초 공백) |
| `critical` | `SystemExclamation` | **확인할 때까지** (사이 1.5초 공백) |

간격은 **소리가 끝난 뒤의 공백**이지 주기가 아니다. 5초짜리 알람에 0.7초 간격이면 5초 재생 → 0.7초 침묵 → 5초 재생으로 약 10.7초다.

**PC 앞에 있음**을 체크해 두면 이 반복이 1회로 줄어든다. [트레이 메뉴](#6-트레이-메뉴와-창) 참고.

`critical`의 반복은 트레이 아이콘 클릭 / 알림 풍선 클릭 / `notipet ack` / 최대 지속 시간(기본 120초)에서 멈춘다. 이 상한은 설정과 무관하게 코드에서 강제된다 — 잘못 구성된 훅이 자리 비운 사이 사이렌을 계속 울리면 안 되기 때문이다.

레벨별 소리는 `settings.json`의 `sound.byLevel`에서 바꾼다. 자세한 건 [settings.md](settings.md).

---

## 5. CLI

자주 쓰는 것만 추렸다. 전체 명령과 옵션은 **[CLI.md](CLI.md)**, 또는 `notipet help`.

```powershell
notipet send --title "api: 배포" --body "스테이징 완료" --level success   # 알림 보내기
dotnet test 2>&1 | notipet send --title "api: 테스트" --body -           # 출력을 본문으로
notipet doctor          # 알림이 안 왔을 때 제일 먼저
notipet status          # 음소거·자리 착석·방해금지·알람 상태
notipet history         # 최근 알림 (막힌 것도 이유와 함께)
notipet desk on         # PC 앞에 있음 → 긴 알람이 짧아짐
notipet mute 30m        # 회의 들어갈 때 / notipet unmute
notipet ack             # 울리는 알람 전부 정지
notipet open settings   # 옵션 창
notipet restart         # 데몬 재시작
```

명령 없이 부르면 **에이전트 훅 페이로드로 해석한다**(stdin JSON 또는 마지막 인자의 JSON). 이때는 stdout에 아무것도 쓰지 않는다.

**종료 코드는 기본적으로 항상 0이다.** Claude Code는 특정 비-0 종료를 블로킹 신호로 해석하는데, 알림 도구가 죽었다고 에이전트 동작이 바뀌면 안 되기 때문이다. 스크립트에서 진짜 종료 코드가 필요하면 `--strict`.

---

## 6. 트레이 메뉴와 창

아이콘 **우클릭**. 메뉴는 Windows 다크/라이트 설정을 따르고, 동작 항목에는 Fluent 아이콘이 붙는다(체크 항목은 체크 표시가 그 자리를 쓴다).

| 항목 | 설명 |
|---|---|
| 알람 정지 | 울리는 중에만 나타난다 |
| **PC 앞에 있음** (체크) | 자리에 앉아 있는 동안 켜 둔다. 긴 알람이 짧아지고, 옵션에 따라 다른 소리로 바뀐다 |
| 음소거 (체크) | 무기한. `critical`은 설정에 따라 통과 |
| 30분간 음소거 | 시간 지나면 자동 해제 |
| 방해금지 시간대 (체크) | `quietHours` 구간을 켜고 끈다 |
| 최근 알림... | 카드 목록 창 |
| 옵션... | 설정 창 |
| 테스트 알림 | 소리 확인 |
| 데이터 폴더 열기 | `settings.json`, `runtime.json`, `crash.log`가 있는 곳 |
| Windows 시작 시 실행 (체크) | HKCU Run 키 |
| notipet 종료 | |

아이콘 **좌클릭**은 순서가 정해져 있다.

1. 알람이 **울리는 중**이면 → 알람만 정지. 창은 **다음 클릭**에서 뜬다.
2. 최근 알림 창이 **올라와 있으면**(보이고, 다른 창에 가려지지 않았으면) → 닫는다.
3. 그 외(닫혀 있음, 최소화, **다른 창 뒤에 가려짐**) → 맨 앞으로 띄운다.

가려진 창을 클릭했을 때 닫히지 않고 올라오는 게 핵심이다. 판단은 "포커스가 있나"가 아니라 "**위에 겹친 창이 있나**"로 한다 — 트레이를 누르는 순간 포커스는 항상 작업 표시줄로 가기 때문이다. 더블클릭은 한 번으로 친다(열자마자 닫히지 않게). 예전에는 좌클릭이 테스트 알림을 울렸다 — 아무 일 없을 때 클릭하면 이유 없이 소리가 나서 없앴다.

**알림 풍선 클릭**: 알람 정지.

### 최근 알림 창

**프로젝트별로 묶인다.** 그룹 머리글(폴더 아이콘 · 이름 · 개수)을 누르면 접히고, 마우스를 올리면 어느 폴더에서 왔는지 보인다. 새 알림이 온 프로젝트가 위로 오고, 프로젝트를 알 수 없는 알림은 맨 아래 **"기타"**에 모인다 — 빠진 정보 때문에 알림이 안 보이는 일은 없다.

카드 하나가 알림 하나다.

- 맨 왼쪽 **색 띠**: 보낸 에이전트 — 주황 Claude, 보라 Codex, 회색 그 외/알 수 없음. 심각도 색(초록·노랑·빨강·파랑)과 겹치지 않게 골랐다
- 레벨 배지 (색과 아이콘으로 심각도)
- 제목
- **스레드 이름**(말풍선 아이콘) — 에이전트 앱이 붙인 이름(Codex 사이드바 이름, Claude 세션 이름)이 있으면 그것, 없으면 보낸 쪽이 준 이름, 그것도 없으면 짧은 ID. **파란 글씨면 링크** — 누르면 그 스레드가 Claude/Codex 데스크톱 앱에서 열린다
- **본문 전체(줄바꿈, 드래그해서 선택 가능)**
- 레벨 · "3분 전" 같은 상대 시각 (마우스를 올리면 정확한 시각) · 중복 횟수 `×3`
- 칩: **에이전트 이름**(색 점과 함께 — 색만으로 구분하지 않는다), 배달 결과(`소리 전달`, `알림 전달`) 또는 **막힌 이유**(`차단됨: 음소거`)
- 폴더 경로
- 오른쪽 버튼: **Claude/Codex에서 열기**(링크가 있을 때) · **복사**(제목+본문) · **폴더 열기** · **삭제**(이 항목만)

링크가 있는 카드는 **카드 아무 곳이나 클릭**해도 그 스레드가 열린다(마우스가 손 모양으로 바뀐다). 본문은 글을 선택할 수 있도록 클릭해도 열지 않고, 카드 안의 버튼은 각자의 동작을 한다.

**스레드 열기**가 되는 경우:

| 에이전트 | 조건 | 여는 링크 |
|---|---|---|
| Codex (데스크톱 앱 = ChatGPT 앱) | 스레드 ID가 있음 (훅·스킬 모두 자동) | `codex://threads/<id>` — 공식 문서에 있는 형식 |
| Claude Code (Claude 데스크톱 앱의 Code 탭) | Claude Desktop 안에서 보낸 알림 (`CLAUDE_CODE_HOST_SESSION_ID`가 있음) | `claude://code/continue?session=local_…` — 앱 코드에서 확인한 형식, 문서화되지 않음 |
| 터미널에서 돌린 Claude Code, 그 외 | — | 링크 없음. **폴더 열기**를 쓴다 |

앱이 설치돼 있지 않으면(URL 처리기가 등록돼 있지 않으면) 링크를 보여 주지 않는다. Claude 링크는 문서화되지 않은 경로라 앱 업데이트로 바뀔 수 있다 — 그러면 앱만 앞으로 오거나 Code 탭 첫 화면이 열린다.

위쪽:

- **필터**: 전체 / 전달됨 / 차단됨
- **새로 고침**, **옵션**(최근 알림 페이지로 바로), **비우기**(확인 후 전부 삭제)

상대 시각은 창이 열려 있는 동안 30초마다 갱신된다. 100개씩 보여주고 더 있으면 "더 보기". `Esc`로 닫는다.

여는 법: 트레이 좌클릭 · 메뉴의 "최근 알림..." · `notipet open` · `NotipetTray.exe`를 한 번 더 실행.

### 옵션 창

Windows 11 설정 앱처럼 왼쪽에 페이지, 오른쪽에 카드. **바꾸는 즉시 저장된다** (확인/취소 버튼 없음). `Esc`로 닫는다.

| 페이지 | 내용 |
|---|---|
| **일반** | **언어**(Windows 설정 따름 / English / 한국어), Windows 시작 시 실행, 알림 표시 on/off, 트레이 클릭으로 알람 정지, 테스트 알림 보내기 |
| **사운드** | 소리 재생 on/off, 볼륨, **알람 최대 지속 시간**(30초~10분, 또는 **무제한**), **레벨별 사운드**(레벨마다 소리 · 재생 횟수 1~10회 또는 **확인할 때까지** · 간격 · **듣기** 버튼) |
| **자리 착석** | 지금 PC 앞에 있음, 긴 알람을 짧게(1~3회), **다른 소리로 대체** + 대체 소리 선택 + 듣기 |
| **방해금지** | 사용 여부, 시작/종료 시각, 그래도 통과시킬 레벨, Windows 방해 금지 존중, 음소거 중 긴급 허용 |
| **최근 알림** | **보관할 알림 수**(20~1000), **에이전트 앱의 스레드 이름 표시**, 최근 알림 열기, 비우기 |
| **정보** | 버전, 데이터 폴더, 로컬 API 주소, CLI 경로, 문서 폴더 |

여는 법: 메뉴의 "옵션..." · 최근 알림 창의 톱니 버튼 · `notipet open settings` · `NotipetTray.exe --settings`(바로가기로 만들어 둘 수 있다).

### 언어

옵션 → 일반 → 언어. **바꾸는 즉시** 트레이 메뉴, 툴팁, 열려 있는 창, notipet 자체 알림이 그 언어로 바뀐다(재시작 불필요). "Windows 설정 따름"은 Windows 표시 언어가 한국어면 한국어, 아니면 영어.

알림 **내용**(제목·본문)은 보내는 쪽이 정한다 — 에이전트 스킬은 사용자가 쓰는 언어로 쓰도록 되어 있다. CLI 출력은 영어다.

### PC 앞에 있음

긴 알람은 자리로 불러들이기 위한 것이다. 이미 화면을 보고 있을 때는 소음일 뿐이다. 켜 두면:

1. **짧게** — 반복 알람과 "확인할 때까지" 알람이 정해진 횟수(기본 1회)만 울린다.
2. **다른 소리로 대체** (옵션에서 켰을 때) — 모든 레벨의 소리를 고른 소리 하나로 바꾼다. 5초짜리 알람 대신 짧은 알림음 같은 식. **"소리 없음"**을 고르면 소리 없이 알림만 뜬다.

둘은 같이 적용된다. 응답 `warnings`에 `shortened: at desk` / `replaced: at desk` / `silenced: at desk`가 찍혀서 왜 그렇게 울렸는지 확인할 수 있다.

켜고 끄는 법: 트레이 메뉴 · 옵션 → 자리 착석 · `notipet desk on|off|toggle`.

유휴 타이머로 자동 판단하지 않는 이유는, 문서를 읽고 있는 것과 자리를 비운 것을 타이머가 구분하지 못하기 때문이다. 자동 모드는 [IDEAS.md](IDEAS.md)에 제안으로 올려 두었다. **자리를 뜰 때 끄는 것만 기억하면 된다** — 켜 둔 채 나가면 자리를 비운 사이 오는 알림도 짧게 울린다.

### 최근 알림 보관

옵션 → 최근 알림 → 보관할 알림 수. 새 알림이 오면 오래된 것부터 빠지고, 줄이면 **즉시** 그 수로 잘린다.

**에이전트 앱의 스레드 이름 표시**(기본 켬)는 카드의 스레드 이름을 앱이 붙인 이름으로 바꿔 보여 준다. Codex는 `~/.codex/session_index.jsonl`, Claude는 `~/.claude/sessions/*.json`을 **읽기만** 한다(문서화되지 않은 형식이라 바뀌면 조용히 보낸 쪽 이름으로 돌아간다). 알림이 소리를 낸 **뒤에** 따로 돌기 때문에 알림을 늦추지 않는다.

기록은 **메모리에만** 있다 — notipet을 종료하거나 재시작하면 사라진다. 본문에 프롬프트 내용이 들어갈 수 있어서 기본으로 디스크에 쓰지 않는다. 재시작 후에도 유지하는 옵트인 옵션은 [IDEAS.md](IDEAS.md)에 제안으로 있다.

### 아이콘 색이 상태다

| 모양 | 의미 |
|---|---|
| 파란 종 | 대기 중 (정상) |
| 빨간 종 | 알람이 울리는 중 |
| 회색 종 + 빗금 | 음소거 |
| 남색 종 | 방해금지 시간대 |
| **주황 종 + !** | **오디오 장치 없음** — 알림은 뜨지만 소리가 안 난다 |

빗금과 `!`는 종에서 **투명하게 파낸** 모양이라 작업 표시줄이 밝든 어둡든 보인다. 툴팁에 포트, 상태(PC 앞이면 `· PC 앞`), 최근 알림 한 줄이 나온다.

---

## 7. 문제 해결

### 소리가 안 난다

**먼저 `notipet doctor`.** 데몬, 사운드 엔진, 음소거, 방해금지, 훅·스킬 설치 여부를 한 번에 본다. 그래도 모르겠으면 순서대로 확인한다.

**1) 트레이 아이콘이 주황색인가** → 오디오 장치 문제다. 장치를 다시 연결하면 **재시작 없이** 다음 알림부터 복구된다.

```powershell
.\bin\notipet.exe status      # sound engine 줄을 본다
```

**2) 사운드만 단독으로 테스트한다** (HTTP도 트레이도 거치지 않는다):

```powershell
.\bin\NotipetTray.exe --test-sound attention
```

여기서 안 나면 사운드 계층 문제, 나면 규칙에 막힌 것이다.

**3) 히스토리에서 이유를 본다.** 억제된 알림은 이유가 기록된다:

```powershell
$rt = Get-Content "$env:LOCALAPPDATA\notipet\runtime.json" | ConvertFrom-Json
curl.exe -s -H "Authorization: Bearer $($rt.token)" "$($rt.baseUrl)/v1/history?limit=10"
```

| `suppressedReason` | 뜻 |
|---|---|
| `muted` | 음소거 중 |
| `quiet_hours` | 방해금지 시간대 안이었다 |
| `focus_assist` | 방해금지가 켜져 있고 + 윈도우가 방해 금지/프레젠테이션 모드였다 |
| `deduped` | 같은 대화에서 같은 태그로 이미 울렸다 (기본 30초 이내) |
| `rate_limited` | 너무 많이 왔다 |
| `source_disabled` | 해당 소스가 `settings.json`에서 꺼져 있다 |
| `all_channels_disabled` | 사운드/트레이 채널이 둘 다 꺼져 있다 |

### `focus_assist`로 차단됐다

이건 **`quietHours.enabled`가 `true`일 때만** 일어난다. 방해금지를 켜 둔 상태에서 윈도우가 **방해 금지** 또는 **프레젠테이션 모드**였다는 뜻이다.

세 가지 선택지가 있다:

```jsonc
// 1) 윈도우 방해 금지를 무시하고 항상 알린다
{ "quietHours": { "respectFocusAssist": false } }

// 2) 방해금지 시간대 자체를 끈다
{ "quietHours": { "enabled": false } }

// 3) 방해 금지 중에도 통과시킬 레벨을 낮춘다 (기본은 critical만)
{ "quietHours": { "allowLevels": ["attention"] } }
```

3번이 보통 원하는 답이다 — 밤에 잡다한 알림은 막되 "에이전트가 나를 기다린다"는 통과시킨다.

전체화면 앱(게임, 전체화면 터미널, 전체화면 브라우저)은 **차단 사유가 아니다.** 사용자가 직접 켠 방해 금지와 프레젠테이션 모드만 센다.

> 이전 버전에는 `quietHours.enabled`가 `false`여도 Focus Assist가 `critical` 미만을 전부 먹는 버그가 있었다. 설정을 만진 적이 없는데 이 사유로 알림을 잃었다면 그 버그다 — 고쳐졌고 [regression.md](regression.md)에 기록돼 있다.

### 알림이 너무 자주 온다

`settings.json`에서 조인다:

```json
{ "rateLimit": { "perSource": { "capacity": 3, "refillPerMinute": 4 } },
  "dedupe": { "windowSec": 60 } }
```

또는 훅 설정에서 `Notification`만 남기고 `Stop` / `SubagentStop`을 뺀다.

반대로 `attention`의 2회 반복이 성가시면 `sound.byLevel.attention.repeat`를 `"once"`로 바꾼다.

### `notipet: daemon not running`

```powershell
.\bin\notipet.exe ping --no-launch
```

- `not running (no runtime.json)` → 한 번도 안 떴다. `.\bin\NotipetTray.exe` 실행
- `not running (stale runtime.json, pid N)` → 비정상 종료로 파일만 남았다. 그냥 다시 띄우면 정리된다

### 알람이 안 멈춘다

```powershell
.\bin\notipet.exe ack --all
```

트레이 아이콘 클릭이나 풍선 클릭도 같은 일을 한다. 어느 쪽도 안 되면 최대 지속 시간(기본 120초)에 자동으로 멈춘다.

### 탐색기가 죽은 뒤 트레이 아이콘이 사라졌다

자동으로 돌아온다(`TaskbarCreated` 처리). 안 돌아오면 데몬을 재시작한다.

### 로그

```
%LOCALAPPDATA%\notipet\crash.log
```

> `--self-test`를 돌리면 여기에 `Channel:stub / stub failure` 항목이 남는다. **정상이다** — 채널 격리 검사가 일부러 예외를 던진 기록이다.

---

## 8. 직접 HTTP로 부르기

에이전트가 아닌 다른 것(빌드 스크립트, 감시 작업 등)에서 쓰고 싶을 때.

```powershell
$rt = Get-Content "$env:LOCALAPPDATA\notipet\runtime.json" | ConvertFrom-Json
$body = '{"title":"CI","body":"main 빌드 실패","level":"error","tag":"ci:main"}'
$body | Set-Content "$env:TEMP\n.json" -Encoding utf8

curl.exe -s -X POST "$($rt.baseUrl)/v1/notify" `
  -H "Authorization: Bearer $($rt.token)" `
  -H "Content-Type: application/json" `
  --data-binary "@$env:TEMP\n.json"
```

알아둘 것:

- `127.0.0.1`에만 바인딩된다. 다른 머신에서는 못 부른다.
- **`Origin` 헤더가 붙으면 거부한다.** 웹페이지가 로컬 API를 치는 걸 막기 위한 것이고, 토큰이 어딘가로 새더라도 이 검사는 계속 동작한다.
- 억제도 **HTTP 200**이다. `accepted: false`와 `suppressedReason`으로 판단한다. 비-2xx는 진짜 오류(인증, JSON 파싱, 크기 초과)일 때만 나온다.

라우트 전체와 필드 설명은 [api.md](api.md).

---

## 9. 아직 없는 것

| 기능 | 상태 |
|---|---|
| 버튼 있는 리치 토스트 | 2단계 |
| 알람 정지 전역 단축키 | 2단계 |
| 사운드 라이브러리(내 파일) 관리 UI | 2단계 — 지금은 `settings.json`의 `sound.library`에 직접 등록 |
| 아이폰 푸시 | 3단계 — 후보 비교는 [TODO.md](TODO.md) |
| 부재 감지 기반 라우팅 | 3단계 — 현재는 `/v1/health`에 기록만 |
