# presence

## Purpose

사용자가 실제로 자리에 있는지 판단한다. 1단계에서는 기록만 하고 `/v1/health`로 노출한다. 3단계에서 라우팅에 쓴다 — 책상에 있으면 소리, 없으면 폰.

지금 만들어 두는 이유는 아래 Important Constraints의 첫 항목 때문이다. 틀리기 쉽고 나중에 끼워 넣기 비싸다.

## Related Files

- [app/Presence/PresenceMonitor.cs](../../app/Presence/PresenceMonitor.cs)
- [app/Tray/TrayIconHost.cs](../../app/Tray/TrayIconHost.cs) — `WM_WTSSESSION_CHANGE`를 전달
- [core/Rules/RuleEngine.cs](../../core/Rules/RuleEngine.cs) — Focus Assist 조회

## Public APIs

```csharp
PresenceMonitor(Func<int> idleThresholdSec)
PresenceState Current { get; }      // Active | Idle | Away | Locked | FullScreen
void SetLocked(bool locked)
static TimeSpan IdleTime()
static bool IsFocusAssistActive()
```

## Internal Flow

`Current`의 판단 순서: 잠김 → 전체화면/프레젠테이션 → 유휴 시간이 임계 초과면 `Away` → 1분 초과면 `Idle` → `Active`.

## State-Data Flow

```
WM_WTSSESSION_CHANGE ──> TrayIconHost ──> SetLocked(bool) ──┐
GetLastInputInfo ──────────────────────────────────────────┼─> PresenceState
SHQueryUserNotificationState ──────────────────────────────┘        │
                                                        /v1/health, (3단계) 라우팅
```

## Important Constraints

- **`GetLastInputInfo`는 잠긴 데스크톱의 입력을 보고하지 않는다.** 그래서 잠금 상태는 반드시 `WTSRegisterSessionNotification` + `WM_WTSSESSION_CHANGE`로 따로 받아야 한다. 이게 부재 감지에서 가장 많이 빠뜨리는 항목이고, 없으면 잠긴 머신이 마지막 입력 시각 기준으로 영원히 "활성"으로 보인다.
- `TrayIconHost`의 숨은 창이 **메시지 전용이 아닌 실제 최상위 창**이라서 이 메시지를 받을 수 있다. 창 종류를 바꾸면 조용히 깨진다.
- **`dwTime`은 32비트라 약 49일마다 랩어라운드한다.** `unchecked((uint)Environment.TickCount - info.dwTime)`로 uint 산술에서 빼야 결과가 맞는다. 부호 있는 연산으로 하면 엉뚱하게 음수가 나온다.
- **`IsFocusAssistActive()`는 `QUNS_QUIET_TIME`과 `QUNS_PRESENTATION_MODE`만 참으로 본다.** 둘 다 사용자가 명시적으로 한 행동이기 때문이다. 윈도우 문서가 같이 묶는 `QUNS_BUSY`(전체화면 앱이 **아무거나** 실행 중)와 `QUNS_RUNNING_D3D_FULL_SCREEN`은 제외한다 — 전체화면 터미널로 빌드를 보는 사람은 자리에 있고, 그때 알림을 죽이는 건 정확히 반대다. 전체화면이 궁금한 호출자는 `PresenceState.FullScreen`을 쓴다. 다른 질문이다.

## Known Problems

- 다중 모니터/다중 세션 환경에서 실측하지 않았다.
- `presence.pollSec` 설정이 있지만 현재는 폴링 루프가 없고 `Current`를 요청 시점에 계산한다. 3단계에서 라우팅에 쓰기 시작하면 폴링이 필요해질 수 있다.
- `FullScreen`을 `Away`처럼 다룰지 아직 정하지 않았다 — 게임 중은 자리에 있는 것이지만 방해받고 싶지는 않다.

## Regression Notes

- `SetLocked(true)`가 유휴 시간이 뭐라고 하든 `Locked`를 이겨야 한다.
- 데스크톱을 실제로 잠갔다 풀 때 `/v1/health`의 `presence`가 따라오는지 확인한다.

## Rejected Approaches

- **`GetLastInputInfo`만으로 부재 판단** — 잠금 상태를 놓친다. 위 제약 참고.
- **자체 유휴 타이머** — OS가 이미 알고 있다.

## TODO

- 3단계 라우팅 규칙: `{"when":{"presence":"Away","levelAtLeast":"attention"},"channels":["mobile_push"]}`
- `FullScreen` 정책 결정
