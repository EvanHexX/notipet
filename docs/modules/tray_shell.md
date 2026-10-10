# tray_shell

## Purpose

트레이 아이콘, 컨텍스트 메뉴, 풍선 알림, 그리고 데몬의 수명 전체.

## Related Files

- [app/Tray/TrayIconHost.cs](../../app/Tray/TrayIconHost.cs) — 네이티브 `Shell_NotifyIcon` 호스트
- [app/Tray/TrayIconRenderer.cs](../../app/Tray/TrayIconRenderer.cs) — 상태별 아이콘 런타임 렌더링
- [app/TrayController.cs](../../app/TrayController.cs) — 수명 조립
- [app/App.xaml.cs](../../app/App.xaml.cs) — WinUI Application, 미처리 예외
- [app/Program.cs](../../app/Program.cs) — 진입점, 단일 인스턴스, 진단 플래그
- [app/Channels/TrayBalloonChannel.cs](../../app/Channels/TrayBalloonChannel.cs) — 디스패처와의 접점

## Public APIs

```csharp
interface ITrayIcon : IDisposable
{
    event Action? LeftClicked;
    event Action? BalloonClicked;           // NIN_BALLOONUSERCLICK
    event Action<bool>? SessionLockChanged; // WM_WTSSESSION_CHANGE
    event Action? AudioDeviceChanged;       // WM_DEVICECHANGE
    void SetIcon(Icon);  void SetTooltip(string);
    void SetMenu(IReadOnlyList<TrayMenuItem>);
    void ShowNotification(string title, string message, BalloonLevel level);
    void Show();
}

record TrayMenuItem(string? Text, Action? Invoke, bool IsChecked = false,
                    IReadOnlyList<TrayMenuItem>? Submenu = null);
```

## Internal Flow

`TrayController` 생성자: 데이터 디렉터리 → 설정 로드 → 언어 → 사운드/히스토리/부재 감지 → 채널 등록 → 디스패처 → 트레이 시작 → 서버 시작 → 첫 갱신.

메뉴는 매번 `BuildMenu()`로 다시 만든다. 체크 표시와 "알람 정지" 항목의 존재 여부가 상태에 따라 달라지므로, 캐시하면 틀린 메뉴를 보여주게 된다.

`Dispose()`는 역순: `runtime.json` 삭제 → 서버 → 사운드 → 트레이 아이콘.

## State-Data Flow

```
HTTP 워커 스레드 ──> 채널 ──> OnUiThread(...) ──> DispatcherQueue ──> TrayIconHost
                                                                         │
히스토리 변경 / 알람 변경 ─────────────────────> RefreshTray() ──> 아이콘·툴팁·메뉴
```

아이콘 상태 우선순위(`ComputeIconState()`): `Alarming`(빨강) > `Muted`(회색+빗금) > `SoundUnavailable`(주황) > `QuietHours`(남색) > `Idle`(파랑).

## Important Constraints

- **WndProc에서 오래 걸리거나 COM을 부르는 일을 하지 않는다.** 숨은 창은 UI 스레드에 있고, 여기서 막히면 트레이·알림 창·모든 창이 함께 멈춘다(HTTP는 살아 있어서 아무도 모른다). `WM_DEVICECHANGE`는 한 번에 수십 개씩 온다 — 이벤트만 올리고 일은 `SoundService.RequestProbe`가 스레드 풀에서 묶어서 한다.
- **`UiWatchdog`**(app/UiWatchdog.cs): 5초마다 UI 스레드에 빈 작업을 넣고, 12번 연속(1분) 처리되지 않으면 `crash.log`에 남기고 트레이 아이콘을 내린 뒤 `--restart-after <pid>`로 새 인스턴스를 띄우고 스스로 끝낸다. 새 인스턴스는 옛 프로세스가 끝나길 기다렸다가(단일 인스턴스 뮤텍스) 뜨고 "다시 시작했다" 풍선을 띄운다. 최근 알림은 history.json으로 넘어간다(`history.persist`). 원인이 무엇이든 "조용히 먹통"으로 남지 않게 하는 그물이다.
- **그래픽 어댑터가 바뀌면 재시작한다**(app/GraphicsAdapters.cs): 그래픽 드라이버를 업데이트하는 등 디스플레이 어댑터가 새로 추가되면, 실행 중인 WinUI 3 앱은 글자·이미지·배경이 보이지 않게 되고, 같은 프로세스에서 새로 만든 창도 일부만 그려진다. WinUI의 미해결 버그다([microsoft/microsoft-ui-xaml#10844](https://github.com/microsoft/microsoft-ui-xaml/issues/10844)). notipet과 quota-scope에서 NVIDIA 드라이버 업데이트로 재현됐고, 그래픽 드라이버 재설정(Win+Ctrl+Shift+B)으로는 재현되지 않았다. 시작할 때 만든 DXGI 팩터리가 `IsCurrent() == false`를 돌려주면(30초마다 runtime 파일 점검과 함께 확인) 울리는 알람이 없을 때 `--restart-after <pid> graphics`로 새 인스턴스를 띄우고 끝낸다. 최근 알림은 history.json으로 넘어간다. 시험: `NOTIPET_SIMULATE_ADAPTER_CHANGE=1`로 띄운 데몬은 30초 안에 한 번 재시작한다(새 인스턴스는 이 변수를 무시).

- **`_wndProc`는 인스턴스 필드다.** 네이티브 콜백의 GC 루트이고, 없으면 `FailFast`로 프로세스가 죽는다. quota-scope가 `H.NotifyIcon.WinUI`를 걷어낸 이유가 정확히 이 버그였다.
- **풍선에 `NIIF_NOSOUND`(0x10)를 세팅한다.** 없으면 셸이 자체 알림음을 얹어 모든 알림이 이중으로 울린다.
- **메시지 전용 창이 아니라 실제(숨김) 최상위 창이다.** `TaskbarCreated` 브로드캐스트와 `WM_WTSSESSION_CHANGE`는 메시지 전용 창에 오지 않는다. 탐색기 재시작 복구와 잠금 감지가 둘 다 여기에 달려 있다.
- `Shell_NotifyIcon`은 창을 만든 스레드에서만 부를 수 있다. 채널은 HTTP 워커에서 도착하므로 `OnUiThread`로 되돌린다.
- `DispatcherShutdownMode.OnExplicitShutdown` — 창이 없으므로 기본 모드였다면 `OnLaunched` 반환 즉시 종료된다.
- 미처리 예외는 `Handled = true`로 삼키고 로그만 남긴다. 알림 하나가 잘못됐다고 데몬이 죽는 건 더 나쁘다.
- 툴팁 127자, 풍선 제목 63자, 본문 255자. 네이티브 구조체의 고정 크기 필드라서 넘기면 잘린다.
- 아이콘은 `NIM_SETVERSION`을 부르지 않는 **레거시(버전 0) 모드**다. `lParam`의 하위 워드가 마우스 메시지 그대로이고, `NIN_SELECT`는 오지 않는다. 더블클릭은 DOWN, UP, **DBLCLK**, UP으로 와서 "클릭"이 두 번이 된다 — DBLCLK 직후 1초 안의 UP 하나를 삼킨다. 버전 4로 바꾸면 `NIN_SELECT`/`NIN_KEYSELECT`와 `WM_CONTEXTMENU`로 옮겨야 한다.

## Known Problems

- 풍선에는 **액션 버튼도 알림 센터 영속성도 없다.** 그건 `AppNotificationManager`가 필요한데, unpackaged에서는 AUMID와 시작 메뉴 바로가기가 필요하고 조용히 실패할 수 있다. 그래서 별도 채널(기본 off)로 미뤘다.
- 트레이 아이콘이 알림 영역 오버플로에 숨겨져 있으면 UI Automation으로 메뉴를 자동 검증하기 어렵다. 창은 `--settings`/두 번째 실행으로 직접 열어 검증한다.

## Regression Notes

- 탐색기를 재시작하면 아이콘이 돌아와야 한다 (`TaskbarCreated` 경로).
- 트레이 좌클릭 순서(확정): ① 알람이 울리는 중이면 **정지만** — 창은 다음 클릭에서. ② 최근 알림 창이 올라와 있으면 닫기. ③ 닫혀 있거나 최소화되거나 **다른 창에 가려져 있으면** 맨 앞으로. 테스트 알림은 울리지 않는다. 판단과 함정은 [windows.md의 트레이 토글](windows.md#트레이-토글).
- 손으로 확인할 것(스크립트로는 안 되는 실제 클릭 경로): 닫힌 창 → 클릭 → 열림 → 클릭 → 닫힘 / 메모장 등으로 가린 뒤 클릭 → 닫히지 않고 올라옴 / 최소화 → 클릭 → 복원 / 숨긴 아이콘(오버플로)에서도 같음 / 알람 중 클릭 → 알람만 정지 / 더블클릭해도 창이 깜빡이지 않음.
- 풍선 클릭 → 알람 정지.
- 음소거/방해금지/자동 시작의 체크 표시가 실제 상태와 일치해야 한다.
- 오디오 장치를 전부 끄면 아이콘이 주황으로 바뀌고 툴팁이 이유를 설명해야 한다.

## Rejected Approaches

- **`H.NotifyIcon.WinUI`** — 내부 `SUBCLASSPROC` 델리게이트가 등록된 채로 GC되어 앱이 `FailFast`로 죽었다. quota-scope가 이미 겪고 걷어냈다.
- **WinForms `NotifyIcon`** — 동작하지만 집안 방향이 WinUI다.
- **정적 `.ico` 파일 5개** — 상태가 빌드 단계와 동기화되어야 한다. 런타임 렌더링이 앱 상태와 항상 일치한다.
- **`AppNotificationManager`를 MVP에** — unpackaged 등록이 조용히 실패할 수 있어 MVP가 거기에 의존하면 안 된다.

## TODO

- "알람 정지" 전역 단축키 — quota-scope의 `HotkeyWindow.cs` 복사
- ~~히스토리 항목 클릭 시 해당 cwd 열기~~ — 1.1에서 폴더 열기, 1.2에서 Claude/Codex 스레드 열기로 대체
