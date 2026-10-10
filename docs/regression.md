# regression

버그와 그 재발 방지, 그리고 릴리스 전 수동 체크리스트.

## 수동 스모크 체크리스트

1~8번은 [scripts/smoke.ps1](../scripts/smoke.ps1)이 자동화한다. 9~14번은 사람이 해야 한다 — 스크립트는 소리를 들을 수 없다.

```powershell
.\scripts\smoke.ps1                                      # bin\ 기준
.\scripts\smoke.ps1 -FromBuildOutput -Configuration Debug
```

### 자동화됨

1. 실행하면 트레이 아이콘이 뜨고 `runtime.json`에 살아 있는 포트와 pid가 적힌다
2. `notipet ping` → `ok version=... port=... pid=...`
3. `notipet send`가 accepted로 돌아오고 로컬 채널에 delivered가 찍힌다
4. 인증: 토큰 없음 → 401 / 틀린 토큰 → 401 / **유효한 토큰 + `Origin` 헤더 → 401** / 정상 → 200 / 100KB 본문 → 413 / 모르는 경로 → 404 / 미인증 `/v1/health`가 상태를 흘리지 않음
5. Claude 훅 페이로드가 `attention`으로 매핑되고 제목에 저장소 이름이 들어간다
6. 같은 태그로 5번 → 소리 1번, 히스토리 `count` 5
7. 30건 연속 → 레이트 리밋이 걸린다 (기관총이 되지 않는다)
8. `until_ack` 알람이 등록되고 태그로 ack하면 멈춘다

8·9번(긴 알람의 시간을 재는 검사)은 **"PC 앞에 있음"이 켜져 있으면 `[SKIP]`**으로 건너뛴다. 그 모드는 설계상 모든 반복 알람을 한 번으로 줄이기 때문이다. 테스트가 사용자의 설정을 바꾸지는 않는다 — 돌리려면 직접 끄고 실행한다. 또 스모크를 1분 안에 두 번 돌리면 첫 번째의 7번(폭주)이 레이트 리밋 버킷을 비워 두 번째의 5·7번이 막힌다. 1분 쉬고 돌린다.

### 수동

9. **소리가 실제로 난다.** `--test-sound info` / `attention` / `critical`을 각각 돌려 레벨마다 다르게 들리는지, `attention`이 2번 반복하는지, `critical`이 계속 반복하다 `maxDurationSec`에 자동으로 멈추는지 확인
10. 방해금지 시간대를 지금 시각을 포함하도록 켜면 `info`는 `suppressedReason: quiet_hours`로 억제되고 `critical`은 통과한다
11. 트레이에서 음소거 → 아무 소리도 안 남, 아이콘이 회색+빗금. 해제하면 복구
12. **사운드 설정에서 모든 오디오 장치를 비활성화** → 알림 풍선은 그대로 뜨고, 응답의 `windows_sound`는 `failed`, 트레이 아이콘이 주황색 경고로 바뀌고 툴팁이 이유를 설명한다. 다시 활성화하면 **재시작 없이** 다음 알림부터 소리가 난다
13. `explorer.exe`를 작업 관리자에서 재시작 → 트레이 아이콘이 돌아온다 (`TaskbarCreated` 경로)
14. Codex hooks를 붙인 뒤 **기존 `notify` 통합(computer-use)이 여전히 동작한다**
15. 작업 관리자로 데몬을 강제 종료(정상 종료 아님) → `notipet ping --no-launch`가 `stale runtime.json, pid N`을 보고한다. `--no-launch` 없이 부르면 새 인스턴스를 띄운다
16. 앱을 재시작해도 설정(음소거, 방해금지, 자동 시작)이 유지된다

---

## 기록된 문제

### 긴 알람이 끝나기 전에 계속 다시 시작됐다

- **원인**: `AlarmSession`의 반복 루프가 `PeriodicTimer`로 **재생 시작 시점 기준 고정 주기**를 돌았다. `intervalMs`를 "소리 사이의 공백"으로 의도하고 주석에도 그렇게 적어놨지만, 구현은 주기였다. 소리가 간격보다 길면 다음 틱이 `SetCurrent()`로 이전 핸들을 정지시키고 새로 시작한다.
  - 기본값이 정확히 그 경우였다: `attention`의 `Notification.Looping.Alarm`은 **5초짜리 `Alarm01.wav`**인데 간격이 1200ms였다. 5초 소리가 1.2초마다 잘려 다시 시작 — 한 번도 끝까지 재생되지 않았다. `error`(2초 소리 / 1000ms)도 같은 상태.
- **증상**: "어떤 소리가 끝나기 전에 계속 반복해서 다시 시작되는 것처럼" 들린다. 알람이 말을 더듬는 느낌.
- **영향 모듈**: `Sound` (AlarmSession, 두 엔진 모두)
- **해결**:
  1. `ISoundHandle`에 `Task Completion`을 추가했다. `MediaPlayerSoundEngine`은 `MediaEnded`/`MediaFailed`/`Stop`에서 완료시킨다. winmm에는 완료 콜백이 없어 `PlaySoundEngine`은 고정 추정치(2.5초)로 완료시키고, 그 근사값임을 코드에 명시했다.
  2. 루프가 `PeriodicTimer` 대신 **완료를 기다린 뒤 간격만큼 대기**한다. 하드 상한과 취소는 그대로 존중한다.
  3. `intervalMs`가 이제 진짜 "끝난 뒤의 공백"이므로 기본값을 줄였다 (attention/error 700ms, critical 1500ms).
- **검증**: 수정 후 `attention` 알람의 실제 지속 시간을 측정해 **10.6초** — 예측한 5 + 0.7 + 5초와 일치한다.
- **재발 방지**: `AlarmRegistry.RunSelfTest()`의 `RepeatWaitsForCompletion()`이 완료 신호를 주기 전에는 두 번째 재생이 일어나지 않음을 고정한다. 간격을 1ms로 두었으므로 주기 기반 구현이면 즉시 실패한다.

### 끝난 알람이 레지스트리에 영원히 남았다

- **원인**: `AlarmSession.Start()`가 반복 루프를 `Task.Run`으로 띄운 **뒤에** `AlarmRegistry.Add()`가 `Finished`를 구독한다. 루프가 즉시 끝나면(엔진 실패, 이미 지난 마감 시각) `Finished`가 구독자 없이 발생하고, 그 세션은 제거되지 않는다.
- **증상**: `activeAlarms`가 1에서 내려오지 않는다. 소리는 안 나는데 트레이는 알람 중으로 표시되고, `/v1/ack`으로도 지워지지 않는다. 그 상태로 `MaxConcurrent` 슬롯 하나를 영구히 잡아먹는다.
- **영향 모듈**: `Sound` (AlarmSession, AlarmRegistry)
- **발견 경위**: 긴 알람 수정 이후 스모크 8번이 간헐적으로 "ack-all 했는데도 알람 1개 잔존"으로 실패했다. 처음에는 테스트 순서 문제로 보였지만 단독 재현이 되지 않아 파고든 결과 레이스였다. 알람이 이제 끝까지 재생되면서 실행 시간이 길어진 덕에 창이 넓어져 드러났다.
- **해결**: `AlarmSession.IsStopped`를 노출하고, `Add()`가 구독 직후 이미 끝난 세션이면 바로 제거한다.
- **재발 방지**: `AlarmRegistry.RunSelfTest()`의 `AlreadyFinishedSessionIsNotLeaked()`가 `Start()` → `Stop()` → `Add()` 순서에서 `Count == 0`을 고정한다.

### 설정 창이 프로세스를 통째로 죽였다

- **원인**: 두 가지가 겹쳤다.
  1. `App.xaml`에 `XamlControlsResources`가 없었다. WinUI 3에서는 이게 있어야 컨트롤 기본 스타일이 로드된다. 트레이 데몬이라 그동안 창이 하나도 없어서 드러나지 않았다.
  2. 그걸 고친 뒤에도 남은 진짜 원인은 **`NumberBox`**였다. 이 프로세스에서 렌더 시점에 XAML 내부 예외를 일으킨다.
- **증상**: 설정 창을 열면 프로세스가 `0xc000027b`(stowed exception, `Microsoft.UI.Xaml.dll`)로 즉사한다. **`crash.log`에 아무것도 안 남는다** — 관리 코드 핸들러가 잡을 수 없는 종류다. 구성 시점이 아니라 렌더 시점이라 생성자 `try/catch`도 소용없었다.
- **영향 모듈**: `Windows` (SettingsWindow), `app/App.xaml`
- **해결**: `XamlControlsResources`를 추가하고, `NumberBox`를 프리셋 `ComboBox`로 교체했다. 반복 횟수와 간격은 선택지가 몇 개뿐이라 picker 쪽이 쓰기도 낫다. 섹션별 `try/catch`도 남겨서, 앞으로 구성 시점에 터지는 것은 창 안에 에러 텍스트로 뜨고 데몬은 살아남는다.
- **범인 특정 방법**: 섹션을 환경변수로 선택해 빌드 한 번으로 이분 탐색했다 — general/atdesk/quiet은 생존, sounds만 크래시. `ComboBox`·`TextBox`·`Slider`·`CheckBox`는 다른 섹션에서 정상이므로 소거법으로 `NumberBox`가 남았다. 탐색용 훅은 제거했다.
- **재발 방지**: **이 프로젝트에서 `NumberBox`를 쓰지 않는다.** `docs/RULES.md`의 금지 목록에 올렸다. 새 창을 추가하면 자동 검사로는 부족하니 반드시 실제로 띄워 확인한다 — 컴파일과 self-test는 이 크래시를 전혀 잡지 못했다.

### Focus Assist가 방해금지를 켜지 않았는데도 알림을 먹었다

- **원인**: `QuietHoursRule`이 `respectFocusAssist`를 `quietHours.enabled`와 **무관하게** 평가했다. 기본값이 `enabled: false` + `respectFocusAssist: true`라서, 방해금지를 한 번도 켠 적 없는 사용자도 윈도우가 DND 상태를 보고하는 순간 `critical` 미만 알림을 전부 잃었다. 설정 파일에서는 `respectFocusAssist`가 `quietHours` 아래에 중첩돼 있어서 그 구조가 거짓말이 된 셈이다.
  - 겹친 두 번째 원인: `IsFocusAssistActive()`가 `QUNS_BUSY`(전체화면 앱이 **아무거나** 실행 중)와 `QUNS_RUNNING_D3D_FULL_SCREEN`까지 차단으로 취급했다. 전체화면 터미널로 빌드를 보고 있으면 자리에 있는 건데 그때 알림이 죽는다 — 정확히 반대다.
- **증상**: 에이전트 작업이 끝났는데 소리가 안 났고, 히스토리에 `"accepted": false, "suppressedReason": "focus_assist"`만 남아 있었다. 상태값이 순간적으로만 바뀌므로 나중에 확인하면 정상으로 보여서 재현이 어렵다.
- **영향 모듈**: `Rules` (QuietHoursRule), `Presence` (PresenceMonitor)
- **해결**:
  1. `focusActive`를 `quiet.Enabled`로 게이트했다. 방해금지를 켜야만 Focus Assist를 존중한다.
  2. `IsFocusAssistActive()`를 **사용자가 명시적으로 선택한** 두 상태로 좁혔다 — `QUNS_QUIET_TIME`(방해 금지/집중 세션을 켬), `QUNS_PRESENTATION_MODE`(프레젠테이션 모드로 설정함). 전체화면 여부를 알고 싶은 호출자는 `PresenceState.FullScreen`을 쓴다. 다른 질문이다.
- **재발 방지**: `QuietHoursRule.RunSelfTest()`가 세 가지를 고정한다 — 방해금지 off + Focus 신호 있음 → **통과해야 함**, 방해금지 on + Focus 신호 → 차단하고 이유가 `focus_assist`, `respectFocusAssist` off → 다시 통과. 수정을 되돌리면 이 검사가 실패하는 것을 확인했다.

### 데몬 exe와 CLI exe의 파일명이 충돌했다

- **원인**: 데몬의 `AssemblyName`이 `Notipet`, CLI가 `notipet`이었다. 계획 단계에서는 둘을 구분되는 이름으로 봤지만 **윈도우 파일명은 대소문자를 구분하지 않는다.** 둘을 같은 `bin\`으로 퍼블리시하면 하나가 다른 하나를 덮어쓴다.
- **증상**: `publish.ps1`이 성공으로 끝나는데 결과 폴더에 `notipet.exe` 하나만 남았다. 나중에 퍼블리시된 CLI가 데몬을 덮어썼다.
- **영향 모듈**: 전체 (빌드/배포)
- **해결**: 데몬을 `NotipetTray.exe`로 개명했다. quota-scope의 `QuotaScopeWinUI.exe`와 같은 방식이고, CLI는 사용자가 실제로 타이핑하고 훅이 부르는 이름이므로 `notipet.exe`를 유지했다.
- **재발 방지**: `publish.ps1`이 마지막에 `bin\`의 `.exe` 목록을 크기와 함께 출력한다. 둘 중 하나가 사라지면 바로 보인다.

### `publish.ps1`의 인자 스플랫이 문자 단위로 쪼개졌다

- **원인**: `$aotArgs = if (...) { @('-p:PublishAot=true') }` — PowerShell이 **원소가 하나인 배열을 문자열로 언랩한다.** 그 상태에서 `@aotArgs`로 스플랫하면 문자열이 한 글자씩 개별 인자로 전달된다.
- **증상**: `MSBUILD : error MSB1001: 알 수 없는 스위치입니다.` 실제로 MSBuild가 받은 것은 `- p : P u b l i s h A o t = t r u e`였다.
- **영향 모듈**: `scripts`
- **해결**: `[string[]]`로 타입을 못 박고 전체 인자 배열을 한 번에 만들어 `& dotnet @cliArgs`로 호출한다.
- **재발 방지**: 스크립트에 그 이유를 주석으로 남겼다. PowerShell에서 네이티브 명령에 배열을 넘길 때는 항상 `[string[]]`을 명시한다.

### NativeAOT 링크가 `vswhere.exe`를 못 찾아 실패했다

- **원인**: ILCompiler 타깃이 MSVC 링커를 찾으려고 `vswhere.exe`를 경로 없이 호출한다. Visual Studio는 고정 경로(`%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`)에 설치하지만 PATH에는 넣지 않는다. 개발자 명령 프롬프트 밖에서는 실패한다.
- **증상**: `error MSB3073: 'vswhere.exe'은(는) 내부 또는 외부 명령... (코드: 123)`
- **영향 모듈**: `scripts`
- **해결**: `publish.ps1`이 `vswhere.exe`가 PATH에 없으면 표준 설치 경로를 프로세스 PATH 앞에 붙인다. 없으면 경고하고 `-NoAot` 폴백을 안내한다.
- **재발 방지**: 위 처리 + `-NoAot`(ReadyToRun + single-file) 경로 유지.

### 스모크 테스트 7번이 8번을 무너뜨렸다

- **원인**: 7번(30건 연속 발사)이 **전역** 레이트 리밋 버킷을 비운다. 8번의 `critical` 알림은 소스별 버킷은 우회하지만 전역 버킷은 일부러 우회하지 않으므로 억제됐다.
- **증상**: 8번이 `activeAlarms = 0`으로 실패. 같은 검사를 단독으로 돌리면 통과.
- **영향 모듈**: `Rules` (설계는 정상), `scripts`
- **해결**: 8번이 전역 버킷 리필을 3초 기다리고, 억제됐으면 `suppressedReason`을 출력하며 실패한다.
- **재발 방지**: 레이트 리밋을 소진시키는 검사 뒤에 오는 검사는 리필을 기다리거나 리밋을 끄고 돌린다. 이건 제품 버그가 아니라 테스트 순서 문제였다.

### 자기 자신의 self-test가 crash.log에 남는다

- **원인**: `HttpSelfTest`의 스텁 채널이 채널 격리를 검증하려고 일부러 예외를 던지는데, `Dispatcher.SendSafelyAsync`가 그걸 정상적으로 `CrashLog`에 기록한다.
- **증상**: `--self-test`를 돌릴 때마다 `%LOCALAPPDATA%\notipet\crash.log`에 `Channel:stub / stub failure` 항목이 쌓인다.
- **영향 모듈**: `SelfTest`, `Core` (Dispatcher)
- **상태**: 동작은 정상이다 — 격리 경로가 실제로 돌았다는 증거이기도 하다. 다만 헤드리스 검사가 사용자 데이터 폴더에 쓰는 건 깔끔하지 않다. [TODO.md](TODO.md)에 올려뒀다.
- **재발 방지**: `CrashLog`의 출력 경로를 주입 가능하게 만들고 self-test에서 임시 폴더로 돌린다.

### `dotnet run`이 데몬의 콘솔 출력을 삼킨다

- **원인**: 데몬이 `WinExe`라 자체 콘솔이 없다. `SelfTestRunner`가 부모 콘솔을 빌려오는데(`AttachConsole`), 이미 콘솔이 있는 상태에서 그걸 호출하면 `Console`이 캐싱해 둔 핸들이 교체되면서 모든 줄이 사라진다.
- **증상**: `dotnet run --project app -- --self-test`가 출력 없이 끝난다.
- **영향 모듈**: `SelfTest`
- **해결**: `AttachParentConsole()`이 `GetConsoleWindow()`로 먼저 확인하고, 콘솔이 없을 때만 붙인 뒤 `Console.SetOut`/`SetError`를 다시 연다.
- **재발 방지**: 검증은 exe를 직접 실행한다. [RULES.md](RULES.md)에 명시.

### 태그가 말줄임표로 잘려 중복 병합이 깨졌다

- **원인**: `PayloadMapper.BuildTag`가 세션 id를 표시용 `Shorten()`으로 줄였는데, 이 함수는 말줄임표(`…`)를 붙인다.
- **증상**: 기대한 태그 `claude-code:8f3a12345678:...` 대신 `claude-code:8f3a1234567…:...`가 나왔다. 태그는 중복 병합 키라서, 길이가 경계에 걸리는 세션에서는 같은 이벤트가 다른 키로 갈라질 수 있었다.
- **영향 모듈**: `shared` (PayloadMapper), `Rules` (DedupeRule)
- **해결**: 표시용 `Shorten()`과 키용 `Truncate()`를 분리하고, 태그에는 `Truncate()`를 쓴다.
- **재발 방지**: `PayloadMapper.RunSelfTest()`가 기대 태그 문자열을 그대로 고정한다.

### self-test가 사용자의 실제 `settings.json`을 덮어썼다

- **원인**: `AppSettings.Save()`가 인스턴스와 무관하게 항상 `Paths.SettingsPath`에 썼다. 1.1에서 추가한 `/v1/presence`의 폴백 경로가 `Save()`를 불렀고, `HttpSelfTest`는 사운드·풍선·레이트 리밋·중복 병합을 끈 **테스트용 설정 인스턴스**로 이 라우트를 호출했다.
- **증상**: 데몬 self-test를 한 번 돌리자 실제 설정에서 소리·풍선이 꺼지고 레이트 리밋·중복 병합이 꺼졌다. 다음 알림부터 조용해진다 — 알림 도구에서 가장 나쁜 종류의 실패.
- **영향 모듈**: `Settings`, `SelfTest`, `Http`
- **해결**:
  1. `AppSettings.SourcePath`(JSON 제외)를 두고 `Load(path)`만 설정한다. `Save()`는 이 경로에만 쓰고, 불러오지 않은 인스턴스(`new AppSettings()`)는 `false`를 반환하며 아무것도 쓰지 않는다.
  2. `HttpSelfTest.Run()`이 실제 `settings.json`의 SHA-256을 앞뒤로 비교해, 바뀌었으면 실패한다.
  3. 덮어쓰인 파일은 `%LOCALAPPDATA%\notipet\settings.clobbered-by-selftest.json`으로 보관하고 네 플래그를 되돌렸다.
- **재발 방지**: `AppSettings` self-test가 `SourcePath`와 "불러오지 않은 인스턴스는 저장하지 않음"을 고정한다. 위의 해시 가드. [RULES.md](RULES.md)에 "테스트는 실제 설정을 쓰지 않는다".

### 설정 창 레이아웃이 붕괴했다 (넘치는 콤보, 세로로 밀린 라벨)

- **원인**: 레벨별 사운드 카드의 콤보 4개를 가로 `StackPanel`에 넣어 카드 폭을 넘었다. 방해금지 시간의 `TimePicker` 두 개는 최소 폭이 커서 같은 줄의 라벨을 폭 0 근처로 밀어냈다.
- **증상**: 콤보가 카드 밖으로 잘리고, "시간대" 라벨이 한 글자씩 세로로 쌓였다.
- **해결**: 사운드 카드는 `Grid` 별 비율 열(2 / 1.5 / 1 / 1)로, 시간대는 `ExpandedCard`(라벨 위, 선택기 아래)로.
- **발견 경위**: 컴파일·self-test 모두 통과. 창을 띄운 스크린샷에서만 보였다.
- **재발 방지**: [scripts/ui-check.ps1](../scripts/ui-check.ps1)이 모든 페이지의 스크린샷을 남긴다.

### 아이콘 버튼을 UI Automation이 찾지 못했다

- **원인**: `Fluent.IconButton`이 글리프만 있고 접근성 이름이 없었다. 툴팁은 이름이 아니다.
- **증상**: 화면 낭독기가 "단추"라고만 읽는다. ui-check가 "비우기"를 찾지 못했다.
- **해결**: `AutomationProperties.SetName(button, label)`.

### `notipet stop` 뒤 `notipet start`가 데몬을 찾지 못했다

- **원인**: 데몬 exe 위치를 `runtime.json`의 `exePath`와 "CLI 옆"에서만 찾았다. 정상 종료가 `runtime.json`을 지우므로, CLI가 데몬과 다른 폴더에 있으면(개발 빌드) 찾을 곳이 없다.
- **해결**: 데몬이 뜰 때마다 `%LOCALAPPDATA%\notipet\daemon.path`에 자기 경로를 기록하고, CLI가 세 번째 후보로 읽는다.

### 트레이 메뉴 아이콘 렌더링이 프로세스를 죽일 수 있었다

- **원인**: 메뉴 글리프 비트맵은 `WM_APP` 트레이 콜백, 즉 창 프로시저 안에서 만든다. 여기서 관리 예외가 새면 네이티브 경계에서 FailFast다. 또 `BITMAPINFOHEADER.biSize`를 잘못 넣으면 `CreateDIBSection`이 실패한다.
- **해결**: 글리프 렌더링과 `SetMenuItemInfo`를 `try/catch`로 감싸 실패 시 아이콘 없이 메뉴만 띄운다. `biSize = 40`. 32비트 premultiplied 알파, `MNS_CHECKORBMP`로 체크 표시와 아이콘이 한 칸을 공유한다.
- **재발 방지**: `MenuGlyphs.RunSelfTest()`가 밝은/어두운 글리프를 실제로 렌더링해 알파가 있는지 확인한다.

### 앱 아이콘이 우편함처럼 보였다

- **원인**: 16px 트레이용 종 경로를 그대로 키워 앱 아이콘으로 썼다. 큰 크기에서 각진 윤곽과 이음매가 드러났다.
- **해결**: 베지어 곡선으로 종을 다시 그리고, 앱 아이콘은 둥근 그라데이션 타일 위 흰 종으로 분리했다. 트레이 아이콘의 음소거 빗금과 경고 `!`는 **투명하게 뚫어서**(`CompositingMode.SourceCopy`) 밝은/어두운 작업 표시줄 모두에서 읽힌다. `NotipetTray.exe --export-icon <ico> [sheet.png]`로 ICO와 미리보기를 다시 만든다.
- **주의**: GUI exe는 셸이 기다리지 않는다. 미리보기를 만들 때 `Start-Process -Wait`를 쓰지 않으면 이전 파일을 보게 된다.

### 재배포 후 `notipet start`가 예전 개발 빌드를 띄웠다

- **원인**: `DaemonPath()`가 `runtime.json`의 `exePath`를 CLI 옆 exe보다 먼저 봤다. 데몬이 강제 종료되면 `runtime.json`이 남고, 그 경로가 Debug 빌드였다. `publish.ps1 -Restart`는 옛 CLI(1.0, `stop` 없음)로 정지를 시도하다 강제 종료 폴백을 탔고, 이어진 `start`가 stale 파일을 믿었다.
- **증상**: 배포 직후 트레이에 떠 있는 건 `app\bin\Debug\...\NotipetTray.exe`. `doctor`의 `daemon exe`에서 드러났다.
- **해결**: 띄울 때는 CLI 옆 exe가 우선. `doctor`/`open`은 `preferRunning: true`로 실행 중인 데몬 경로 우선.

### 배포본(bin)에서 모든 창이 열리지 않았다

- **원인**: unpackaged `dotnet publish`가 앱 자신의 리소스 인덱스 `NotipetTray.pri`를 게시 폴더로 복사하지 않는다. 컴파일된 `App.xaml`(`XamlControlsResources`)이 거기 있으므로, 없으면 `{ThemeResource AccentTextFillColorPrimaryBrush}` 같은 테마 리소스가 해석되지 않는다.
- **증상**: Debug 빌드 출력에서는 ui-check 12/12 통과, `bin\`에서는 설정·최근 알림 창이 열리지 않고 `crash.log`에 `XamlParseException: Cannot find a Resource with the Name/Key AccentTextFillColorPrimaryBrush`. 데몬은 살아 있다(구성 시점 예외라 잡힘).
- **발견 경위**: 배포 후 `ui-check.ps1`을 `bin\` 대상으로 돌렸을 때. 빌드 출력만 검사했다면 놓쳤다.
- **해결**: `Notipet.App.csproj`의 `NotipetPublishPri` 타깃이 게시 후 `.pri`를 복사하고, 없으면 게시를 실패시킨다.
- **재발 방지**: [RULES.md](RULES.md) — 배포 후 `bin\` 대상으로 `ui-check.ps1`을 돌린다.

### "확인할 때까지" 알람이 2분 만에 꺼져 있었다

- **증상**: 09-23 20:15에 Codex의 `attention` 알림이 왔고 기록상 정상 전달(`windows_sound: delivered`, 알람 `alm_0092`)이었는데, 사용자가 PC 앞에 왔을 때는 아무 소리도 나지 않았다. 음소거·방해금지·자리 착석 모두 아니었고 오디오 오류도 없었다.
- **원인**: `sound.maxDurationSec`(기본 120초)이 `until_ack`에도 그대로 적용된다. 알람은 20:15:49~20:17:49 동안만 울리고 스스로 멈췄다.
- **왜 그렇게 만들었나**: 잘못된 훅이 `until_ack`을 루프로 쏘면 자리를 비운 내내 사이렌이 울리는 것을 막으려는 상한이었다. 그런데 **그 방어가 정작 보호하려던 상황(자리를 비운 사이)에서 알람을 꺼 버렸다.** 사용자가 돌아왔을 때 울리고 있어야 그 알람이 의미가 있다.
- **해결**: `sound.maxDurationSec = 0`(`AppSettings.UnlimitedAlarmSeconds`) 선택지를 설정 창에 추가했다. `until_ack` 알람에 한해 마감 시각이 없고, 확인(트레이 클릭·풍선·`ack`) 때만 멈춘다. `repeat`/`once`, 요청이 지정한 지속 시간, 자리 착석 단축(30초)은 그대로 제한된다.
- **검증**: 볼륨 0으로 무제한 `until_ack` 알람을 띄우고 **2분 33초 뒤에도 `alarms 1`**임을 확인한 뒤 `ack`으로 0이 되는 것까지 확인했다. self-test에 네 가지 고정(무제한+until_ack=0 / repeat는 600 / 요청 45초는 45초 / 착석 시 30초 이하)을 추가했다.
- **주의**: `AlarmSession.WaitAsync`는 `Task.Delay`가 받을 수 없는 길이를 피하려고 하루 단위로 나눠 기다린다. 즉 실질 한계는 24시간이다.

### 창이 다른 창 뒤에 있으면 트레이 클릭이 아무 반응도 없었다

- **증상**: 최근 알림 창이 이미 열려 있는데 다른 창에 가려져 있으면, 트레이 아이콘을 클릭해도 아무 일도 일어나지 않는다. 창은 떠 있지만 뒤에 있으니 사용자에게는 **프로그램이 죽은 것처럼 보인다.**
- **원인**: `Activate()`가 `AppWindow.Show(true)` + `Window.Activate()`에만 의존했다. 이미 보이는 창에 `Show(true)`는 아무것도 하지 않고, `Window.Activate()`는 **포그라운드 잠금**(foreground lock)에 걸린다 — 트레이를 클릭한 시점에 포그라운드 프로세스는 다른 앱이라 Windows가 우리의 포그라운드 전환 요청을 거부한다.
- **해결**: [Fluent.cs](../app/Windows/Fluent.cs)의 `BringToFront(Window)`로 통일했다. 최소화 상태면 `OverlappedPresenter.Restore()`, 그다음 `SetForegroundWindow`, 거부되면 **포그라운드 스레드의 입력 큐에 `AttachThreadInput`으로 붙은 뒤** `BringWindowToTop` + 재시도, 그래도 안 되면 topmost를 잠깐 켰다 끄는 것으로 최소한 위로 올린다. 두 창(최근 알림, 설정) 모두 이걸 쓴다.
- **검증**: 메모장을 앞에 띄워 창을 가린 뒤 트레이 좌클릭 메시지를 보내고 포그라운드 창을 확인하는 스크립트로, **고치기 전 FAIL / 고친 뒤 PASS**를 둘 다 확인했다.
- **교훈**: 트레이 앱의 "창 보이기"는 `Activate()` 한 줄이 아니다. 창이 보이는 상태인지, 최소화인지, 가려져 있는지 세 경우를 모두 확인해야 한다.

### 트레이 토글을 포그라운드로 판단하면 실제 클릭에서는 절대 닫히지 않는다 (예방)

- **맥락**: 1.2에서 "창이 올라와 있으면 트레이 클릭으로 닫기"를 넣었다. 처음 떠오르는 구현은 `GetForegroundWindow() == 우리 창`이면 닫기다.
- **함정**: 알림 영역에서 마우스를 **누르는** 순간 작업 표시줄(`Shell_TrayWnd`, 오버플로 창)이 포그라운드를 가져간다. 우리 `WM_LBUTTONUP` 콜백이 돌 때는 이미 포그라운드가 아니므로, 그 구현은 실제 사용자에게 **영원히 닫히지 않는다.** 반면 스크립트의 `PostMessage` 가짜 클릭은 포그라운드를 옮기지 않아서 **테스트는 통과한다** — 가장 나쁜 조합이다.
- **해결**: `Fluent.IsInFront`가 z-order로 판단한다. 우리 창 위의 창 중 보이고·최소화/클로킹 아니고·topmost/툴/비활성 창 아니고·우리 팝업 아니면서 **겹치는** 창이 있으면 "가려짐" → 올린다. 없으면 닫는다. 사각형은 DWM 확장 프레임 경계(보이지 않는 테두리 제외).
- **검증**: 가짜 클릭 스크립트로 열기/닫기/가려진 창 올리기/더블클릭 32/32. **실제 클릭 경로는 손으로 확인해야 한다** — [tray_shell.md](modules/tray_shell.md) Regression Notes의 체크리스트.

### 테스트가 사용자의 메모장에 빈 탭을 열었다

- **원인**: 가려진 창을 만들려고 테스트에서 `Start-Process notepad`를 썼다. Windows 11 메모장은 이미 떠 있는 메모장으로 넘겨서 **사용자의 메모장 창에 빈 탭을 추가**했고, 시작한 프로세스는 바로 종료돼 `MainWindowHandle`이 0이라 창 이동도 안 됐다.
- **해결**: 테스트용으로 자기 프로세스의 WinForms 창(`notipet test cover`, 20초 후 스스로 닫힘)을 쓴다. 사용자 앱은 건드리지 않는다.
- **덤**: PowerShell에서 `[DllImport] FindWindow(string, string)`에 `$null`을 넘기면 `""`가 된다. `[NullString]::Value`를 쓴다. 숨김으로 시작한 프로세스의 첫 `ShowWindow`는 `SW_HIDE`를 물려받아 폼이 안 보인다 — 버리는 폼으로 한 번 소비한다.

### 드라이브 루트가 프로젝트 "C:"가 됐다

- **증상**: `cwd`가 `C:\`인 알림이 최근 알림 창에 "C:" 그룹으로 묶였다.
- **해결**: `AgentIdentity.Leaf`가 드라이브 루트와 `/`를 프로젝트로 치지 않는다 → "기타". self-test 고정.

### Codex notify의 turn-id가 스레드로 쓰였다 (예방)

- **원인**: `FromCodexNotify`가 `Session = thread-id ?? turn-id`였다. 턴 ID는 턴마다 바뀐다.
- **영향**: 스레드별로 묶거나 딥링크를 만들면 카드가 턴마다 갈라지고 링크는 아무 데도 가지 않는다.
- **해결**: 스레드는 `thread-id`만. 태그는 예전 그대로 둬서 버전 간 중복 병합 키가 바뀌지 않게 했다.

### 1.2 리뷰에서 잡은 것들 (배포 전)

다섯 갈래(정확성·보안·UI·CLI·불변식) 리뷰 + 항목마다 반박 3회로 걸러 남은 것. 전부 배포 전에 고쳤다.

- **같은 태그를 쓰는 두 대화가 하나로 합쳐졌다.** 스킬은 `<repo>:needs-input` 같은 태그를 권하는데, 병합 키가 태그뿐이라 같은 저장소의 두 대화가 30초 안에 같은 태그를 보내면 두 번째는 소리도 카드도 없었고 남은 카드의 링크는 첫 번째 대화를 열었다. → `DedupeKey = 태그|스레드`(스레드를 알 때). 훅 태그는 이미 세션을 품고 있어 동작이 같다. 스킬 문서의 "태그를 맞추면 훅 알림과 합쳐진다"는 불가능한 약속도 지웠다(훅 태그는 `claude-code:<세션>:<이벤트>`).
- **버전 관리되는 홈 폴더가 모든 프로젝트를 삼켰다.** `.git`을 찾아 위로 올라가다 `C:\Users\u\.git`(dotfiles)을 만나면 그 아래 저장소가 아닌 폴더가 전부 "u"로 묶였다. → git의 `GIT_CEILING_DIRECTORIES`처럼 프로필 폴더를 천장으로. 홈에서 바로 작업할 때(시작점이 천장)만 홈을 본다.
- **이름 없는 Codex 스레드가 같은 라벨을 달았다.** ID 앞 8자리를 썼는데 Codex ID는 UUIDv7이라 앞자리가 시각 — 같은 분에 시작한 스레드가 전부 같은 `#0199a213`. → 끝 8자리.
- **긴 제목·프로젝트·경로가 말줄임표 없이 잘렸다.** 가로 `StackPanel`이 자식에게 무한 폭을 줘서 `CharacterEllipsis`가 동작하지 않았고, 링크 표시 아이콘과 개수가 밀려 사라졌다. → `Grid`의 `*` 열.
- **그룹을 접으면 키보드 포커스가 사라졌다.** → 다시 만든 머리글로 포커스 복원.
- **카드를 클릭해도 열리지 않았다(작은 링크와 아이콘만).** → 카드 클릭으로 열기(버튼·본문 제외, 손 모양 커서).
- **"스레드 이름 표시"를 꺼도 이미 읽은 이름이 남았다.** → 끄면 지우고, 켜면 다시 찾고, 진행 중이던 조회는 쓰기 전에 설정을 다시 본다.
- **스킬이 `--agent claude-code`를 박아 두었다.** `install-skill --codex`도 같은 파일을 쓰므로 Codex가 Claude로 기록됐다. AGENTS 조각은 반대로 `--agent codex`. → 둘 다 빼고 CLI 자동 감지에 맡긴다.
- **상대 `--cwd`(`.`)가 프로젝트 "."이 됐다.** → 전체 경로로 풀고, `.`/`..`은 프로젝트가 아니다.
- **반드시 통과하는 self-test 두 개.** HTTP 검사는 응답 전체에서 문자열을 찾아 앞선 훅 항목이 대신 만족시켰고, `CLAUDE_PROJECT_DIR` 검사는 워크트리 경로라 그 변수 없이도 같은 답이 나왔다. → 그 항목 자체를 확인, 무관한 cwd로 교체.

### 스모크 9번이 사용자 설정에 따라 결과가 달랐다

- **증상**: 코어 분리 후 배포에서 스모크 9번이 "30.3초 재생"으로 실패했고, 이어진 ui-check의 트레이 토글 검사도 실패했다.
- **원인**: 9번이 레벨(`attention`) 기본값으로 알람을 울렸는데, 사용자 설정은 `attention = until_ack` + 지속 시간 무제한이었다. 9번은 30초까지만 기다리고 **ack 없이** 끝나서 알람이 계속 울렸고, ui-check의 첫 트레이 클릭은 설계대로 "알람 정지"가 되어 토글 순서가 한 칸 밀렸다. 코드 결함이 아니라 검사가 사용자 설정과 남은 알람에 기대고 있었다.
- **해결**: 9번은 요청에 소리 사양(`repeat` 2회, 간격 700ms)을 직접 넣어 사용자 설정과 무관하게 재고, 끝나면 항상 ack한다. ui-check는 토글 검사 전에 울리는 알람을 모두 멈춘다.

### 알람 한도로 생략된 소리가 "소리 실패"로 보였다

- **증상**: 반복 알람이 이미 3개 울리는 중에 온 알림의 카드에 빨간 "소리 실패" 칩이 붙었다. 오류처럼 보이지만 한도(`AlarmRegistry.MaxConcurrent`)가 의도대로 동작한 것이다.
- **해결**: `SoundPlayOutcome.Skipped`를 두고 사운드 채널이 `skipped`("3 alarms already sounding")로 보고한다. 카드에는 회색 "소리 건너뜀" 칩과, 그 아래 한 줄로 "소리: 알람 3개가 이미 울리는 중". 이유를 칩 안에 넣었더니 칩 줄이 넘쳐 옆 칩이 잘려서 줄을 나눴다.

### "클릭할 때까지 유지" 창이 넘치면 가장 오래된 것이 말없이 사라졌다

- **원인**: 화면 한도(4개)를 넘으면 가장 오래된 창을 닫았다. 유지 옵션과 무관하게.
- **영향**: 자리를 비운 사이 쌓이면 가장 오래 기다린 요청부터 표시 없이 화면에서 사라졌다(기록에는 남음).
- **해결**: 유지하는 레벨의 창은 맨 위 "외 N개" 카드에 수를 더하고 빠진다. 누르면 최근 알림 창. 같은 요청에서 유지 옵션을 레벨별(`popup.stayLevels`)로 바꿨다.

### 데몬은 몇 시간 동안 살아 있었는데 CLI·훅은 "꺼져 있다"고 봤다 (1.4.1)

- **증상**: `notipet ping`이 `stale runtime.json, pid 84152`. 실제로는 다른 데몬(18192)이 다섯 시간째 정상 동작 중이었다(트레이·알림 창·HTTP 모두). 훅 알림 대부분이 전달되지 못했다.
- **확인한 것**: 84152는 `crash.log`·이벤트 로그에 아무 흔적 없이 사라졌다(강제 종료로 보인다). 18192는 훅이 띄웠고 `daemon.path`까지 썼다(같은 함수에서 `runtime.json` 바로 앞). 18192에 Claude 알림 창이 있었으니 `runtime.json`은 한때 18192를 가리켰어야 하는데, 발견 당시에는 84152의 내용(13:24 작성)이었다. 권한·포터블 모드·쓰기 실패 기록 모두 정상 — **무엇이 되돌렸는지는 확정하지 못했다.**
- **해결**(원인과 무관하게 낫도록):
  - 데몬이 30초마다 `runtime.json`·`runtime-{세션}.json`이 자기를 가리키는지 보고, 아니면 다시 쓴다. 확인: 다른 pid로 바꿔도, 지워도 30초 안에 복구.
  - `daemon.log`: 시작, 종료 이유(트레이·`notipet stop`·감시 장치), 프로세스 종료, 그리고 다음 시작 때 "앞 인스턴스가 정리 없이 끝남". 다음에 조용히 사라지면 언제였는지는 남는다.
- **나중에 알게 된 것**: 진단에 쓴 셸 일부가 에이전트 샌드박스 안이었다. 같은 시각에 샌드박스 셸은 `runtime.json`을 오래된 사본(이미 죽은 pid)으로, 일반 셸은 정상(살아 있는 pid)으로 보았다. 그러니 "파일이 되돌려졌다"는 관찰의 일부는 샌드박스가 보여 준 착시였을 수 있다. **데몬 상태는 샌드박스 밖 셸에서 확인한다.** 샌드박스 셸에서 CLI를 돌리면, CLI가 데몬이 꺼졌다고 보고 샌드박스 안에서 새 데몬을 띄울 수도 있다 — 그 데몬이 쓴 `runtime.json`은 사용자 쪽 훅에 보이지 않는다.

### UI 스레드가 멈췄다 — 트레이·알림 창·창이 전부 먹통 (1.4.0)

- **증상**: Notipet이 "죽은" 것처럼 보였다. 프로세스는 살아 있고 HTTP(`notipet ping`)도 답했지만 창은 "응답 없음", 트레이 클릭과 알림 창이 동작하지 않았다. CPU 0 — 무언가를 기다리는 교착.
- **원인**(덤프로 확인): `TrayIconHost.WndProc`(`WM_DEVICECHANGE`) → `SoundService.ProbeEngines` → `MediaPlayerSoundEngine.Probe`가 **UI 스레드에서** `MediaPlayer`를 만들고 버렸다. `MediaPlayer.Dispose()`가 기다리는 동안 메시지 루프가 돌았고, 연달아 온 다음 `WM_DEVICECHANGE`가 그 안으로 다시 들어와 `Probe` → `new MediaPlayer()`에서 첫 번째를 기다리며 영원히 멈췄다. 장치 변경 메시지는 헤드셋 연결·절전 해제 때 수십 개씩 온다.
- **재현**: 테스트 데몬의 트레이 창에 6개 스레드에서 `WM_DEVICECHANGE`를 `SendMessageTimeout`으로 240번 — 1.4.0은 223번 시간 초과 후 멈춤(UI 스레드에 MediaPlayer 창 21개), 수정본은 0번·정상.
- **해결**:
  - 장치 변경은 `SoundService.RequestProbe`: 스레드 풀에서, 1초 모아서, 한 번에 하나.
  - 미리듣기·테스트 알림의 소리도 스레드 풀에서. 엔진은 STA에서 불리면 스레드 풀로 넘긴다.
  - `UiWatchdog`: UI 스레드가 1분 답이 없으면 기록하고 스스로 다시 시작한다(UI 스레드를 강제로 멈춰 확인: 60초 뒤 새 인스턴스).
- **같이 고친 것**: 숨겨진 최근 알림 창을 알림마다 통째로 다시 그리던 것(UI CPU 대부분)을 보일 때만으로. 자체 테스트의 의도된 예외가 사용자 `crash.log`에 쌓이던 것(`Channel:stub`)을 임시 파일로.

### 훅으로 온 한글이 깨졌다 (`??遺꾨━?댁빞`)

- **증상**: Claude Code의 `Stop` 훅 알림 본문(마지막 답변)이 `??遺꾨━?댁빞`처럼 깨졌고, `→` 같은 기호는 `??`가 됐다. 영어는 멀쩡했다.
- **원인**: CLI가 stdin을 `Console.In`으로 읽었다. `Console.In`은 콘솔 입력 코드 페이지(한국어 Windows는 CP949)로 디코딩하는데, 훅은 UTF-8 JSON을 보낸다. 실행 환경에 따라 깨진 글자로 들어오거나, JSON 파싱 자체가 실패해 본문이 비었다.
- **해결**: stdin을 바이트로 읽어 **UTF-8로 엄격하게** 디코딩한다(BOM 허용). UTF-8이 아닌 바이트일 때만 콘솔 코드 페이지로 되돌아간다 — 옛 프로그램 출력을 `--body -`로 파이프하는 경우. `ArgParsing` 자체 테스트가 한글·BOM·비UTF-8 세 경우를 고정한다.
- **확인**: 같은 UTF-8 페이로드를 바이트로 넣었을 때 1.3.0 CLI는 본문이 비었고, 1.3.1은 `분리해야 한다고 봅니다 → 끝` 그대로 저장했다.

### Codex 훅의 `args`가 조용히 버려지고 있었다 (1.5.1)

- **증상**: 없음 — 그래서 몰랐다. `install-hooks --codex`가 출력하고 문서가 안내한 `args = ["--source", "codex"]`가 실제로는 전달되지 않았다.
- **원인**: Codex 훅 핸들러에는 `args` 필드가 없다(`codex-rs/config/src/hook_config.rs`). 모르는 필드는 경고 없이 버린다. 그래도 동작한 것은 CLI가 페이로드 모양(`turn_id`, `.codex` 경로)으로 Codex를 알아봤기 때문이다.
- **해결**: 인수를 `command` 안으로(`'"…\notipet.exe" --source codex'`). `install-hooks --codex --write`가 넣는 블록도 그렇게 쓰고, 예전 블록(`args`가 든 것)은 새 것으로 바꾼다.

### `codex exec`의 훅에서 notipet이 "꺼져 있다"고 했다 (1.5.1)

- **증상**: `codex exec`로 턴을 돌리면 config.toml의 notipet 훅이 실행되는데도 알림이 없었다. 같은 명령을 사람이 cmd로 실행하면 알림이 왔다.
- **원인**: 이 PC의 Codex(0.162.0-alpha.17.2)는 `codex exec`에서 훅을 **환경 변수 하나 없이** 실행했다(기록용 훅의 `set` 출력이 비었다). `SystemRoot`가 없으면 Winsock이 소켓을 만들지 못해, CLI가 루프백 연결에 실패하고 데몬이 꺼진 것으로 판단했다. 빈 환경으로 직접 실행해 재현했다: `daemon not running` → `SystemRoot`만 넣으면 전달.
- **해결**: CLI가 시작할 때 `SystemRoot`/`windir`이 비어 있으면 Windows 폴더(셸 API, 환경 변수 아님)로 채운다(`Program.RestoreSystemRoot`). 데스크톱 앱의 훅은 환경이 채워져 있어 이 문제를 겪지 않았다.

### Codex 훅 위치: 플러그인 → hooks.json → config.toml (1.5.1 개발 중, 예방)

- **플러그인에 넣은 훅**은 `codex exec`에서 돌지 않았다(신뢰를 넣거나 `--dangerously-bypass-hook-trust`를 줘도). 공식 문서도 데스크톱 전용이라고 하고, 훅이 든 플러그인은 공개 목록에 못 올린다 → 플러그인은 스킬만.
- **`~/.codex/hooks.json`**에 쓴 훅은 앱 서버 `hooks/list`에는 보였지만 `codex exec`에서 실행되지 않았다. 같은 내용을 config.toml에 넣으면 실행됐다 → `install-hooks --codex --write`는 config.toml에 쓴다.
- 확인 방법: `codex exec --ephemeral --dangerously-bypass-hook-trust -c 'hooks.Stop=[{hooks=[{type="command",command="echo ran >> %TEMP%\x.log"}]}]' ...`처럼 설정 파일을 건드리지 않는 기록용 훅으로 비교한다.

### 플러그인 훅과 settings.json 훅은 둘 다 돈다 (1.6.1, 예방)

- **사실**: Claude Code는 같은 핸들러가 여러 settings 파일에 있으면 한 번만 돌리지만, 플러그인의 복사본은 따로 돌린다(공식 hooks 문서). Codex도 모든 출처의 훅을 다 돌린다.
- **대응**: Claude 플러그인으로 옮길 때 `notipet install-hooks --claude --remove`로 settings.json의 notipet 훅을 빼고 `~/.claude/skills/notipet`을 지운다. `doctor`가 둘 다 있으면 경고하고, `--write`도 플러그인이 켜져 있으면 경고한다.
- 함께 확인한 것: Claude Code 훅의 `args`는 정식 필드다(exec form, 셸 없이 실행). 예전 settings.json의 `args`는 제대로 쓰이고 있었다 — Codex와 다르다.