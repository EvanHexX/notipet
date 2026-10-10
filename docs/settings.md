# settings.json

## 위치

`%LOCALAPPDATA%\notipet\settings.json`.

exe 옆에 `settings.json`이 있으면 그쪽을 우선한다(포터블 모드). quota-scope는 항상 exe 옆에 두지만 그건 배포된 앱의 호환성 고정이지 원칙이 아니고, `Program Files`에 설치되면 깨진다.

같은 폴더의 다른 파일: `runtime.json`(포트·토큰), `crash.log`, `sounds\`.

설정 창(트레이 → 설정...)에서 대부분을 바꿀 수 있고 바꾸는 즉시 저장된다. 설정 창에 없는 것(레이트 리밋, 중복 병합 창, 포트, `sound.library` 등)은 직접 편집하고 `notipet restart` 한다 — 실행 중인 데몬은 메모리의 설정을 쓰고, 다음 저장 때 파일을 덮어쓰기 때문이다.

설정은 **불러온 경로로만 저장된다**(`SourcePath`). 테스트가 만든 설정 인스턴스는 저장 자체가 되지 않는다 — self-test가 실제 설정을 덮어쓴 사고 이후의 규칙이다([regression.md](regression.md)).

파일이 없으면 기본값으로 동작한다. **저장은 트레이 메뉴나 API가 설정을 바꿀 때만 일어난다** — 손으로 편집한 파일이 재시작 때 덮어써지지는 않지만, 트레이에서 음소거를 누르면 저장된다.

## 전체 스키마

```jsonc
{
  "schemaVersion": 1,
  "language": "System",              // System | EN | KO. 설정 창에서 바꾸면 즉시 적용
  "theme": "System",                 // System(Windows 따름) | Light | Dark. 창·알림 창·트레이 메뉴. 즉시 적용
  "autostart": false,                // 트레이 메뉴와 동기화됨

  "server": {
    "port": 0,                       // 0 = 임의 포트. http 훅을 쓰면 고정할 것
    "requireAuth": true,
    "maxBodyBytes": 65536,
    "remoteTimeoutMs": 2000          // 원격 채널 1개당 예산 (3단계)
  },

  "sound": {
    "enabled": true,
    "volume": 0.7,                   // 요청의 volume과 곱해진다
    "engine": "auto",                // auto | mediaplayer | playsound
    "maxDurationSec": 120,           // 반복 알람의 상한. 옵션: 30 60 120 180 300 600, 0=무제한
    "stopOnTrayClick": true,
    "library": {                     // 이름 → 파일. 요청에서 sound.name으로 참조
      "alarm2": "%LOCALAPPDATA%\\notipet\\sounds\\alarm2.mp3"
    },
    "allowedRoots": [                // sound.file이 이 안에 있어야 허용
      "%LOCALAPPDATA%\\notipet\\sounds",
      "%SystemRoot%\\Media"
    ],
    "byLevel": {
      "info":      { "alias": "Notification.Default",       "repeat": "once" },
      "success":   { "alias": "Notification.IM",            "repeat": "once" },
      "attention": { "alias": "Notification.Looping.Alarm", "repeat": "repeat",
                     "repeatCount": 2, "intervalMs": 700 },   // 공백, 주기 아님
      "warn":      { "alias": "SystemExclamation",          "repeat": "once" },
      "error":     { "alias": "SystemHand",                 "repeat": "repeat",
                     "repeatCount": 3, "intervalMs": 700 },
      "critical":  { "alias": "SystemExclamation",          "repeat": "until_ack",
                     "intervalMs": 1500 }
    }
  },

  "mute": {
    "enabled": false,
    "until": null,                   // ISO 8601. 트레이의 "30분간 음소거"가 쓴다
    "allowCritical": true            // 음소거 중에도 critical은 통과
  },

  "quietHours": {
    "enabled": false,
    "start": "23:00",                // HH:mm, 로컬 시간
    "end": "08:00",                  // 자정을 넘는 구간을 제대로 처리한다
    "allowLevels": ["critical"],     // 하한(floor)이다. "warn"이면 warn 이상 통과
    "respectFocusAssist": true       // enabled가 true일 때만 적용된다
  },

  "rateLimit": {
    "enabled": true,
    "perSource": { "capacity": 5,  "refillPerMinute": 10 },
    "global":    { "capacity": 20, "refillPerMinute": 60 }
  },

  "dedupe": {
    "enabled": true,
    "windowSec": 30,
    "collapse": true                 // true면 기존 기록의 count만 올린다
  },

  "presence": {
    "idleThresholdSec": 300,         // 이보다 오래 입력이 없으면 Away
    "pollSec": 15,
    "atDesk": false,                 // 트레이 메뉴 "PC 앞에 있음"과 동기화
    "shortenAlarmsAtDesk": true,     // 앞에 있을 때 긴 알람 -> 짧은 알람
    "atDeskMaxRepeats": 1,           // 짧게 줄일 때 재생 횟수
    "replaceSoundAtDesk": false,     // 앞에 있을 때 모든 레벨의 소리를 대체
    "atDeskSound": "Notification.Default"  // 별칭 | "library:<이름>" | "none"
  },

  "channels": {
    "windows_sound": { "enabled": true },
    "tray_balloon":  { "enabled": true },
    "windows_toast": { "enabled": false },   // 2단계
    "mobile_push":   { "enabled": false, "outboundNetworkApproved": false,
                       "provider": "", "endpoint": "", "key": "" }   // 3단계
  },

  "sources": {
    "claude-code": { "enabled": true, "levelFloor": null },
    "codex":       { "enabled": true, "levelFloor": null },
    "manual":      { "enabled": true }
  },

  "popup": {                         // notipet 자체 알림 창 (둘 중 하나라도 켜면 트레이 알림 대신)
    "stayLevels": [],                // 클릭·닫기 전까지 유지할 레벨, 예: ["attention", "error", "critical"]
                                     // (1.2.0의 "stayUntilClicked": true는 모든 레벨로 한 번 옮겨 읽는다)
    "showOverFullscreen": false,     // 전체화면 앱 위에도 표시
    "timeoutSec": 8                  // 유지하지 않을 때 스스로 닫히기까지 (3~120)
  },

  "links": {                         // `send --open`으로 카드가 열 수 있는 것
    "allowedSchemes": []             // 이 PC의 http(s)는 항상 허용. 그 밖의 URI 스킴은 여기 적은 것만, 예: ["codexbridge"]
                                     // file, ms-*, search-ms, shell, javascript, vbscript, data, http/https 는 적어도 무시된다
                                     // API로는 바꿀 수 없다 — notipet stop → 이 파일 수정 → notipet start
  },

  "alerts": {                        // 어떤 프로젝트·스레드의 에이전트 알림이 울릴지 (설정 → 프로젝트·스레드)
    "mode": "all",                   // all: 끈 것만 빼고 전부 | selected: 켠 것만
    "projects": [                    // 프로젝트 이름(최근 알림 창의 묶음 이름)별 규칙
      { "key": "shop", "on": false, "since": "2026-10-11T09:00:00+09:00" }
    ],
    "threads": [                     // 스레드 id별 규칙. 프로젝트 규칙보다 우선. 최대 200개(오래된 것부터 빠짐)
      { "key": "019a2b3c-...", "agent": "codex", "on": true, "label": "checkout", "project": "shop", "since": "..." }
    ]
  },                                 // 수동 알림과 critical은 항상 울린다. API·CLI(`notipet alerts`)로도 바꿀 수 있다

  "updates": {                       // 설치판에서만 쓰인다
    "autoCheck": false,              // 하루 한 번 GitHub에 새 버전이 있는지만 묻는다. 설정 창에서만 켠다 (API 불가)
    "lastAutoCheck": null,           // 마지막 자동 확인 시각 (자동)
    "notifiedVersion": null          // 이미 알린 새 버전 — 같은 버전은 한 번만 알린다 (자동)
  },

  "history": {
    "keepInMemory": 200,             // 설정 창: 20 50 100 200 500 1000. 줄이면 즉시 잘린다
    "lookupThreadTitles": true,      // 카드에 에이전트 앱의 스레드 이름 표시 (Codex/Claude 로컬 파일 읽기 전용)
    "persist": true                  // history.json에 저장해 재시작·업데이트·재부팅 뒤에도 유지. 끄면 메모리에만, 파일 삭제
  }
}
```

## 알아둘 것

### 모르는 키는 보존된다

루트에 `[JsonExtensionData]`가 있어서, 구버전 notipet이 신버전이 쓴 키를 조용히 지우지 않는다. quota-scope의 `AppSettings`는 왕복하면 모르는 키를 잃는데, 그 동작은 일부러 물려받지 않았다.

### 상한은 코드에서 강제된다

`Normalize()`가 로드 직후에 값을 조인다. 특히 `sound.maxDurationSec`은 **설정 값과 무관하게** `AbsoluteMaxAlarmSeconds`(600초)를 넘을 수 없고, 요청의 `sound.maxDurationSec`은 이 값을 줄이기만 할 수 있다. 잘못 구성된 훅이 `until_ack`을 루프로 쏘면 자리를 비운 사이 사이렌이 계속 울리게 되기 때문이다.

깨진 JSON이나 없는 섹션은 예외가 아니라 기본값이 된다. 알림 데몬이 설정 파일 때문에 안 뜨는 건 최악이다.

### `sound.maxDurationSec = 0` — 무제한

`0`이면 **`until_ack` 알람에 한해 시간 제한이 사라진다.** 멈추는 경로는 그대로다: 트레이 아이콘 클릭, 풍선 클릭, 트레이 메뉴의 알람 정지, `notipet ack`, 앱 종료.

`0`이어도 다음은 여전히 제한된다.

| | 결과 |
|---|---|
| `repeat` / `once` 알람 | 횟수로 끝나고, 지속 시간은 코드 상한 600초 |
| 요청이 `sound.maxDurationSec`을 지정한 경우 | 그 값(최대 600초). 요청은 줄이기만 한다 |
| PC 앞에 있음 + 짧게 줄이기 | 30초 — 무제한은 빈 자리를 위한 것이지 앉아 있는 사람을 위한 게 아니다 |

이 선택지는 2분 상한 때문에 `attention` 알람이 사용자가 돌아오기 2분 전에 꺼져 있던 일 때문에 생겼다([regression.md](regression.md)).

### 설정 창의 "재생 횟수"

설정 창은 `repeat`와 `repeatCount`를 하나의 선택지로 보여 준다: `1회` = `once`, `2~10회` = `repeat` + `repeatCount`, `확인할 때까지` = `until_ack`. "확인할 때까지"는 트레이 클릭·풍선 클릭·`notipet ack`로 멈추며, **`maxDurationSec`(최대 10분)을 넘으면 스스로 멈춘다.**

### `intervalMs`는 주기가 아니라 **소리가 끝난 뒤의 공백**이다

5초짜리 알람에 `intervalMs: 700`이면 5초 재생 → 0.7초 침묵 → 5초 재생이다. 0.7초마다 다시 시작하는 게 아니다. (예전에는 주기였고, 그래서 긴 소리가 잘려서 계속 다시 시작됐다 — [regression.md](regression.md) 참고.)

윈도우 기본 소리 길이 참고: `Notification.Looping.Alarm` 5초, `SystemHand` 2초, 나머지 대부분 1초.

### `presence.atDesk` / `shortenAlarmsAtDesk`

`atDesk`는 트레이 메뉴의 **"PC 앞에 있음"** 체크와 같은 값이다. 유휴 타이머로 추론하지 않고 수동 스위치로 둔 이유는, 화면을 읽고 있는 것과 자리를 비운 것을 타이머가 구분하지 못하고 양쪽으로 틀릴 때마다 다른 방식으로 성가시기 때문이다.

`atDesk`가 켜져 있고 `shortenAlarmsAtDesk`가 true면 반복 알람이 짧아진다:

| 원래 | `atDeskMaxRepeats: 1` | `atDeskMaxRepeats: 2` |
|---|---|---|
| `until_ack` | 1회 재생 | 2회 재생 |
| `repeat` x3 | 1회 재생 | 2회 재생 |
| `once` | 그대로 | 그대로 |

줄어든 알람은 지속 시간도 30초로 제한된다. 응답의 `warnings`에 `shortened: at desk`가 찍히므로 왜 짧아졌는지 확인할 수 있다.

### `presence.replaceSoundAtDesk` / `atDeskSound`

`atDesk`가 켜져 있고 `replaceSoundAtDesk`가 true면 **모든 레벨의 소리를 `atDeskSound` 하나로** 바꾼다. 짧게 줄이기와 함께 적용된다. 설정 창의 "PC 앞에 있음" 페이지에 있다.

| `atDeskSound` | 결과 |
|---|---|
| `Notification.Default` 등 윈도우 별칭 | 그 소리 |
| `library:<이름>` | `sound.library`에 등록한 파일 |
| `none` | **소리 없음** — 알림만 뜬다. 사운드 채널은 `skipped` (실패 아님), 경고 `silenced: at desk` |

해석되지 않는 값(없는 별칭·라이브러리 이름)이면 **원래 소리를 유지한다.** 조용하라고 한 적 없는데 조용해지면 안 되기 때문이다. 대체되면 경고 `replaced: at desk`.

### `sound.byLevel`의 `alias`

`HKCU\AppEvents\Schemes\Apps\.Default\<alias>\.Current`를 읽어 실제 `.wav` 경로로 바꾼다. 즉 **사용자가 제어판에서 고른 윈도우 소리를 그대로 쓰되, 볼륨과 반복은 notipet이 건다.** 별칭이 해석되지 않으면 폴백 엔진이 별칭 그대로 재생한다.

쓸 만한 별칭: `Notification.Default`, `Notification.IM`, `Notification.Looping.Alarm`, `Notification.Looping.Call`, `Notification.Reminder`, `SystemAsterisk`, `SystemExclamation`, `SystemHand`, `SystemNotification`.

자기 파일을 쓰려면 `%LOCALAPPDATA%\notipet\sounds\`에 넣고 `library`에 이름을 등록한 뒤 요청에서 `sound.name`으로 부르거나, `byLevel`에서 `alias` 대신 `file`을 쓴다.

### `quietHours.respectFocusAssist`는 `enabled`에 종속이다

`quietHours.enabled`가 `false`면 이 값이 `true`여도 **아무것도 차단하지 않는다.** 방해금지를 켜지 않은 사용자가 알림을 잃는 일은 없어야 하기 때문이다. (예전에는 독립적으로 평가돼서 실제로 알림을 먹었다 — [regression.md](regression.md) 참고.)

켰을 때 차단하는 것은 **사용자가 명시적으로 고른** 두 상태뿐이다:

| 윈도우 상태 | 차단? |
|---|---|
| 방해 금지 / 집중 세션 (`QUNS_QUIET_TIME`) | O |
| 프레젠테이션 모드 (`QUNS_PRESENTATION_MODE`) | O |
| 전체화면 앱 실행 중 (`QUNS_BUSY`) | **X** |
| 전체화면 게임 (`QUNS_RUNNING_D3D_FULL_SCREEN`) | **X** |

전체화면 터미널로 빌드를 보고 있으면 자리에 **있는** 것이다. 그때 알림을 죽이는 건 반대로 가는 일이라 제외했다.

### `quietHours.allowLevels`는 하한이다

`["error"]`로 두면 error와 critical이 통과한다. 정확히 일치하는 레벨만 통과시키는 게 아니다.

`start`와 `end`가 같으면 **아무것도 막지 않는다**. `start == end`로 앱을 영원히 침묵시키려는 사람은 없다고 봤다.

### `server.port`

기본값 `0`은 매 실행마다 임의 포트를 잡는다. 충돌이 사실상 불가능해서 기본값으로 좋지만, Claude Code의 `http` 훅처럼 고정 URL이 필요한 경우엔 `47811` 같은 값으로 고정해야 한다. 고정한 포트가 점유돼 있으면 `port`~`port+9`를 시도하고, 그래도 안 되면 임의 포트로 떨어진 뒤 트레이 풍선과 `/v1/health`의 `warnings`로 알린다. **포트 충돌로 시작 실패하지는 않는다.**

### `channels.mobile_push.outboundNetworkApproved`

notipet에서 유일하게 외부로 나가는 경로의 잠금장치다. `enabled`와 이 값이 **둘 다** true여야 하고, 이 값은 설정 UI의 명시적 확인 대화상자로만 켤 수 있다 — `PATCH /v1/settings`로는 불가능하다. 로컬 프로세스도 에이전트도 이 게이트를 뒤집을 수 없어야 하기 때문이다. 3단계 전까지는 켤 방법 자체가 없다.

### `sources[].levelFloor`

아직 읽히지 않는다(모델에만 있음). 소스별 최소 레벨 필터로 2단계에 연결할 자리다.
