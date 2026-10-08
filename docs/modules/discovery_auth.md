# discovery_auth

## Purpose

CLI와 훅이 실행 중인 데몬을 찾게 하고, 찾은 뒤에 아무나 그 API를 쓰지 못하게 한다.

이 문서의 절반은 **무엇을 막지 못하는지**에 대한 것이다. 그게 정직한 위협 모델이고, 정직하지 않은 위협 모델은 없는 것보다 나쁘다.

## Related Files

- [core/Http/AuthGuard.cs](../../core/Http/AuthGuard.cs) — 토큰, Origin/Sec-Fetch-Site/Host 검사
- [core/Http/RuntimeFile.cs](../../core/Http/RuntimeFile.cs) — `runtime.json` 쓰기/삭제/stale 판정
- [core/Paths.cs](../../core/Paths.cs) — 데이터 디렉터리와 DACL
- [cli/RuntimeDiscovery.cs](../../cli/RuntimeDiscovery.cs) — CLI 쪽 탐색과 자동 기동
- [shared/Wire.cs](../../shared/Wire.cs) — `RuntimeInfo`

## Public APIs

```csharp
string AuthGuard.GenerateToken()
bool   AuthGuard.IsAuthenticated(HttpListenerRequest)
void   AuthGuard.Require(HttpListenerRequest)          // 아니면 401을 던진다
bool   AuthGuard.PassesBrowserChecks(HttpListenerRequest)
void   AuthGuard.RequireJsonContentType(HttpListenerRequest)

RuntimeInfo RuntimeFile.Describe(port, token, instanceId, sessionId, version)
void        RuntimeFile.Write(RuntimeInfo)
void        RuntimeFile.Delete(int sessionId)
bool        RuntimeFile.IsStale(RuntimeInfo?)

Task<Daemon?> RuntimeDiscovery.FindAsync(TimeSpan timeout)
Task<Daemon?> RuntimeDiscovery.FindOrLaunchAsync(bool allowLaunch, TimeSpan budget)
```

## Internal Flow

### 기동

1. 단일 인스턴스 뮤텍스를 잡는다 (`Local\Notipet.SingleInstance`).
2. 토큰을 생성한다: `RandomNumberGenerator.GetBytes(32)` → Base64Url.
3. 리스너를 **먼저 바인딩**한다.
4. 바인딩된 포트로 `AuthGuard`를 만든다 (Host 검사가 포트를 알아야 한다).
5. **그다음에** `runtime.json`을 쓴다. 갖고 있지 않은 포트를 광고하지 않는다.
6. 쓰기는 원자적으로: `.tmp`에 쓰고 `File.Move(overwrite: true)`.

### 탐색 (CLI)

파일 없음 → `Process.GetProcessById(pid)` 실패 → **`process.StartTime != processStartTimeUtc`** → `GET /v1/health` 실패 또는 `instanceId` 불일치.

세 번째가 핵심이다. 크래시로 남은 파일의 pid를 OS가 무관한 프로세스에 재사용해 주면, 이 검사가 없는 구현은 엉뚱한 프로그램에 말을 건다. 대부분 이걸 빠뜨린다.

## State-Data Flow

```
데몬 기동 ──> 리스너 바인딩 ──> runtime.json (원자적 쓰기)
                                      │
              CLI ──> 세션별 파일 우선 ──> pid 확인 ──> 시작 시각 확인 ──> /v1/health
                                                                            │
                                                          instanceId 일치 ──> Daemon
정상 종료 / ProcessExit ──> runtime.json 삭제
```

`%LOCALAPPDATA%\notipet\runtime.json` 내용:

```json
{ "schemaVersion": 1, "instanceId": "...", "pid": 7032,
  "processStartTimeUtc": "2026-09-20T07:31:49.03+00:00", "sessionId": 1,
  "port": 5523, "baseUrl": "http://127.0.0.1:5523", "token": "...",
  "version": "1.0.0", "exePath": "...", "startedAtUtc": "..." }
```

## Important Constraints

### 위협 모델 — 루프백은 신뢰 경계가 아니다

같은 사용자로 도는 아무 프로세스나 `runtime.json`을 읽을 수 있고, 따라서 토큰을 읽을 수 있다. **DPAPI는 도움이 안 된다** — 같은 사용자면 그대로 복호화된다.

토큰이 실제로 막는 것은 두 가지고, 둘 다 현실적인 위협이다:

1. **브라우저발 CSRF / DNS 리바인딩.** 방문한 웹페이지가 `fetch('http://127.0.0.1:5523/v1/notify', {method:'POST'})`를 할 수 있다. 토큰이 없으면 그게 통한다.
2. **같은 머신의 다른 사용자 계정 / 저무결성 프로세스.** 데이터 디렉터리를 현재 사용자 SID만 허용하는 명시적 DACL로 만드는 이유다.

### 적용되는 검사

- `Authorization: Bearer <token>`, `CryptographicOperations.FixedTimeEquals`로 비교 (타이밍 공격 방지)
- 본문이 있으면 `Content-Type: application/json` 필수 — 브라우저의 simple request 경로를 막는다
- **`Origin` 헤더가 있거나 `Sec-Fetch-Site != none`이면 거부.** 브라우저는 항상 보내고 CLI는 절대 안 보낸다. 이게 (1)을 실제로 죽이는 검사이고, **토큰이 어딘가 로그로 새더라도 계속 동작한다**
- `Host`가 `127.0.0.1:<port>` / `localhost:<port>` / `[::1]:<port>`인지 검증 — DNS 리바인딩 방어
- `RemoteEndPoint`가 루프백인지 확인 — 바인딩상 불가능해야 하지만 가정하지 않고 단언한다
- **CORS 헤더는 어떤 응답에도 붙이지 않는다**

이 검사들은 미인증 `/v1/health`에도 적용된다. 웹페이지가 API 존재 여부조차 알아내지 못하게.

### 그 외

- 미인증 `/v1/health`는 `{ok, name, version, instanceId}`만 준다. CLI가 stale 파일을 판별하기엔 충분하고, 사용자가 무엇을 알림받는지는 새지 않는다.
- `Local\` 뮤텍스는 **로그온 세션 범위**다. RDP + 콘솔이 동시에 붙으면 데몬이 둘 뜬다. 그건 올바른 동작이지만 `%LOCALAPPDATA%`를 공유하므로 `runtime-{sessionId}.json`을 함께 쓰고, CLI는 자기 세션 파일을 우선한다.
- 포트 충돌로 **시작 실패하지 않는다.** 고정 포트가 점유되면 `port`~`port+9` → 임의 포트 순으로 떨어지고 경고를 남긴다. 조용히 안 뜨는 알림 데몬이 최악이다.

## Known Problems

- **토큰이 평문 파일에 있다.** 같은 사용자 루프백 IPC의 본질적 한계이고, 완화책은 위 검사들과 이 문서다.
- 다중 세션 동작을 실제 RDP로 검증하지 않았다.
- 토큰 회전 경로가 아직 없다 (2단계).
- `IsStale`이 판정 불가(접근 거부 등)일 때는 "살아 있음"으로 처리하고 health 검사에 맡긴다. 동작하는 인스턴스를 죽이는 것보다 낫다는 판단.

## Regression Notes

- 작업 관리자로 강제 종료한 뒤 `notipet ping --no-launch`가 `stale runtime.json, pid N`을 보고해야 한다. `--no-launch` 없이는 새 인스턴스를 띄워야 한다.
- 유효한 토큰 + `Origin: http://evil.test` → **401**. 이게 깨지면 브라우저 CSRF 방어가 사라진 것이다. `HttpSelfTest`가 고정한다.
- 미인증 `/v1/health` 응답에 `soundEngine`이 들어가면 안 된다.

## Rejected Approaches

- **토큰을 DPAPI로 보호** — 같은 사용자 컨텍스트에서는 아무것도 막지 못한다. 보안이 늘어난 것처럼 보이게만 만든다.
- **토큰 없이 루프백만 신뢰** — 브라우저 CSRF가 그대로 통한다.
- **포트 0을 `HttpListener`에 직접** — 지원하지 않는다. `TcpListener`로 빈 포트를 뽑아 넘기고, 그 사이 경합에 대비해 재시도한다.
- **`netsh http add urlacl` + `http://+:port/`** — 관리자 권한이 필요하고, 애초에 오프머신 노출은 원하지 않는다.
- **고정 포트를 기본값으로** — 충돌 처리가 항상 필요해진다. 임의 포트를 기본으로 두고 `http` 훅을 쓰는 사람만 고정하게 했다.

## TODO

- 트레이 메뉴에서 토큰 회전
- 다중 세션 RDP 검증
