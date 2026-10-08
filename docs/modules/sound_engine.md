# sound_engine

## Purpose

소리를 낸다. notipet이 존재하는 이유이자 MVP의 유일한 새 의존성.

두 개의 엔진과 하나의 브리지로 되어 있다. 핵심 아이디어는 `SystemSoundCatalog`인데, 윈도우 사운드 별칭을 실제 `.wav` 경로로 바꿔줌으로써 "시스템 사운드를 쓰면 볼륨 조절 불가 / 볼륨을 쓰면 내가 만든 파일만" 이라는 양자택일을 없앤다. 사용자가 제어판에서 고른 소리를 그대로 쓰되 볼륨과 반복은 notipet이 건다.

## Related Files

- [app/Sound/ISoundEngine.cs](../../app/Sound/ISoundEngine.cs) — 엔진/핸들 인터페이스
- [app/Sound/MediaPlayerSoundEngine.cs](../../app/Sound/MediaPlayerSoundEngine.cs) — 주 엔진
- [app/Sound/PlaySoundEngine.cs](../../app/Sound/PlaySoundEngine.cs) — 폴백 엔진
- [app/Sound/SystemSoundCatalog.cs](../../app/Sound/SystemSoundCatalog.cs) — 별칭 → 경로
- [app/Sound/SoundResolver.cs](../../app/Sound/SoundResolver.cs) — 요청+설정+레벨 → `ResolvedSound`
- [app/Sound/AlarmSession.cs](../../app/Sound/AlarmSession.cs) — 반복 루프와 상한
- [app/Sound/AlarmRegistry.cs](../../app/Sound/AlarmRegistry.cs) — 울리는 알람 추적, ack 수렴
- [app/Sound/SoundService.cs](../../app/Sound/SoundService.cs) — 엔진 선택과 동시 알람 정책
- [app/Channels/WindowsSoundChannel.cs](../../app/Channels/WindowsSoundChannel.cs) — 디스패처와의 접점

## Public APIs

```csharp
SoundService(Func<AppSettings> settings)
SoundPlayOutcome Play(ResolvedSound sound, NotificationLevel level, string? tag)
SoundEngineInfo Describe()          // /v1/health 와 트레이 아이콘 상태용
void ProbeEngines()                 // WM_DEVICECHANGE 에서 호출
AlarmRegistry Alarms { get; }       // Stop(id|tag|all), Count, HasActive, Changed

ResolvedSound SoundResolver.Resolve(SoundSpec?, NotificationLevel, AppSettings, List<string> warnings)
bool SoundResolver.TryAcceptFile(string candidate, AppSettings, out string? accepted)
string? SystemSoundCatalog.ResolvePath(string? alias)
```

## Internal Flow

1. `WindowsSoundChannel.SendAsync`가 `SoundResolver.Resolve`를 부른다.
2. 음원 결정 순서: 요청의 `file` → `library[name]` → `alias`(요청 → 레벨 기본값) → 카탈로그 폴백. 각 단계가 거절되면 `warnings`에 이유가 쌓여 응답에 실린다.
3. 볼륨 = `settings.sound.volume × request.volume`, 0~1로 클램프.
4. 반복 모드/횟수/간격/상한을 요청 → 레벨 기본값 → 설정 순으로 채운다.
5. `SoundService.Play`가 `AlarmSession`을 만든다. 반복 알람이면 먼저 `AlarmRegistry.TryAdmit`으로 자리를 확보한다.
6. `AlarmSession.Start()`가 **첫 재생을 동기로** 한다 — 호출자가 "소리가 났는가"를 즉시 알아야 하기 때문. 반복 모드면 나머지를 `PeriodicTimer` 루프에 넘긴다.
7. 각 재생은 엔진 체인을 순서대로 시도한다. `IsAvailable`이 false인 엔진은 먼저 `Probe()`로 한 번 더 기회를 준다.

## State-Data Flow

```
NotifyRequest.sound ─┐
settings.sound      ─┼─> SoundResolver ─> ResolvedSound ─> AlarmSession ─> ISoundEngine ─> 스피커
레벨 기본값          ─┘                         │
                                               └─> AlarmRegistry (반복 알람만)
                                                        │
                            /v1/ack, 트레이 클릭, 풍선 클릭, 메뉴, 종료 ──> Stop()
```

`ResolvedSound`는 불변 레코드다. 한번 결정되면 재생 중에 설정이 바뀌어도 그 알람은 원래대로 끝난다.

## Important Constraints

- **`MediaPlayer.CommandManager.IsEnabled = false`는 선택이 아니다.** unpackaged 프로세스에서 기본 SMTC 통합이 시스템 미디어 전송 컨트롤 등록을 시도하다 던지거나, 던지지 않으면 사용자의 미디어 키를 가로챈다.
- `AudioCategory = MediaPlayerAudioCategory.Alerts` — 윈도우가 알림 스트림으로 취급해 음악/통화에 대해 올바르게 더킹한다.
- **지속 시간 상한은 코드에서 강제된다.** `AppSettings.AbsoluteMaxAlarmSeconds`(600초)이고, 요청의 `maxDurationSec`은 이 값을 **줄이기만** 할 수 있다. 상한은 `until_ack`뿐 아니라 모든 반복 모드에 적용된다.
- **`sound.file`은 `allowedRoots` 안에서만 허용된다.** 루프백 API 호출자가 데몬으로 임의 파일을 열게 해선 안 된다.
- **별칭에 경로 구분자가 있으면 거부한다.** 별칭은 HTTP 호출자에게서 올 수 있고, 레지스트리 키 경로를 벗어나면 안 된다.
- **`intervalMs`는 소리가 끝난 뒤의 공백이지 재생 주기가 아니다.** 루프는 `ISoundHandle.Completion`을 기다린 뒤 간격만큼 쉬고 다시 재생한다. 고정 주기로 돌렸더니 간격보다 긴 소리가 잘려서 계속 다시 시작됐다 — 기본 알람이 5초짜리라 정확히 그 상태였고, [regression.md](../regression.md)에 기록돼 있다.
- `MediaPlayerSoundEngine`은 `MediaEnded`/`MediaFailed`/`Stop`에서 완료를 알린다. winmm에는 완료 콜백이 없어 `PlaySoundEngine`은 고정 추정치(2.5초)로 완료시킨다 — 근사값이고, 그래서 폴백 엔진의 반복 타이밍은 주 엔진보다 느슨하다.
- 반복에 `IsLoopingEnabled`를 쓰지 않는다. 제어 가능한 정적(silence)이 핵심이고, 틈 없는 루프는 사이렌이다.
- 1회 재생은 `AlarmRegistry`에 등록되지 않는다. 누가 멈추기 전에 끝나므로 확인할 것이 없다.
- 동시 알람 최대 3개. 4번째가 더 시끄러우면 가장 조용한 것을 선점하고, 같거나 조용하면 거절된다.
- **`presence.atDesk`가 켜져 있으면 반복 알람이 짧아진다.** `SoundResolver.Resolve(..., atDesk)`에서 처리하고 응답 `warnings`에 `shortened: at desk`를 남긴다. 긴 알람은 자리로 부르기 위한 것이라, 이미 화면을 보고 있을 때는 소음이다.
- `--self-test`는 **절대 소리를 내지 않는다.** 알람 로직은 `FakeEngine`으로 검증하고, 소리는 `--test-sound`로만 난다.

## Known Problems

- **`PlaySoundEngine`은 볼륨을 전혀 제어하지 못한다.** winmm에 그런 API가 없다. 폴백으로 떨어지면 소리 크기가 설정과 달라지는데, 이건 조용한 동작 차이라 응답 `warnings`로 드러내는 게 맞다 — 아직 안 하고 있다.
- **winmm은 프로세스당 재생 슬롯이 하나다.** 폴백 엔진은 동시에 한 소리만 낼 수 있고, 하나를 멈추면 전부 멈춘다. 폴백으로서는 수용 가능하지만 주 엔진이었다면 불가능했을 것이다.
- 오디오 장치가 재생 도중 사라지면 `MediaFailed`가 뜨고 그 회차는 조용히 끝난다. 다음 회차에서 폴백으로 넘어간다.

## Regression Notes

- 풍선 알림이 `NIIF_NOSOUND` 없이 뜨면 셸이 자체 알림음을 얹어 **모든 알림이 이중으로 울린다.** 사운드 쪽이 아니라 [tray_shell](tray_shell.md) 쪽 규칙이지만 증상은 여기서 나타난다.
- 오디오 장치를 전부 비활성화한 뒤 다시 켰을 때 **재시작 없이** 복구되는지 확인한다. 실패를 영구 래치하지 않는 것이 요점이고, 블루투스 헤드셋 재연결이 흔한 경우다.

## Rejected Approaches

- **`System.Media.SoundPlayer`** — `.wav` 전용, 볼륨 없음, 게다가 .NET에서는 `System.Windows.Extensions` NuGet이 필요하다. 더 약한 API에 의존성만 느는 조합.
- **NAudio** — 장치 열거·믹싱·포맷 변환이 필요해지면 값을 하지만 MVP는 그 중 아무것도 필요 없다. 출력 장치별 라우팅 요구가 생기면 재검토.
- **`PlaySound`를 주 엔진으로** — 별칭 재생이 간단해서 매력적이지만 볼륨 제어가 없고 실패 신호도 없다. `SystemSoundCatalog`가 별칭 문제를 해결하면서 이 선택지의 유일한 장점이 사라졌다.
- **`MediaPlayer.IsLoopingEnabled`로 반복** — 간격을 제어할 수 없다.

## TODO

- 폴백 엔진으로 떨어졌을 때 "볼륨이 적용되지 않음"을 응답 `warnings`에 넣는다
- `sound.library` 관리 UI (2단계)
- 출력 장치 선택 (NAudio 재검토 트리거)
