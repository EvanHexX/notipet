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
| POST | `/v1/test` | 필수 | 테스트 알림 (`?level=`) |
| GET | `/v1/channels` | 필수 | 채널 상태 |
| GET | `/v1/history` | 필수 | 최근 기록 (`?limit=`) |
| DELETE | `/v1/history` | 필수 | 기록 전부 삭제 (`POST /v1/history/clear`도 같음) |
| POST | `/v1/presence` | 필수 | 자리 착석 설정 `{atDesk: true|false}`, 비우면 토글 |
| POST | `/v1/shutdown` | 필수 | 데몬 종료 (응답을 보낸 뒤 종료) |
| POST | `/hooks/claude-code` | 필수 | Claude Code 훅 JSON 원본 |
| POST | `/hooks/codex` | 필수 | Codex 훅 JSON 원본 |
| POST | `/hooks/agent` | 필수 | 소스를 추론해서 처리 |
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
  "ttlSec": 300
}
```

- `title`과 `body` 중 **하나만 있으면 된다.** 나머지는 전부 선택.
- 나머지 값은 요청 → `settings.sound.byLevel[레벨]` → 코드 기본값 순으로 채워진다.
- **모르는 최상위 필드는 거부하지 않고 무시한다.** 신버전 CLI가 구버전 데몬을 깨뜨리면 안 되기 때문.
- 상한을 넘으면 거부가 아니라 **절단 + `warnings[]` 기록**: `title` 200자, `body` 2000자, `tag` 128자, 본문 전체 64KB(`server.maxBodyBytes`).
- 모르는 `level`은 `info`로 낮추고 경고를 남긴다.
- `channels`는 범위를 **좁히기만** 한다. 사용자가 끈 채널을 요청으로 켤 수는 없다.
- `sound.file`은 `settings.sound.allowedRoots` 안으로 해석될 때만 허용된다. 로컬 프로세스가 데몬으로 임의 파일을 열게 해선 안 되기 때문.

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
    { "channel": "windows_sound", "status": "delivered", "referenceId": "alm_0002" },
    { "channel": "tray_balloon",  "status": "delivered" }
  ]
}
```

`deliveries[].status` ∈ `delivered | skipped | failed | disabled | pending`

`suppressedReason` ∈ `ttl_expired | source_disabled | muted | quiet_hours | focus_assist | deduped | rate_limited | all_channels_disabled`

`warnings`에는 규칙이 소리를 바꾼 흔적도 들어간다: `shortened: at desk`, `replaced: at desk`, `silenced: at desk`.

`referenceId`는 반복 알람일 때만 나온다. 1회 재생은 확인할 것이 없어서 등록되지 않는다.

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

Claude Code의 네이티브 `http` 훅이 보내는 원본 JSON을 그대로 받는다. 중간에 CLI가 없다.

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
| `UserPromptSubmit` | 지난 턴의 `Stop` 알림과 `idle_prompt` — 새 턴이 시작됐으니 "기다리는 중"이 아니다 | **없음** |
| `SessionEnd` | 위 둘 다 | 그대로 (`info`) |

`UserPromptSubmit`은 사용자가 친 것 말고도(예약 작업, 백그라운드 에이전트 보고, 다른 세션의 메시지) 발생하므로 "사용자가 봤다"의 증거로 쓰지 않는다. 스킬로 보낸 알림은 훅이 해제하지 않는다 — 에이전트가 `notipet resolve`로 직접 한다. 세션 ID가 없는 페이로드는 아무것도 해제하지 않는다. 응답: 알림이 있으면 `/v1/notify`와 같은 모양, 없으면(`UserPromptSubmit`) `/v1/resolve`의 응답.

> `http` 훅을 쓰려면 `settings.server.port`를 고정해야 한다. 기본값 `0`은 매 실행마다 포트가 바뀐다.

## `POST /v1/ack`

```json
{ "id": "alm_0002" }   // 또는 { "tag": "..." } 또는 { "all": true }
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
- 실제로 무언가 멈추거나 닫힌 알림만 기록에 `resolvedAt`이 붙고, 최근 알림 카드에 **해결됨** 칩이 생긴다.
- 해제된 알림은 중복 병합 대상에서 빠진다. 같은 태그가 30초 안에 다시 와도 새 알림으로 울린다.

```json
{ "ok": true, "resolved": 1, "alarmsStopped": 1, "popupsClosed": 1 }
```

`tag`는 보낼 때와 같은 방식으로 다듬어 비교한다(앞뒤 공백 제거, 128자). Windows 기본 알림(풍선)은 닫지 못한다 — notipet 자체 알림 창을 쓸 때만 창이 닫힌다.

## `POST /v1/mute`

```json
{ "muted": true, "minutes": 30 }
```

`minutes`가 있으면 그 시간 뒤 자동 해제(`mute.until`), 없으면 무기한(`mute.enabled`). `{"muted": false}`로 해제.

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
{ "ok": true, "name": "notipet", "version": "1.0.0", "instanceId": "4c1e..." }
```

CLI가 stale `runtime.json`을 판별하기에는 충분하고, 사용자가 무엇을 알림받는지는 새지 않는다.

인증하면 `pid`, `port`, `startedAt`, `uptimeSec`, `muted`, `atDesk`, `language`, `historyCount`, `quietHoursActive`, `presence`, `activeAlarms`, `soundEngine`, `warnings`가 추가된다.

## `GET /v1/history?limit=50`

억제된 것도 이유와 함께 들어 있다. 이런 도구에 대한 1순위 질문이 "왜 소리가 안 났지?"라서 의도적으로 남긴다.

```json
{ "entries": [
  { "id": "ntp_...", "at": "...", "level": "info", "title": "dup", "body": "same thing",
    "source": "manual", "project": "notipet", "thread": "8f3a…", "threadTitle": "project grouping",
    "tag": "smoke:dup", "count": 5, "accepted": true,
    "suppressedReason": null, "resolvedAt": null, "deliveries": [ ... ] }
] }
```

`count`는 중복 병합된 횟수다. 같은 태그로 5번 오면 소리는 1번, `count`는 5.

`resolvedAt`은 보낸 쪽이 `/v1/resolve`로 끝났다고 알렸을 때(그 시점에 알람이나 창이 아직 살아 있었을 때만) 채워진다.

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
