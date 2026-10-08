# rules_engine

## Purpose

알림을 채널로 보낼지, 억제할지, 기존 기록에 합칠지 결정한다.

**모든 결정을 히스토리에 남긴다.** 이런 도구에 대한 1순위 질문이 "왜 소리가 안 났지?"이고, 기록이 없으면 답이 추측이 된다.

## Related Files

- [core/Rules/RuleEngine.cs](../../core/Rules/RuleEngine.cs) — 순서, TTL/소스/음소거/방해금지/중복
- [core/Rules/RateLimitRule.cs](../../core/Rules/RateLimitRule.cs) — 토큰 버킷
- [core/Core/HistoryStore.cs](../../core/Core/HistoryStore.cs) — 중복 판정의 조회 대상
- [app/Presence/PresenceMonitor.cs](../../app/Presence/PresenceMonitor.cs) — Focus Assist 조회

## Public APIs

```csharp
RuleDecision RuleEngine.Evaluate(NotificationEnvelope, RuleContext)
// RuleOutcome: Continue | Suppress(reason, nextAllowedAt) | Collapse(into)

bool QuietHoursRule.IsWithin(TimeOnly now, TimeOnly start, TimeOnly end)
bool RateLimitRule.ShouldAnnounceThrottle(DateTimeOffset now)
```

## Internal Flow

고정 순서. 싸고 호출자 범위인 검사가 먼저라서, 꺼진 소스는 레이트 리밋 토큰을 소비하지 않는다.

1. `TtlRule` — 큐에 앉아 있다 기한이 지난 알림은 낡은 소식이다
2. `SourceRule` — `sources[id].enabled`
3. `MuteRule` — 전역 음소거와 `mute.until`. `allowCritical`이면 critical은 통과
4. `QuietHoursRule` — 시간대 + Focus Assist
5. `DedupeRule` — `tag`(없으면 내용 해시) 기준 윈도우 내 중복
6. `RateLimitRule` — 소스별 + 전역 토큰 버킷

## State-Data Flow

```
NotificationEnvelope ──> [6개 규칙 순차] ──> RuleDecision
                              │                    │
                       HistoryStore 조회      Dispatcher가 해석
                                              ├─ Continue: 채널로
                                              ├─ Collapse: 기존 기록 count++ , 무음
                                              └─ Suppress: 이유와 함께 기록, 200 응답
```

## Important Constraints

- **자정을 넘는 방해금지 구간.** `23:00`→`08:00`은 "23:00 이상 **또는** 08:00 미만"이다. 숫자 범위로 다루면 틀린다. `IsWithin`의 self-test가 23:30 / 00:30 / 07:59 / 08:01 / 12:00 / 22:59와 퇴화 케이스를 고정한다.
- `start == end`는 **아무것도 막지 않는다.** 그걸로 앱을 영원히 침묵시키려는 사람은 없다.
- `allowLevels`는 **하한(floor)**이다. `["error"]`면 error와 critical이 통과한다.
- Focus Assist는 복제하지 않고 `SHQueryUserNotificationState`로 **조회**한다. 사용자가 켠 집중 모드를 두 번 설정하게 만들지 않기 위해.
- **단, `quietHours.enabled`가 true일 때만 적용된다.** 이 설정은 `quietHours` 아래에 중첩돼 있고, 독립적으로 평가하면 방해금지를 켜지 않은 사용자가 알림을 잃는다 — 실제로 그랬고 [regression.md](../regression.md)에 기록돼 있다.
- 차단으로 치는 윈도우 상태는 **사용자가 명시적으로 고른** `QUNS_QUIET_TIME`과 `QUNS_PRESENTATION_MODE` 둘뿐이다. 전체화면 여부(`QUNS_BUSY`, `QUNS_RUNNING_D3D_FULL_SCREEN`)는 제외한다 — 전체화면 터미널로 빌드를 보는 사람은 자리에 있다.
- `critical`은 소스별 버킷을 우회하지만 **전역 버킷은 우회하지 못한다.** 한 소스가 전부 critical로 라벨링해서 머신의 주의를 독점할 수 없어야 한다.
- 레이트 리밋 안내 풍선은 10분에 최대 1회. 억제한 홍수를 "억제했습니다" 홍수로 바꾸면 의미가 없다.
- 규칙은 `Continue`가 아닌 첫 결정에서 멈춘다. 따라서 응답의 `suppressedReason`은 항상 **가장 먼저 걸린** 이유다.

## Known Problems

- `sources[].levelFloor`가 모델에만 있고 `SourceRule`이 아직 읽지 않는다.
- `PresenceRule`은 1단계에서 기록만 한다 (3단계에서 라우팅).
- 버킷 상태는 메모리에만 있다. 데몬을 재시작하면 리밋이 초기화되는데, 실용적으로는 문제가 안 된다.

## Regression Notes

- 같은 태그로 5회 → 소리 1번, 히스토리 `count` 5.
- 1.2부터 병합 키는 `NotificationEnvelope.DedupeKey` = 태그 + 스레드(있을 때). 두 대화가 같은 태그를 써도 각각 울린다. 스레드가 없으면 태그만 — 예전과 같다.
- 30건 연속 → 대부분 `rate_limited`로 억제되고 안내 풍선은 1번.
- 방해금지 중 `info`는 억제, `critical`은 통과.
- 잘못된 시간 문자열(`"not-a-time"`)이 **아무것도 침묵시키지 않는지** 확인한다.

## Rejected Approaches

- **억제에 4xx** — 훅이 비-2xx로 오동작할 수 있다.
- **고정 윈도우 카운터** — 경계에서 두 배가 통과한다. 토큰 버킷이 훅의 버스트 패턴에 맞다.
- **Focus Assist 상태를 자체 설정으로 복제** — 사용자가 같은 걸 두 번 설정하게 된다.

## TODO

- `levelFloor` 연결
- 3단계의 `PresenceRule` 라우팅
