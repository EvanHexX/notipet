# HTTP API

`http://127.0.0.1:<port>` 에만 바인딩된다. 포트와 토큰은 데몬이 `%LOCALAPPDATA%\notipet\runtime.json`에 쓴다.

정의: [shared/Wire.cs](../shared/Wire.cs) · 라우트: [core/Http/ApiRoutes.cs](../core/Http/ApiRoutes.cs)

## 모든 요청에 적용되는 규칙

- `Authorization: Bearer <token>` — `/v1/health`를 제외한 전부에 필요
- `Content-Type: application/json` — 본문이 있는 요청에 필요
- `Origin` 헤더가 있거나 `Sec-Fetch-Site`가 `none`이 아니면 **거부**
- `Host`가 `127.0.0.1:<port>` / `localhost:<port>`가 아니면 **거부**
- CORS 헤더는 어떤 응답에도 붙지 않는다

이유는 [modules/discovery_auth.md](modules/discovery_auth.md)에 있다. 요약하면: 토큰은 브라우저발 CSRF와 다른 사용자 계정을 막고, `Origin` 검사는 토큰이 로그로 새더라도 전자를 계속 막는다.

## 라우트

| Method | Path | 인증 | 용도 |
|---|---|---|---|
| GET | `/v1/health`, `/health`, `/healthz` | 선택 | 생존 확인 |
| POST | `/v1/notify` | 필수 | 메인 진입점 |
| POST | `/v1/ack` | 필수 | 울리는 알람 정지 |
| POST | `/v1/resolve` | 필수 | "그 일은 끝났다" — 지목한 알림의 알람만 정지 + 알림 창만 닫기 |
| POST | `/v1/mute` | 필수 | 음소거 켜기/끄기 |
| GET / POST | `/v1/alerts` | 필수 | 어떤 프로젝트·스레드가 울릴지 (모드, 프로젝트·스레드 규칙) |
| POST | `/v1/test` | 필수 | 테스트 알림 (`?level=`) |
| GET | `/v1/channels` | 필수 | 채널 상태 |
| GET | `/v1/history` | 필수 | 최근 기록 (`?limit=`) |
| DELETE | `/v1/history` | 필수 | 기록 전부 삭제 (`POST /v1/history/clear`도 같음) |
| POST | `/v1/presence` | 필수 | 자리 착석 설정 `{atDesk: true|false}`, 비우면 토글 |
| POST | `/v1/shutdown` | 필수 | 데몬 종료 (응답을 보낸 뒤 종료) |
| POST | `/hooks/claude-code` | 필수 | Claude Code 훅 JSON 원본 (토큰을 읽는 클라이언트용, 아래) |
| POST | `/hooks/codex` | 필수 | Codex 훅 JSON 원본 (같음) |
| POST | `/hooks/agent` | 필수 | 소스를 추론해서 처리 (같음) |
| * | 그 외 | — | `404 {"error":"not_found"}` |

## `POST /v1/notify`

```json
{
  "title": "Build finished",
  "body": "12 tests passed in 34s",
  "level": "info|success|attention|warn|error|critical",
  "tag": "claude-code:8f3a12345678:Stop",
  "collapse": true,
  "source": { "id": "claude-code", "label": "Claude Code",
              "session": "8f3a", "cwd": "C:\\src\\notipet",
              "project": "notipet", "threadTitle": "project grouping",
              "hostSession": "local_6f1c…", "client": "claude-desktop" },
  "sound": { "name": null, "alias": "Notification.Default", "file": null,
             "volume": 0.8, "repeat": "once|repeat|until_ack",
             "repeatCount": 3, "intervalMs": 1500, "maxDurationSec": 120,
             "mute": false },
  "channels": ["windows_sound", "tray_balloon"],
  "ttlSec": 300,
  "open": "codexbridge://approve/42"
}
```

- `title`과 `body` 중 **하나만 있으면 된다.** 나머지는 전부 선택.
- 나머지 값은 요청 → `settings.sound.byLevel[레벨]` → 코드 기본값 순으로 채워진다.
- **모르는 최상위 필드는 거부하지 않고 무시한다.** 신버전 CLI가 구버전 데몬을 깨뜨리면 안 되기 때문.
- 상한을 넘으면 거부가 아니라 **절단 + `warnings[]` 기록**: `title` 200자, `body` 2000자, `tag` 128자, 본문 전체 64KB(`server.maxBodyBytes`).
- 모르는 `level`은 `info`로 낮추고 경고를 남긴다.
- `channels`는 범위를 **좁히기만** 한다. 사용자가 끈 채널을 요청으로 켤 수는 없다.
- `sound.file`은 `settings.sound.allowedRoots` 안으로 해석될 때만 허용된다. 로컬 프로세스가 데몬으로 임의 파일을 열게 해선 안 되기 때문.
- `open`은 카드(최근 알림 카드, notipet 알림 창)를 클릭하면 **스레드 대신 열 URI**다. 셸에 URI로만 넘기고 명령줄로는 절대 쓰지 않는다. 허용되는 것:
  - 이 PC의 `http`/`https` (`127.0.0.1`, `localhost`, `[::1]`)
  - `settings.links.allowedSchemes`에 등록된 스킴 (예: `codexbridge`). 이 목록은 settings.json에서만 바뀌고 API로는 못 바꾼다
  - 등록해도 항상 거부: `file`, `ms-*`, `search-ms`, `shell`, `javascript`, `vbscript`, `data` 등. 경로(`C:\…`, `\\server\…`)는 `file:`로 해석돼 거부된다
  - 2048자 이하, 제어 문자 없음
  - 공백, `"`, `<`, `>`, `^`, `|`, `` ` ``가 없을 것, http(s)가 아니면 `&`도 없을 것(1.11.1). 셸은 링크를 핸들러의 명령줄에 쓰인 그대로 넣으므로, 공백이나 따옴표가 있으면 링크가 핸들러에 인수를 더할 수 있고, cmd로 도는 핸들러는 `<>^|&`를 명령으로 읽는다. 진짜 URI는 이스케이프한다(`%20`, `%22`, `%26`). http(s)의 쿼리 문자열은 `&`를 그대로 쓴다
  - 안 맞으면 **알림은 그대로 보내고 `open`만 버린다** — `warnings`에 `open ignored: <이유>`. 4xx가 아니다
  - 클릭할 때 지금 설정으로 한 번 더 확인한다. 목록에서 뺀 스킴은 이미 있는 카드에서도 열리지 않는다
- `untilReply: true`(1.8.0)면 **사용자가 그 스레드에서 답하는 순간 끝나는 알림**이 된다. 질문용이다. 답으로 치는 것은 두 가지다.
  - 그 스레드에 사용자가 직접 친 프롬프트(`UserPromptSubmit`). summit 중계(`[agent-summit]…`)와 `<task-notification>` 같은 시스템 입력은 빼고 센다.
  - 에이전트 질문 도구의 답: Claude `AskUserQuestion`/`ExitPlanMode`, Codex `request_user_input`의 `PostToolUse`.
  - 답이 오면 훅이 `/v1/resolve`에 `untilReply: true`로 끝내고, 그 알림을 "답함"으로 읽음 처리한다. 스레드(`source.session`)가 없으면 무시하고 `warnings`에 남긴다.
- `ttlSec`은 받아 두지만 **지금은 아무 효과가 없다.** 만료 기준 시각이 데몬이 요청을 받은 순간이고 규칙이 그 자리에서 바로 돌기 때문에, 만료될 틈이 없다. 그래서 `ttl_expired`도 실제로는 나오지 않는다. 대기열이 생길 때를 위해 필드를 남겨 둔 것이다.

### `source` — 누가, 어디서

전부 선택이다. **어느 것이 빠지거나 이상해도 요청은 실패하지 않는다** — 그 값만 버리고(필요하면 `warnings`에 기록) 최근 알림 창에서 "기타"로 분류된다.

| 필드 | 뜻 | 없을 때 / 이상할 때 |
|---|---|---|
| `id` | 에이전트. `claude-code` \| `codex` \| `manual` \| 그 외. `claude`, `Codex` 같은 표기도 정규화된다 | `manual` |
| `project` | 최근 알림 창의 그룹 이름 | `cwd`의 마지막 폴더(Claude 워크트리 `\.claude\worktrees\x`는 저장소 이름으로). 드라이브 루트면 없음 → "기타". 80자 초과 시 줄임 + 경고 |
| `session` | 스레드 ID (Claude 세션 ID / Codex 스레드 ID) | `[A-Za-z0-9._:-]` 128자 이내가 아니면 **버린다**(`thread ignored: invalid id`). 딥링크에 들어가므로 자르거나 고치지 않는다 |
| `threadTitle` | 스레드 이름 | 없음. 120자 초과 시 줄임 + 경고 |
| `hostSession` | Claude Desktop의 세션 ID(`local_…`). `claude://` 링크가 받는 값 | 형식이 다르면 버린다 |
| `client` | 실행한 프런트엔드 (`claude-desktop`, `codex-tui` …) | 없음 |
| `cwd` | 작업 폴더 | 없음 |

딥링크 URL은 **받지 않는다.** 데몬이 검증된 ID로 직접 만든다 — 토큰을 가진 로컬 프로세스가 클릭 한 번을 임의 프로토콜 실행으로 바꾸지 못하게 하기 위해서다. 만드는 링크는 `codex://threads/<uuid>`와 `claude://code/continue?session=local_…` 둘뿐이다.

중복 병합 키에는 스레드(`session`)가 들어간다. 태그가 있으면 `태그|스레드`, 없으면 내용 + `project` + `session`. 그래서 두 대화가 같은 태그(`shop:needs-input`)나 같은 문구("테스트 통과")를 보내도 합쳐지지 않는다 — 합쳐지면 두 번째는 소리도 카드도 없고, 남은 카드의 링크는 첫 번째 대화를 연다. 스레드를 모르면 예전처럼 태그만으로 병합한다. 훅 태그에는 이미 세션이 들어 있어 동작이 같다.

병합은 같은 키의 카드가 마지막으로 온 지 `dedupe.windowSec`(30초) 안일 때만 일어난다. 창은 카드가 처음 온 때가 아니라 마지막으로 온 때부터 재므로, 더 새 카드들 뒤에 있는 오래된 카드도 방금 다시 왔으면 병합 대상이다. 다음 카드는 같은 키로 뒤에 오는 알림을 삼키지 않는다(1.11.1).

- **끝난 카드.** `/v1/resolve`나 훅이 지목한 카드는 그 순간 소리가 이미 끝났어도 끝난 것이다. 턴 완료 알림이 다음 프롬프트로 끝나면, 다음 턴의 완료 알림은 30초 안에 와도 울린다.
- **사용자가 바꿀 수 있는 상태 때문에 막힌 카드.** 음소거, 방해금지, 꺼 둔 스레드·프로젝트 같은 것이다. 그 상태를 풀고 나서 오는 같은 알림은 울려야 한다. 예외는 `rate_limited`와 `deduped`(`dedupe.collapse`를 끈 경우 버려진 반복)다. 둘 다 같은 폭주에 속하므로 계속 한 카드로 접힌다. resolve가 이름 댄 것은 막혔던 기록이어도 끝난 것으로 친다.
- **더 높은 레벨.** 같은 키로 `error` 뒤에 `critical`이 오면 새 알림으로 울린다. 같거나 낮은 레벨은 접힌다.

### 응답

성공과 억제 **모두 HTTP 200**이다. 방해금지는 클라이언트 오류가 아니고, 비-2xx를 본 훅이 에이전트 동작을 바꿀 수 있다.

```json
{
  "ok": true,
  "id": "ntp_20260920073203_000a",
  "accepted": true,
  "suppressed": false,
  "suppressedReason": null,
  "collapsedWith": null,
  "nextAllowedAt": null,
  "level": "attention",
  "receivedAt": "2026-09-20T16:32:03.5729090+09:00",
  "warnings": [],
  "deliveries": [
    { "channel": "windows_sound", "status": "delivered", "referenceId": "alm_3f9c2a_0002" },
    { "channel": "tray_balloon",  "status": "delivered" }
  ]
}
```

`deliveries[].status` ∈ `delivered | skipped | failed | disabled | pending`

`suppressedReason` ∈ `ttl_expired | source_disabled | thread_off | thread_event_off | project_off | not_selected | muted | quiet_hours | focus_assist | deduped | rate_limited | all_channels_disabled` (`ttl_expired`는 지금 나오지 않는다 — 위 `ttlSec`)

`warnings`에는 규칙이 소리를 바꾼 흔적도 들어간다: `shortened: at desk`, `replaced: at desk`, `silenced: at desk`.

`referenceId`는 반복 알람일 때만 나온다. 1회 재생은 확인할 것이 없어서 등록되지 않는다.

알람 id는 `alm_<실행마다 새로 뽑는 16진수 6자리>_<순번>`이다(1.11.1). 예전에는 `alm_0002`처럼 순번뿐이어서 재시작하면 다시 1부터 붙었고, 기록에서 되살린 지난 실행의 카드를 지목한 resolve가 같은 번호를 받은 새 알람을 멈췄다(스레드 A의 `Stop` 훅이 스레드 B의 알람을 끈 일). 지금은 `history.json`에서 되살린 카드에 소리 전달은 남지만 `referenceId`는 없다 — 지난 실행의 알람은 이미 없다.

**429는 쓰지 않는다.** 레이트 리밋도 `suppressed`로 표현한다.

### 오류

| 상태 | `error` | 언제 |
|---|---|---|
| 400 | `invalid_json` | 본문이 JSON이 아님 |
| 400 | `validation_failed` | `title`과 `body`가 둘 다 비었거나 `Content-Type`이 틀림 |
| 401 | `unauthorized` | 토큰 없음/틀림, `Origin` 있음, `Host` 불일치 |
| 413 | `payload_too_large` | `server.maxBodyBytes` 초과 |
| 404 | `not_found` | 모르는 경로 |

## `POST /hooks/claude-code`

에이전트 훅 페이로드 원본 JSON을 그대로 받는다(`/hooks/codex`, `/hooks/agent`도 같다). 훅이 부르는 CLI(`notipet.exe`)와 같은 매핑을 쓴다.

**토큰을 `runtime.json`에서 읽어 보내는 클라이언트만 쓸 수 있다.** 다른 라우트처럼 토큰이 필요한데, 토큰은 데몬이 시작할 때마다 새로 생기고 `runtime.json`에만 있다. 그래서 Claude Code `settings.json`에 적어 두는 `http` 훅은 쓸 수 없다 — 붙여 넣은 http 훅은 매번 401이었고, 1.11.1부터 `install-hooks`는 그런 블록을 출력하지 않는다. 포트를 고정해도 마찬가지다. 훅은 `notipet.exe`(command 훅)로 건다.

CLI를 거칠 때와 다른 점:

- `/hooks/codex`는 Codex `config.toml`의 `approvals_reviewer`를 읽지 않는다. 검토자가 켜져 있어 사용자에게 묻지 않는 `PermissionRequest`도 울린다. CLI 훅은 이를 확인하고 아무것도 하지 않는다.
- 본문은 엄격하게 읽는다. 필드 타입이 바뀐 페이로드(문자열 자리에 객체·배열)는 `400 invalid_json`이다. CLI는 문자열 필드만 골라 읽고 그대로 처리한다(1.11.1).

```json
{ "session_id": "8f3a", "hook_event_name": "Notification",
  "notification_type": "agent_needs_input", "cwd": "C:\\src\\notipet" }
```

→ `level: attention`, `title: "Claude Code - notipet"`, `tag: "claude-code:8f3a:Notification:agent_needs_input"`

매핑 규칙 전체는 [shared/PayloadMapper.cs](../shared/PayloadMapper.cs)에 있고, 그 클래스의 `RunSelfTest()`가 표를 고정한다. 모르는 이벤트는 `info` + 원본 이벤트명이 본문에 들어간다 — 절대 400이 되지 않는다.

훅 이벤트 중 몇 개는 **알림을 띄우기 전에 끝난 것을 먼저 해제한다** (`PayloadMapper.PlanForHookEvent`, 아래 `/v1/resolve`와 같은 동작). 같은 에이전트·같은 세션의, 정확히 그 태그만:

| 이벤트 | 해제하는 것 | 알림 |
|---|---|---|
| `Stop`, `StopFailure` | 이 세션의 권한 요청(`Notification:permission_prompt`, `elicitation_dialog`, `elicitation_url_dialog`, Codex `PermissionRequest`) — 턴이 끝났으면 이미 답했다는 뜻 | 그대로 (`success` / `error`) |
| `UserPromptSubmit` | 지난 턴의 `Stop` 알림과 `idle_prompt` — 새 턴이 시작됐으니 "기다리는 중"이 아니다. 사용자가 친 프롬프트면 이 세션에 `untilReply`로 보낸 알림도 끝내고 "답함"으로 읽음 처리한다 | **없음** |
| `PostToolUse` — 질문 도구의 답(Claude `AskUserQuestion`/`ExitPlanMode`, Codex `request_user_input`) | 이 세션의 권한 요청(`Stop`과 같은 태그)과 `untilReply` 알림. 둘 다 "답함"으로 읽음 처리한다 — 턴이 끝날 때까지 기다리지 않는다 | **없음** |
| `SessionEnd` | 권한 요청과 지난 턴의 `Stop`·`idle_prompt` (답으로 치지 않는다) | 그대로 (`info`) |
| Codex 질문 창 — `PreToolUse`(`request_user_input`), `PostToolUse`(`request_user_input_async`) (1.9.0) | 없음 | `attention`, 본문은 질문 문장, 태그 `codex:<스레드>:Question`, `untilReply` — 사용자의 답이 끝낸다 |

그 밖의 도구에 대한 `PreToolUse`·`PostToolUse`는 아무것도 하지 않는다. Codex의 몇 이벤트도 아무것도 하지 않는다: 데스크톱 앱의 입력 제안 턴(`Stop`의 마지막 메시지가 `{"suggestion": ...}`/`{"suggestions": [...]}`), 앱 자체 폴더(`%LOCALAPPDATA%\OpenAI\Codex\...`)를 `cwd`로 하는 이벤트, 그리고 CLI를 거칠 때만 `approvals_reviewer`가 `user`가 아닐 때의 `PermissionRequest`(위).

`UserPromptSubmit`은 사용자가 친 것 말고도(예약 작업, 백그라운드 에이전트 보고, 다른 세션의 메시지) 발생하므로 "사용자가 봤다"의 증거로 쓰지 않는다. 답으로 치는 것은 사용자가 친 것으로 보이는 프롬프트뿐이다: `[agent-summit]` 중계와 `<task-notification>`처럼 `<`로 시작하는 시스템 입력은 빼고, Codex 비동기 질문 창의 답(`<send_user_message_question_reply>`)은 넣는다. 스킬로 보낸 알림은 훅이 해제하지 않는다 — 에이전트가 `notipet resolve`로 직접 한다. `--until-reply`(`untilReply`)로 보낸 것만 예외로, 사용자의 답이 끝낸다. 세션 ID가 없는 페이로드는 아무것도 해제하지 않는다. 응답: 알림이 있으면 `/v1/notify`와 같은 모양, 없으면(`UserPromptSubmit`, `PostToolUse`) `/v1/resolve`의 응답.

## `POST /v1/ack`

```json
{ "id": "alm_3f9c2a_0002" }   // 또는 { "tag": "..." } 또는 { "all": true }
```

선택자를 하나도 주지 않으면 전부 정지한다. 급해서 이 엔드포인트를 찾은 사람이 원하는 게 그거다.

```json
{ "ok": true, "stopped": 1 }
```

## `POST /v1/resolve`

"내가 알린 그 일은 끝났다." 원격에서 답했거나 에이전트가 스스로 해결했을 때, 자리에서 계속 울리는 알람을 보낸 쪽이 끈다.

```json
{ "source": "claude-code", "session": "0b7c3a5e-…", "tag": "api:needs-input" }
{ "id": "ntp_…" }
{ "source": "codex", "session": "019a…", "tags": ["codex:019a…:PermissionRequest"] }
```

- **지목한 것만.** 주어진 선택자를 **모두** 만족하는 알림(`id` / `tag`·`tags` 중 하나 / `source` / `session`)의 알람(소리 전달의 `referenceId`)을 멈추고 그 알림 창을 닫는다. "외 N개" 카드에 접혀 있던 것이면 수에서 뺀다. 다른 알람·창은 건드리지 않는다.
- **선택자가 없으면 400.** `source`만 있어도 400이다("Codex가 보낸 전부"는 끝난 일 하나가 아니다). 전부 끄기는 `/v1/ack`.
- **이미 끝났으면 조용히 통과.** 사용자가 먼저 알람을 끄고 창을 닫았으면 아무것도 바꾸지 않고 `200`에 0을 돌려준다. 보내는 쪽은 확인 없이 보내면 된다.
- 억제(차단)된 알림은 울린 적이 없으므로 대상이 아니다.
- 실제로 무언가 멈추거나 닫힌 알림만 기록에 `resolvedAt`이 붙고, 최근 알림 카드에 **해결됨** 칩이 생긴다. 응답의 `resolved`도 그 수다.
- 지목된 알림은 그 순간 살아 있었든 아니든 **끝난 것**으로 기록되고(1.11.1, `history.json`의 `endedAt`. API에는 나오지 않는다), 중복 병합 대상에서 빠진다. 소리가 이미 끝난 턴 완료 알림도 그렇다 — 같은 태그가 30초 안에 다시 와도 새 알림으로 울린다.
- 알람은 소리 전달의 `referenceId`로 찾는다. 재시작 전 실행의 카드에는 그것이 없으므로(위 `/v1/notify` 응답), 그 카드를 지목해도 새 알람은 멈추지 않는다.
- `untilReply: true`(1.8.0)는 다른 선택자처럼 범위를 좁힌다. `untilReply`로 보낸 알림만 대상이다.
- `seenBy: "reply"`(1.8.0): 사용자가 답했다는 뜻이다. 맞은 알림은 그 순간 살아 있었는지와 상관없이 "답함"으로 읽음 처리한다. 훅이 보낸다.

```json
{ "ok": true, "resolved": 1, "alarmsStopped": 1, "popupsClosed": 1 }
```

`tag`는 보낼 때와 같은 방식으로 다듬어 비교한다(앞뒤 공백 제거, 128자). Windows 기본 알림(풍선)은 닫지 못한다 — notipet 자체 알림 창을 쓸 때만 창이 닫힌다.

## `POST /v1/mute`

```json
{ "muted": true, "minutes": 30 }
```

`minutes`가 있으면 그 시간 뒤 자동 해제(`mute.until`), 없으면 무기한(`mute.enabled`). `{"muted": false}`로 해제.

## `GET /v1/alerts`, `POST /v1/alerts`

어떤 프로젝트·스레드의 **에이전트 알림**이 울릴지(설정의 `alerts`). 판정은 **스레드 규칙 → 스레드 이벤트 규칙 → 프로젝트 규칙 → 모드** 순이고, 수동 알림(`source.id` = `manual`)과 `critical`은 항상 울린다. 막힌 알림은 다른 억제처럼 **200**에 `suppressedReason`: `thread_off` | `thread_event_off` | `project_off` | `not_selected`.

```json
// POST: 모드 바꾸기와 규칙 하나 바꾸기를 함께 또는 따로. 아무것도 없으면 읽기만.
{ "mode": "selected" }                                         // all | selected
{ "target": "thread", "state": "off", "agent": "codex", "thread": "019a2b3c-..." }
{ "target": "project", "state": "on", "project": "shop" }       // state: on | off | reset
{ "target": "thread", "state": "off", "agent": "codex", "thread": "019a2b3c-...",
  "events": ["Stop", "SubagentStop"], "label": "plan", "by": "agent-summit" }
```

**이벤트 규칙** (`events`, 1.11.0): 스레드 전체가 아니라 그 스레드의 **훅 이벤트 몇 개만** 끈다. 다른 도구가 턴마다 돌리는 대화에서 `Stop`/`SubagentStop`만 끄고 권한 요청(`PermissionRequest`, `Notification` `permission_prompt`)과 질문 창(`PreToolUse`/`PostToolUse`)은 그대로 울리게 할 때 쓴다.

- 맞춰 보는 것은 훅이 만든 알림의 `source.event`(훅 이벤트 이름, `PayloadMapper`가 채운다)뿐이다. 에이전트가 `notipet send`로 직접 보낸 알림에는 이벤트가 없어서 이벤트 규칙에 걸리지 않는다.
- `state`는 `off`(그 이벤트를 규칙에 더한다)나 `reset`(그 이벤트만 규칙에서 뺀다. 마지막 이벤트가 빠지면 규칙이 사라진다)만 된다. `on`은 400 — 스레드 전체를 켜려면 `events` 없이 `on`.
- 한 스레드에 전체 규칙과 이벤트 규칙이 함께 있을 수 있고, **전체 규칙이 이긴다**(전체 `on`이면 `Stop`도 울리고, 전체 `off`면 권한 요청도 막힌다). `events` 없는 `reset`은 그 스레드의 규칙을 **둘 다** 지운다.
- 이벤트 이름은 영문자·숫자·`_`·`-`, 대소문자 무시, 스레드당 16개까지. 잘못된 이름이나 프로젝트 규칙에 `events`를 주면 400.
- `by`는 누가 설정했는지 목록에 보이는 이름이다(설정 창, 최근 알림 ⋯ 메뉴의 "다시 울리기").
- **1.11.0보다 오래된 데몬은 `events`를 모르고 무시해서 스레드 전체를 꺼 버린다.** 먼저 `GET /v1/health`의 `features`에 `alerts.events`가 있는지 확인한다(CLI는 스스로 확인한다).

`target`을 빼면 `thread`가 있으면 스레드, 없으면 프로젝트. 스레드 규칙에는 `agent`와 `thread`가 필요하다(`label`·`project`는 목록 표시용이며, 빠지면 최근 알림에서 채운다). 잘못된 값은 `400 validation_failed`.

```
GET /v1/alerts?agent=codex&thread=019a2b3c-...&project=shop     // 이 스레드는 울리나
GET /v1/alerts?agent=codex&thread=019a2b3c-...&event=Stop       // 이 스레드의 Stop은 울리나
```

```json
{
  "ok": true, "mode": "selected", "changed": false,
  "projects": [ { "key": "shop", "on": true, "since": "..." } ],
  "threads":  [ { "key": "019a2b3c-...", "agent": "codex", "on": false, "label": "checkout", "project": "shop", "since": "..." },
                { "key": "019a2b3c-...", "agent": "codex", "on": false, "events": ["Stop", "SubagentStop"], "by": "agent-summit", "since": "..." } ],
  "rings": false, "because": "thread_off"
}
```

`because`: `thread_on` | `thread_off` | `thread_event_off`(읽을 때 `event`를 준 경우) | `project_on` | `project_off` | `mode_all` | `not_selected`. 음소거처럼 API로 바꿀 수 있다 — 사용자 자신의 에이전트를 조용하게 또는 시끄럽게 할 뿐이다.

## `POST /v1/presence`

```json
{ "atDesk": true }      // 또는 false, 또는 {} (토글)
```

```json
{ "ok": true, "atDesk": true }
```

트레이 메뉴의 "PC 앞에 있음"과 같은 값이다. 켜져 있으면 반복 알람이 짧아지고, 설정에서 켰다면 다른 소리로 대체된다.

## `DELETE /v1/history`

```json
{ "ok": true, "cleared": 12 }
```

되돌릴 수 없다.

## `POST /v1/shutdown`

```json
{ "ok": true, "message": "shutting down" }
```

응답을 먼저 보내고 약 150ms 뒤 종료한다. `runtime.json`이 정리된다. `notipet stop`과 `publish.ps1 -Restart`가 쓴다. 다른 라우트와 같은 토큰·브라우저 검사를 거치므로 웹 페이지가 데몬을 끌 수는 없다.

## `GET /v1/health`

미인증:

```json
{ "ok": true, "name": "notipet", "version": "1.11.1", "instanceId": "4c1e...", "features": ["alerts.events"] }
```

CLI가 stale `runtime.json`을 판별하기에는 충분하고, 사용자가 무엇을 알림받는지는 새지 않는다. `features`는 오래된 데몬이 모르는 필드를 무시하는 기능의 목록이다(`alerts.events`: `/v1/alerts`의 `events`). 없으면 그 기능이 없는 데몬이다.

인증하면 `pid`, `port`, `startedAt`, `uptimeSec`, `muted`, `atDesk`, `language`, `historyCount`, `quietHoursActive`, `presence`, `activeAlarms`, `soundEngine`, `warnings`가 추가된다. `warnings`에는 고정 포트를 못 잡아 다른 포트로 떨어진 일과, settings.json을 쓸 수 없어 기본값으로 돌고 있다는 것(1.11.1, [settings.md](settings.md))이 들어간다.

## `GET /v1/history?limit=50`

억제된 것도 이유와 함께 들어 있다. 이런 도구에 대한 1순위 질문이 "왜 소리가 안 났지?"라서 의도적으로 남긴다.

```json
{ "entries": [
  { "id": "ntp_...", "at": "...", "level": "info", "title": "dup", "body": "same thing",
    "source": "manual", "project": "notipet", "thread": "8f3a…", "threadTitle": "project grouping",
    "tag": "smoke:dup", "count": 5, "accepted": true,
    "suppressedReason": null, "resolvedAt": null, "seenAt": null, "seenBy": null, "deliveries": [ ... ] }
] }
```

`count`는 중복 병합된 횟수다. 같은 태그로 5번 오면 소리는 1번, `count`는 5.

`open`은 보낸 쪽이 준 링크(받아들여진 경우)다.

`resolvedAt`은 보낸 쪽이 `/v1/resolve`로 끝났다고 알렸을 때(그 시점에 알람이나 창이 아직 살아 있었을 때만) 채워진다.

`deliveries[].referenceId`(알람 id)는 이번 실행에서 울린 카드에만 있다. `history.json`에서 되살린 카드는 `windows_sound` 전달이 그대로 보이지만 그 `referenceId`가 없다(위 `/v1/notify` 응답).

`seenAt`·`seenBy`(1.8.0)는 사용자가 그 알림을 처리한 때와 방법이다. 같은 알림이 다시 오면(중복 병합) 다시 안 읽음이 된다. `untilReply`로 보낸 알림이면 `"untilReply": true`가 붙는다.

| `seenBy` | 뜻 |
|---|---|
| `popup` | notipet 알림 창이나 Windows 풍선을 클릭했거나, 알림 창의 닫기를 눌렀다 |
| `link` | 알림 창·최근 알림 카드에서 스레드나 링크를 열었다 |
| `reply` | 그 스레드에서 답했다(`untilReply`, 질문 도구) |
| `recent` | 최근 알림 창에서 읽음으로 표시했다(하나씩, 또는 "모두 읽음") |

`threadTitle`은 에이전트 앱이 붙인 이름(Codex `session_index.jsonl`, Claude `~/.claude/sessions`)을 찾으면 그것이고, 못 찾으면 보낸 쪽이 준 이름이다. 앱 이름 조회는 전달이 끝난 뒤 따로 돌므로 직후 조회에는 아직 없을 수 있다. 설정 → 최근 알림 → "에이전트 앱의 스레드 이름 표시"로 끈다.

## curl 예시

```powershell
$rt = Get-Content "$env:LOCALAPPDATA\notipet\runtime.json" | ConvertFrom-Json
$body = '{"title":"hello","body":"from curl","level":"info"}'
$body | Set-Content "$env:TEMP\n.json" -Encoding utf8
curl.exe -s -X POST "$($rt.baseUrl)/v1/notify" `
  -H "Authorization: Bearer $($rt.token)" `
  -H "Content-Type: application/json" --data-binary "@$env:TEMP\n.json"
```
