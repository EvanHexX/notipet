# RULES

이 저장소에서 코드를 고칠 때 지키는 것들. 전역 규칙은 [AGENTS.md](../AGENTS.md)에 있고, 여기는 notipet에만 해당하는 내용이다.

## 작업 순서

1. [AGENTS.md](../AGENTS.md)와 [PROJECT_MAP.md](PROJECT_MAP.md)를 읽는다.
2. 건드릴 영역의 [modules/](modules/) 문서를 읽는다.
3. 해당 소스 파일을 본다.
4. 바꿀 내용을 한 문단으로 요약하고 범위를 좁게 유지한다.

관련 없는 리팩터링, UI 프레임워크 교체, 네임스페이스 변경, 채널 확장을 다른 수정에 끼워 넣지 않는다.

## 빌드와 검증

```powershell
dotnet build notipet.slnx
.\app\bin\Debug\net10.0-windows10.0.19041.0\win-x64\NotipetTray.exe --self-test
.\cli\bin\Debug\net10.0\win-x64\notipet.exe --self-test
.\app\bin\Debug\net10.0-windows10.0.19041.0\win-x64\NotipetTray.exe --test-sound attention
.\scripts\smoke.ps1 -FromBuildOutput -Configuration Debug
.\scripts\ui-check.ps1 -FromBuildOutput -Configuration Debug   # 창을 고쳤다면
```

**exe를 직접 실행한다.** 데몬은 `WinExe`라서 `dotnet run`으로 돌리면 셸에 따라 콘솔 출력이 삼켜지는데, self-test는 출력이 전부다.

데몬이 떠 있으면 `app\bin\`이 잠긴다. 죽이지 말고 스크래치 출력으로 빌드한다:

```powershell
dotnet build app\Notipet.App.csproj -p:BaseOutputPath=$env:TEMP/notipet-build/
```

**실행하지 않은 검증을 했다고 보고하지 않는다.** 명령이 실패하면 실패한 그대로 보고한다.

## 검증 규칙

- `--self-test`는 **무음·헤드리스**다. 트레이 아이콘도, 창도, 소리도 없다. CI에서 돌 수 있어야 한다. 소리를 내는 검증은 `--test-sound`뿐이다.
- 새 규칙·파서·매퍼를 추가하면 그 클래스에 `RunSelfTest()`를 만들고 [SelfTestRunner](../app/SelfTest/SelfTestRunner.cs)의 목록에 넣는다. 검사는 그 클래스 안에 있어야 한다 — 별도 테스트 프로젝트를 만들지 않는다.
- 트레이·사운드·훅을 바꿨으면 [regression.md](regression.md)의 수동 체크리스트를 보고서에 포함한다. 요청받지 않았으면 GUI를 직접 띄우지 않는다.
- **창을 고쳤으면 `scripts\ui-check.ps1`을 돌리고 `%TEMP%\notipet-ui`의 스크린샷을 눈으로 본다.** 모든 옵션 페이지, 최근 알림 창, 트레이 메뉴를 실제로 열고 생존·에러 표시·이벤트 로그 크래시를 확인한다. 레이아웃 붕괴(넘치는 콤보, 한 글자씩 세로로 밀린 라벨)는 스크린샷으로만 보인다.
- **배포 후에는 `bin` 대상으로 `ui-check.ps1`을 한 번 더 돌린다.** 빌드 출력과 게시 폴더는 같지 않다 — `.pri`가 빠져 배포본에서만 창이 안 열린 적이 있다.
- **테스트는 사용자의 실제 `settings.json`을 절대 쓰지 않는다.** `AppSettings.Save()`는 `Load(path)`로 불러온 경로에만 쓰고, `HttpSelfTest`는 실제 파일의 해시가 바뀌면 실패한다.
- **창(Window)을 추가하거나 고쳤으면 반드시 실제로 띄워서 확인한다.** WinUI 컨트롤은 렌더 시점에 `0xc000027b`로 프로세스를 죽일 수 있고, 컴파일도 self-test도 그것을 잡지 못한다. `NotipetTray.exe --settings` / 두 번째 실행으로 창을 열 수 있고, 검증은 UI Automation으로 창 이름과 자식 컨트롤을 읽어 확인한다.

## 깨뜨리면 안 되는 것들

각각 실제 실패에서 나온 규칙이고, 해당 위치에 주석이 달려 있다.

| 규칙 | 어디 | 어기면 |
|---|---|---|
| `TrayIconHost._wndProc`는 인스턴스 필드여야 한다 | [TrayIconHost.cs](../app/Tray/TrayIconHost.cs) | 네이티브 콜백의 GC 루트가 사라져 `FailFast`로 프로세스가 죽는다 |
| 풍선에 `NIIF_NOSOUND`를 세팅한다 | [TrayIconHost.cs](../app/Tray/TrayIconHost.cs) | 셸이 자체 알림음을 얹어 모든 알림이 이중으로 울린다 |
| `MediaPlayer.CommandManager.IsEnabled = false` | [MediaPlayerSoundEngine.cs](../app/Sound/MediaPlayerSoundEngine.cs) | unpackaged 프로세스에서 SMTC 등록이 던지거나 사용자 미디어 키를 가로챈다 |
| 알람 지속 시간 상한은 코드에서 강제한다 | [AppSettings.cs](../core/Settings/AppSettings.cs) | 잘못 구성된 훅이 자리 비운 사이 사이렌을 계속 울린다 |
| 억제는 HTTP 200 + `accepted:false` | [Dispatcher.cs](../core/Core/Dispatcher.cs) | 비-2xx를 본 훅이 에이전트 동작을 바꿀 수 있다 |
| CLI는 기본적으로 항상 0으로 종료 | [cli/Program.cs](../cli/Program.cs) | 알림 데몬이 죽었다는 이유로 에이전트가 다르게 행동한다 |
| 로컬 채널을 원격 채널보다 먼저 보낸다 | [Dispatcher.cs](../core/Core/Dispatcher.cs) | 느린 푸시 서비스가 소리를 지연시킬 수 있게 된다 |
| `PayloadMapper`는 던지지도 거부하지도 않는다 | [PayloadMapper.cs](../shared/PayloadMapper.cs) | 에이전트가 페이로드 형식을 바꾸는 날 훅이 조용히 멈춘다 |
| 트레이 메뉴 아이콘 렌더링은 `try/catch` 안에서 | [TrayIconHost.cs](../app/Tray/TrayIconHost.cs) | 창 프로시저 안이라 예외가 네이티브 경계를 넘으면 프로세스가 즉사한다. 실패하면 아이콘 없이 메뉴만 뜬다 |
| 아이콘만 있는 버튼에는 `AutomationProperties.Name` | [Fluent.cs](../app/Windows/Fluent.cs) `IconButton` | 없으면 화면 낭독기와 ui-check가 버튼을 찾지 못한다 |
| 사용자에게 보이는 문자열은 `Loc.T(en, ko)` | [UiText.cs](../app/Windows/UiText.cs) | 언어를 바꾸면 창이 즉시 다시 그려진다. 한 언어로 하드코딩한 문자열은 그대로 남는다 |
| 창을 다시 보여 줄 때는 `Fluent.BringToFront` | [Fluent.cs](../app/Windows/Fluent.cs) | `Show()`/`Activate()`만으로는 가려진 창이 앞으로 오지 않는다. 포그라운드 잠금 때문이고, 증상은 "트레이를 눌러도 아무 반응 없음" |
| `NumberBox`를 쓰지 않는다 | [SettingsWindow.cs](../app/Windows/SettingsWindow.cs) | 이 프로세스에서 렌더 시점에 XAML 예외로 프로세스가 즉사한다. 프리셋 `ComboBox`를 쓴다 |
| `App.xaml`의 `XamlControlsResources`를 지우지 않는다 | [App.xaml](../app/App.xaml) | WinUI 컨트롤 스타일이 로드되지 않아 창을 여는 순간 프로세스가 죽는다 |
| CLI는 source-gen JSON만 쓴다 | [NotipetJson.cs](../shared/NotipetJson.cs) | NativeAOT에서 트림 경고가 나고 런타임에 실패한다 |

## 네트워크

1~2단계 `app/`에는 `HttpClient`가 **어디에도 없다.** CLI의 것은 `runtime.json`의 루프백 base URL에 고정돼 있다.

외부로 나가는 코드는 3단계의 `MobilePushChannel` 하나뿐이고, `enabled && outboundNetworkApproved`가 둘 다 필요하며, 후자는 설정 UI의 확인 대화상자로만 켜진다. API로는 못 켠다. 이 구조를 관례가 아니라 구조로 유지한다 — 유지보수자 승인 없이 아웃바운드 호출을 추가하지 않는다.

## 문서

- 산문은 한국어, 식별자는 영어. `AGENTS.md`만 영어.
- CLI 명령을 추가하면 `cli/Help.cs`와 [CLI.md](CLI.md)를 같이 고친다. 스킬 내용은 `integrations/claude/skills/notipet/SKILL.md` 한 곳에만 있다(CLI에 내장됨).
- 사용자가 볼 내용(설치, 연동, CLI, 문제 해결)은 [USAGE.md](USAGE.md)에 모은다. `README.md`는 요약과 링크만 유지하고, 기능을 추가하면 `USAGE.md`도 같이 고친다.
- 새 모듈을 만들면 [modules/](modules/)에 10개 헤딩 구조(Purpose / Related Files / Public APIs / Internal Flow / State-Data Flow / Important Constraints / Known Problems / Regression Notes / Rejected Approaches / TODO)로 문서를 만들고 [PROJECT_MAP.md](PROJECT_MAP.md)에 줄을 추가한다.
- 버그를 고쳤으면 [regression.md](regression.md)에 원인·증상·영향 모듈·재발 방지를 남긴다.
- 거절한 대안은 지우지 말고 모듈 문서의 Rejected Approaches에 이유와 함께 남긴다. 다음 사람이 같은 길을 다시 걷지 않게.

## 커밋

- `settings.json` 키, `runtime.json` 스키마, CLI 플래그, HTTP 경로는 호환성에 민감하다. 바꿀 때는 폴백 읽기를 두거나 `schemaVersion`을 올린다.
- 토큰, 자격 증명, 유지보수자 머신의 절대 경로, `bin/`·`obj/`·`publish/`를 커밋하지 않는다.
