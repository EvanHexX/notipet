# http_api

## Purpose

루프백 전용 JSON 서버. 에이전트 훅과 CLI가 데몬에 알림을 넣는 유일한 경로.

## Related Files

- [core/Http/NotipetHttpServer.cs](../../core/Http/NotipetHttpServer.cs) — 수명, 포트 선택, 수신 루프, 라우트 테이블
- [core/Http/ApiRoutes.cs](../../core/Http/ApiRoutes.cs) — 라우트 정의와 핸들러
- [core/Http/HttpJson.cs](../../core/Http/HttpJson.cs) — JSON 읽기/쓰기, `HttpApiException`
- [core/Http/AuthGuard.cs](../../core/Http/AuthGuard.cs) — [discovery_auth](discovery_auth.md) 참고
- [core/Core/AlarmResolver.cs](../../core/Core/AlarmResolver.cs) — `/v1/resolve`와 훅의 "끝났다": 매칭, 알람 정지, 알림 창 닫기(심)
- [app/SelfTest/HttpSelfTest.cs](../../app/SelfTest/HttpSelfTest.cs) — 실제 서버 + 소켓 엔드투엔드 검사

계약 문서는 [../api.md](../api.md).

## Public APIs

```csharp
RouteTable.Map(string method, string path, RouteHandler handler)
RouteHandler? RouteTable.Resolve(string method, string path)

NotipetHttpServer(RouteTable routes, AuthGuard guard)
void Start(int preferredPort)       // 포트 충돌로 실패하지 않는다
int  Port { get; }
List<string> Warnings { get; }      // /v1/health 로도 나간다

Task HttpJson.WriteAsync<T>(ctx, status, body, JsonTypeInfo<T>)
Task<T> HttpJson.ReadAsync<T>(ctx, maxBytes, JsonTypeInfo<T>)
```

## Internal Flow

1. `Start(preferredPort)` — 고정 포트면 `port`~`port+9`를 시도하고, 전부 실패하거나 기본값 `0`이면 임의 포트를 5회까지 시도한다.
2. `GetContextAsync` 루프. 각 요청은 `Task.Run`으로 떨어져 나가므로 느린 핸들러가 수신을 막지 않는다.
3. 모든 요청에 `AuthGuard.PassesBrowserChecks` 먼저 — 미인증 health도 예외가 아니다.
4. `(method, path)`로 핸들러를 찾고, 없으면 종단 404.
5. 핸들러의 `HttpApiException`은 최상위에서 한 번에 상태 코드로 바뀐다. 그 외 예외는 `CrashLog` + 500.

## State-Data Flow

```
훅/CLI ──HTTP──> AuthGuard ──> RouteTable ──> 핸들러
                                                │
                      NotifyRequest ──> EnvelopeFactory ──> Dispatcher ──> 채널
                                                                 │
                                                          NotifyResponse (항상 200)

/v1/resolve, 훅의 PlanForHookEvent.Resolve
    ──> AlarmResolver ──> HistoryStore.Where (수락된 것, 아직 해제 안 된 것, 선택자 전부 일치)
                      ├─> AlarmRegistry.Stop(id: 소리 전달의 referenceId)
                      ├─> ApiContext.ClosePopups(알림 id들)   ── UI 스레드, 2초 상한
                      └─> HistoryStore.MarkResolved(실제로 멈췄거나 닫힌 것만)
```

훅 라우트는 **해제 먼저, 알림 나중**이다. 그래야 `Stop`이 자기 "완료" 알림을 해제하는 일이 없다.

## Important Constraints

- **`127.0.0.1`에만 바인딩한다.** 비루프백 프리픽스는 관리자 URL ACL이 필요하고, 오프머신 노출은 원하지 않는다. 바인딩 주소를 설정 가능하게 만들지 않는다.
- **억제는 200이다.** 방해금지는 클라이언트 오류가 아니고, 비-2xx를 본 훅이 에이전트 동작을 바꿀 수 있다. **429는 쓰지 않는다.**
- 상한 초과는 거부가 아니라 절단 + `warnings[]`. 예외는 본문 크기(413)뿐인데, 그건 읽기 전에 막아야 하는 것이라서.
- **모르는 최상위 필드는 무시한다.** 신버전 CLI가 구버전 데몬을 깨뜨리면 안 된다.
- **`/v1/resolve`는 지목한 것만 끈다.** 선택자가 없거나 `source`뿐이면 400이다 — `/v1/ack`처럼 "전부"로 해석하지 않는다. 보낸 쪽이 실수로 남의 알람까지 끄면, 그건 "알람이 울리지 않았다"와 같은 실패다.
- **`/v1/resolve`는 이미 끝난 것에 200 + 0이다.** 사용자가 먼저 끈 것은 오류가 아니다. 실제로 살아 있던 것만 `resolvedAt`을 받는다.
- `ContentLength64`를 믿되 그것만 믿지 않는다. 읽는 루프도 독립적으로 상한을 건다.
- 응답에 `Cache-Control: no-store`와 `X-Content-Type-Options: nosniff`를 붙인다.

## Known Problems

- **`HttpListener`는 포트 0을 바인딩하지 못한다.** `TcpListener(IPAddress.Loopback, 0)`으로 번호를 뽑고 닫은 뒤 그 번호로 바인딩하는데, 그 사이에 다른 프로세스가 가져갈 수 있는 작은 창이 있다. 5회 재시도로 덮는다.
- `HttpListener`는 향후 투자 대상이 아니라고 문서화돼 있고 `GetContextAsync`의 취소 처리가 어색하다. `INotipetHttpServer` 심 뒤에 두었으니 필요해지면 한 파일 교체다.
- 기동 시 서버를 두 번 만든다(포트를 알아내기 위한 bootstrap 후 진짜 서버). 포트 확보와 guard 생성의 순서 의존성을 푸는 방법인데, 그 사이에 아주 짧은 미바인딩 구간이 생긴다.

## Regression Notes

- `HttpSelfTest`가 401(토큰 없음/틀림/Origin/Host), 400(JSON/검증/Content-Type), 413, 404, 200(정상/억제), 채널 예외 격리를 전부 고정한다. 라우팅이나 인증을 건드리면 이 검사가 먼저 깨져야 한다.
- 고정 포트가 점유된 상태에서 기동해도 **시작에 실패하지 않고** 경고를 남기는지 확인한다.

## Rejected Approaches

- **ASP.NET Core minimal API** — 이 프로세스는 이미 self-contained WindowsAppSDK + self-contained 런타임이다. 라우트 다섯 개짜리 JSON 표면에 공유 프레임워크가 수십 MB 더 붙고, 트레이와 싸우는 두 번째 애플리케이션 수명이 생긴다. `UseWinUI=true` + `Microsoft.NET.Sdk.Web`은 지원 조합도 아니다.
- **`http://+:{port}/` 바인딩** — 비관리자로는 거부되고, 원하지도 않는다.
- **레이트 리밋에 429** — 훅이 비-2xx를 보고 오동작할 수 있어 `suppressed`로 표현한다.

## TODO

- `/v1/resolve`가 Windows 기본 알림(풍선)도 거둘 수 있는지 — `NIM_MODIFY`로 빈 `szInfo`를 보내면 사라지지만, 그 풍선이 이 알림의 것인지 알 방법이 없다

- `PATCH /v1/settings` (허용 목록 방식, `outboundNetworkApproved` 제외) — 2단계
- bootstrap 서버 이중 생성을 한 번으로 정리
