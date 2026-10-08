# channels

## Purpose

알림이 나가는 출구를 균일한 인터페이스 뒤에 둔다. 사운드, 트레이 알림, 그리고 나중의 리치 토스트와 모바일 푸시가 같은 방식으로 꽂힌다.

## Related Files

- [core/Channels/INotificationChannel.cs](../../core/Channels/INotificationChannel.cs)
- [app/Channels/WindowsSoundChannel.cs](../../app/Channels/WindowsSoundChannel.cs)
- [app/Channels/TrayBalloonChannel.cs](../../app/Channels/TrayBalloonChannel.cs)
- [core/Core/Dispatcher.cs](../../core/Core/Dispatcher.cs) — 선택과 실행

## Public APIs

```csharp
interface INotificationChannel
{
    string Id { get; }  string DisplayName { get; }
    ChannelCapabilities Capabilities { get; }   // Sound|Visual|Remote|Actions|Ack
    bool IsEnabled { get; }   // 설정에서 켜져 있는가
    bool IsReady { get; }     // 실제로 쓸 수 있는가
    string? LastError { get; }
    bool IsRemote => Capabilities.HasFlag(Remote);
    Task<ChannelResult> SendAsync(NotificationEnvelope, CancellationToken);
    Task AckAsync(string? referenceId, CancellationToken);
    ChannelInfo Describe();
}
```

`IsEnabled`와 `IsReady`를 나눈 이유: "사용자가 껐다"와 "켜져 있지만 오디오 장치가 없다"는 다른 상황이고, 후자는 이유와 함께 드러나야 한다.

## Internal Flow

`Dispatcher.DispatchAsync`:

1. 규칙 평가 → `Continue`면 계속
2. 채널 선택: 요청의 `channels` ∩ 활성 채널. 요청은 범위를 **좁히기만** 한다
3. **로컬 채널 먼저, 원격 채널 마지막**으로 정렬
4. 채널마다 개별 `try/catch`
5. 집계 → 히스토리 기록 → 응답

## State-Data Flow

```
NotificationEnvelope ──> SelectChannels ──> [로컬...] ──> [원격...]
                                               │              │
                                        예산 없음      RemoteTimeoutMs
                                               │              │
                                         ChannelResult ──> deliveries[]
```

## Important Constraints

- **원격 채널 실패가 소리를 막는 일은 구조적으로 불가능하다.** 원격 채널을 건드릴 시점에는 사운드 채널이 이미 완료돼 있기 때문이다. 약속이 아니라 정렬 순서로 보장된다.
- **채널은 계약상 던지지 않지만, 디스패처는 그래도 감싼다.** 계약은 강제 수단이 아니고, 채널 하나가 망가져도 나머지는 나가야 한다.
- 원격 채널은 자체 `CancellationTokenSource(RemoteTimeoutMs)`를 받고, 초과하면 `pending`으로 응답한다. 훅은 에이전트의 임계 경로에 있다.
- 요청의 `channels`로 **사용자가 끈 채널을 켤 수 없다.**
- `TrayBalloonChannel`은 UI 스레드로 마샬링한다. `Shell_NotifyIcon`은 창을 만든 스레드에서만 호출할 수 있다.
- 3단계의 `MobilePushChannel`은 `enabled && outboundNetworkApproved`가 둘 다 필요하고, 후자는 설정 UI로만 켜진다. 로컬 프로세스도 에이전트도 그 게이트를 뒤집을 수 없어야 한다.

## Known Problems

- `pending` 상태의 배달은 나중에 히스토리가 갱신되어야 하는데 그 경로가 아직 없다 (원격 채널이 없으므로 현재는 도달 불가).
- 채널별 활성/비활성은 설정 파일을 손으로 고쳐야 한다 (UI는 2단계).

## Regression Notes

- 채널이 예외를 던져도 요청은 **200**이고 그 채널만 `failed`여야 한다. `HttpSelfTest`가 스텁으로 고정한다.
- 사운드 채널이 실패해도 트레이 알림은 떠야 한다.

## Rejected Approaches

- **채널 병렬 실행** — 원격이 로컬보다 먼저 끝날 수 있게 되고, 순서로 얻던 보장이 사라진다.
- **실패 시 예외 전파** — 한 채널의 문제가 요청 전체의 실패가 된다.

## TODO

- `WindowsToastChannel` (2단계, 기본 off)
- `MobilePushChannel` (3단계, 결정 문서 승인 후)
