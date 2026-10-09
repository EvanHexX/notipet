# windows

## Purpose

트레이 메뉴가 담을 수 없는 두 가지를 담는 WinUI 창: 최근 알림 카드 목록과 설정. 둘 다 Fluent Design(Mica, 카드, Segoe Fluent Icons, 테마 브러시)을 따르고 EN/KO를 즉시 전환한다.

## Related Files

- [app/Windows/HistoryWindow.cs](../../app/Windows/HistoryWindow.cs) — 최근 알림 카드, 프로젝트 그룹, 필터, 비우기, 트레이 토글
- [core/Core/HistoryGrouping.cs](../../core/Core/HistoryGrouping.cs) — 카드를 프로젝트별로 나누는 순수 함수 (self-test 있음)
- [core/Core/ThreadLinks.cs](../../core/Core/ThreadLinks.cs) — 카드 → 데스크톱 앱 딥링크 (검증된 ID로만 생성)
- [core/Core/ThreadTitleLookup.cs](../../core/Core/ThreadTitleLookup.cs) — 에이전트 앱이 붙인 스레드 이름 조회
- [app/Windows/SettingsWindow.cs](../../app/Windows/SettingsWindow.cs) — 설정 창 (NavigationView 페이지)
- [app/Windows/Fluent.cs](../../app/Windows/Fluent.cs) — 공용 부품(`Card`, `SettingCard`, `ExpandedCard`, `Toggle`, `IconButton`, `LevelBadge`, `Chip`, `TitleBar`, `Chrome`)과 `Glyphs` 표
- [app/Windows/UiText.cs](../../app/Windows/UiText.cs) — 레벨·소리·사유·채널·상대 시간의 EN/KO 이름
- [app/Windows/INotipetHost.cs](../../app/Windows/INotipetHost.cs) — 창이 트레이 컨트롤러에 요구하는 것
- [app/App.xaml](../../app/App.xaml) — `XamlControlsResources` (없으면 창이 프로세스를 죽인다)
- [app/TrayController.cs](../../app/TrayController.cs) — `INotipetHost` 구현, 창 생성·수명
- [app/Program.cs](../../app/Program.cs) — `--settings` 두 번째 인스턴스 신호
- [scripts/ui-check.ps1](../../scripts/ui-check.ps1) — 모든 페이지를 실제로 열어 보는 검사

## Public APIs

```csharp
HistoryWindow(INotipetHost host)
void Activate()      // 새로 고침 후 표시 (가려져 있거나 최소화돼 있어도 앞으로)
void Toggle()        // 트레이 좌클릭 전용: 올라와 있으면 숨기고, 아니면 Activate()
void Refresh()       // 히스토리가 바뀔 때
void Relocalize()    // 언어가 바뀔 때 다시 구성

Fluent.BringToFront(Window)   // 보이기 + 최소화 해제 + 포그라운드 잠금 우회
Fluent.IsInFront(Window)      // 보이고, 최소화 아니고, 위에 겹친 창이 없나 (z-order)
INotipetHost.ThreadLink(entry) / OpenThread(entry)

SettingsWindow(INotipetHost host)
void Activate(string? page = null)   // "general" | "sound" | "desk" | "quiet" | "history" | "about"
void Relocalize()
```

## Internal Flow

창을 다시 보여 줄 때는 **반드시 `Fluent.BringToFront(window)`**를 쓴다. `AppWindow.Show(true)`는 이미 보이는 창에 아무 일도 하지 않고, `Window.Activate()`는 포그라운드 잠금에 걸려 조용히 무시된다 — 가려진 창이 그대로 가려져 있으면 사용자에게는 앱이 죽은 것으로 보인다([regression.md](../regression.md)).


XAML 파일 없이 코드로 구성한다. 테마를 따라야 하는 브러시는 `Fluent.Xaml<T>`가 `XamlReader.Load`로 `{ThemeResource ...}`를 걸어 만든다 — 코드에서 `Application.Current.Resources[...]`로 꺼낸 브러시는 테마가 바뀌어도 따라가지 않는다.

즉시 적용(확인/취소 없음). 모든 컨트롤은 `host.Settings`를 직접 고치고 `host.SettingsChanged()`를 부른다. 트레이가 저장·기록 보관 수 자르기·아이콘/메뉴 갱신을 한다.

언어 콤보는 `LanguageChanged`를 **다음 틱으로 미룬다** — 자기 자신을 다시 만드는 이벤트 핸들러 안에서 창을 재구성하면 콤보가 파괴되는 중에 이벤트가 이어진다.

### 최근 알림 창 — 그룹과 카드

`Refresh()`는 필터 → 페이지(100개) → `HistoryGrouping.Group()` 순서다. 페이지는 **카드 수**로 센다(그룹 수가 아니라). 그룹 순서는 새 카드가 있는 프로젝트가 위, 프로젝트 없음("기타", 키 `"\0other"`)은 항상 맨 아래. 대소문자가 달라도 같은 프로젝트다. 접힌 그룹은 컨트롤이 아니라 `_collapsed` 필드에 둔다 — `Refresh`가 새 알림마다, 30초마다 카드를 전부 다시 만들기 때문이다.

카드 열: 레벨 배지 | 내용 | 동작(⋯, X). 내용은 네 줄이다: 제목, 스레드, 본문, 아래 줄(에이전트 칩 · 예외 칩 · 시각 · `×N`).

- **에이전트**는 아래 줄의 칩 하나로만 표시한다(`Fluent.AgentChip` = 색 점 + 이름). 점 색은 고정 중간 톤(Claude `#D97757`, Codex `#8E6CEF`), 그 외는 `ControlStrongStrokeColorDefaultBrush`. 레벨 배지가 쓰는 상태색(초록·노랑·빨강·강조 파랑)은 쓰지 않는다 — 에이전트 색이 심각도로 읽히면 안 된다. 알림 창도 같은 점(`Fluent.AgentDot`)을 쓴다.
- **전달 칩은 예외일 때만**: `delivered`와 `disabled`(사용자가 끈 채널)는 칩을 만들지 않는다. `failed`, `skipped`, `pending`과 차단 이유만 보인다.
- 레벨 이름은 배지의 툴팁·접근성 이름, 경로는 ⋯ 메뉴 "폴더 열기"의 툴팁에 있다.
- ⋯ 메뉴와 카드의 `ContextFlyout`(우클릭)은 같은 항목을 `BuildMenu`로 **두 번 만든다** — 플라이아웃 하나를 두 소유자에 붙일 수 없다.

스레드 줄은 링크가 있으면 `HyperlinkButton`, 없으면 회색 글씨다. 링크가 있는 카드는 **카드 아무 곳이나 클릭해도** 스레드가 열린다(`LinkGrid` — `ProtectedCursor`로 손 모양 커서, 투명 배경으로 빈 곳도 클릭됨). 예외 두 가지: 카드 안의 버튼(`ButtonBase` 조상)은 각자 동작하고, **본문**은 클릭이 텍스트 선택의 시작이라 열지 않는다.

창을 열 때 포커스는 목록(`ScrollViewer`, `IsTabStop`)에 준다 — 그냥 두면 첫 컨트롤(필터 콤보, 설정 창은 첫 메뉴 항목)이 포커스 사각형을 단 채로 열린다. 첫 표시 때는 XAML이 초기 포커스를 나중에 정하므로 `DispatcherQueue`에 낮은 우선순위로 넣는다. `FocusState.Pointer`로 준다 — `Programmatic`이면 목록 둘레에 포커스 사각형이 그려진다. Tab을 누르면 그때부터 사각형이 보인다.

제목·스레드 이름처럼 한 줄로 자르는 글은 가로 `StackPanel`이 아니라 `Grid`의 `*` 열에 둔다. `StackPanel`은 자식에게 무한 폭을 줘서 `CharacterEllipsis`가 동작하지 않고, 긴 글이 잘리면서 뒤의 아이콘·개수까지 밀어낸다.

그룹 머리글을 접으면 `Refresh`가 머리글까지 다시 만든다. 새 머리글로 포커스를 돌려놓는다(`_headers`) — 안 그러면 키보드 사용자는 포커스를 잃고 화면 낭독기는 바뀐 상태를 읽지 않는다.

이름 없는 스레드는 ID의 **끝** 8자리로 표시한다. Codex ID는 UUIDv7이라 앞자리가 시각이어서, 같은 분에 시작한 스레드가 전부 같은 이름이 된다.

### 트레이 토글

`TrayController.OnTrayClicked`: 울리는 알람이 있으면 정지하고 끝(창은 다음 클릭). 아니면 `ToggleHistory()` → `HistoryWindow.Toggle()` → `Fluent.IsInFront`가 참이면 숨기고 거짓이면 `Activate()`. 메뉴·두 번째 인스턴스·`notipet open`은 **항상 보이기**(`ShowHistory`)다.

`IsInFront`는 포그라운드가 아니라 **z-order**로 판단한다. 실제 트레이 클릭은 마우스를 누르는 순간 작업 표시줄(`Shell_TrayWnd`)이 포그라운드를 가져가서, 우리 창은 클릭 메시지가 올 때 절대 포그라운드가 아니다. 그래서 우리 창 위(`GW_HWNDPREV`)의 창들 중 **보이고, 최소화·클로킹이 아니고, topmost/툴 창/비활성 창이 아니고, 우리 소유 팝업이 아니면서 겹치는** 창이 하나라도 있으면 "가려짐"이다. 사각형은 `DWMWA_EXTENDED_FRAME_BOUNDS` — `GetWindowRect`는 보이지 않는 7px 테두리가 있어서 나란히 붙인 창이 겹치는 것으로 나온다. 판단이 안 서면(예외) 거짓 — 띄우는 쪽이 숨기는 쪽보다 안전하다.

### 스레드 열기

`ThreadLinks.For(agent, threadId, hostSession)`이 만드는 링크는 두 가지뿐이다: Codex는 UUID인 스레드 ID로 `codex://threads/<id>`(공식 문서), Claude는 `local_…` 형식의 Desktop 세션 ID로 `claude://code/continue?session=…`(Desktop 앱 코드에서 확인, 문서 없음). URL·스킴을 와이어에서 받지 않는다. 스킴이 HKCR에 등록돼 있을 때만 보여 주고(등록 여부는 실행 중 한 번만 확인), 클릭 핸들러에서 `AllowSetForegroundWindow(ASFW_ANY)` 후 ShellExecute한다 — 우리가 포그라운드인 그 순간에만 대상 앱에 포그라운드를 넘길 수 있다.

`claude://resume?session=<CLI id>`는 **쓰지 않는다.** CLI 세션을 Desktop으로 가져오면서 폴더를 자동 신뢰하고 보관된 세션을 되살리는 부작용이 있다.

### 스레드 이름

`HistoryStore.Added`(전달이 끝난 뒤) → `TrayController.LookUpThreadTitle` → 작업 스레드에서 `ThreadTitleLookup.Find` → `HistoryStore.SetAppThreadTitle`(한 번만, 지워진 항목엔 안 씀) → `Changed` → 창 갱신. 카드는 `DisplayThreadTitle = 앱 이름 ?? 보낸 쪽 이름`을 보여 준다.

## State-Data Flow

```
TrayController (INotipetHost) ──보유──> SettingsWindow ──수정──> AppSettings
          │                                                   │
          ├── SettingsChanged ──> Save(SourcePath) + Trim + RefreshTray
          └── LanguageChanged ──> Loc.SetLanguage + 두 창 Relocalize()

HistoryStore.Changed ──> HistoryWindow.Refresh()  (+30초 타이머로 상대 시간 갱신)
```

여는 경로: 트레이 우클릭 메뉴 · 두 번째 인스턴스 실행(`NotipetTray.exe` = 최근, `--settings` = 설정) · `notipet open [recent|settings]`.

## Important Constraints

- **`App.xaml`의 `XamlControlsResources`는 필수다.** 없으면 첫 창이 `0xc000027b`로 프로세스를 죽인다.
- **`NumberBox`를 쓰지 않는다.** 렌더 시점에 같은 방식으로 죽인다. 프리셋 `ComboBox`를 쓴다.
- **창이 데몬을 죽이면 안 된다.** 페이지·레벨별 `try/catch`로 구성 실패를 에러 텍스트로 대체한다. 렌더 시점 예외는 잡을 수 없으므로 `ui-check.ps1`로 실제로 띄워 확인한다.
- 좁은 카드에 컨트롤 여러 개를 가로로 넣지 않는다. `Grid` 별 비율 열이나 `ExpandedCard`(라벨 위, 컨트롤 아래)를 쓴다. `TimePicker`는 최소 폭이 커서 옆 라벨을 짓누른다.
- 아이콘만 있는 버튼은 `Fluent.IconButton`으로 만든다 — 접근성 이름을 붙인다.
- 글리프는 `\uXXXX` 이스케이프로 적는다. 사용 영역(PUA) 문자를 그대로 쓰면 편집기·diff에서 보이지 않는다.
- 닫기와 Esc는 파괴가 아니라 숨기기다.
- 미리듣기는 규칙 엔진을 우회한다.

## Known Problems

- 사운드 선택은 윈도우 별칭과 `sound.library`에 등록된 이름만. 파일 추가 UI는 없다.
- 간격·횟수는 프리셋이다. 프리셋에 없는 값이면 가장 가까운 항목이 선택돼 보인다.
- 창 위치는 기억하지 않는다.
- 최근 알림은 100개씩 그리고 "더 보기"로 늘린다. 1000개를 한 번에 그리면 열 때 멈칫한다.
- 중복 병합된 알림은 **첫 알림의** 프로젝트·스레드 이름을 유지한다. 나중 것에만 이름이 있어도 반영되지 않는다(앱 이름 조회가 대부분 메운다).
- 폴더 이름이 같은 두 저장소(`E:\a\api`, `E:\b\api`)는 한 그룹이 된다. 머리글 툴팁에 경로가 둘 다 보인다.
- 트레이 토글의 "실제 클릭" 경로(포그라운드 = 작업 표시줄)는 스크립트로 재현할 수 없다. `PostMessage`로 보낸 가짜 클릭은 포그라운드를 옮기지 않는다 — 손으로 확인한다.
- Claude 링크는 문서화되지 않은 경로라 Desktop 업데이트로 바뀔 수 있다. 바뀌면 앱만 앞으로 오거나 Code 탭 첫 화면이 열린다.

## Regression Notes

- **창을 고쳤으면 `scripts\ui-check.ps1`을 돌리고 스크린샷을 본다.** 레이아웃 붕괴는 스크린샷으로만 보인다([regression.md](../regression.md)).
- 에러 텍스트(`[Sound] ...`)가 섞여 있으면 그 섹션이 구성에 실패한 것이다.
- 언어를 바꾼 뒤 두 창 모두 다른 언어 문자열이 남지 않아야 한다.

## Rejected Approaches

- **컨텍스트 메뉴 서브메뉴로 최근 알림** — 잘린 한 줄밖에 못 보여준다.
- **`NumberBox`** — 프로세스가 죽는다.
- **확인/취소 버튼** — 즉시 적용이면 동기화가 어긋날 상태가 없다.
- **트레이 좌클릭 = 테스트 알림** — 이유 없이 소리가 났다.
- **XAML 파일 + 바인딩** — 창 두 개, 페이지 여섯 개에는 코드 구성이 더 단순하고, 언어 전환이 "다시 만들기" 한 줄로 끝난다.
- **포그라운드로 토글 판단** (`GetForegroundWindow() == 우리 창`이면 닫기) — 실제 클릭에서는 작업 표시줄이 포그라운드라 **영원히 닫히지 않는다.** 가짜 클릭으로 테스트하면 통과해서 더 위험하다.
- **카드 전체 클릭을 버튼으로만 대신** — 처음엔 본문 선택·버튼과 겹친다는 이유로 링크 줄과 아이콘 버튼만 뒀지만, "알림창에서 클릭하면 그 스레드로"라는 요구에 못 미쳤다. 지금은 카드 클릭으로 열되 버튼과 본문은 뺀다(위 참고).
- **카드 한쪽 테두리만 색칠** — 둥근 카드에 한 변만 색이 다르면 깨져 보인다.
- **에이전트 색 띠(카드 왼쪽 안쪽)** — 1.2에서 칩과 함께 썼지만 같은 정보를 두 번, 그것도 띠가 더 크게 말했다. 칩의 색 점만 남겼다.
- **창 안의 큰 제목("최근 알림", 28px)** — 바로 위 제목 표시줄과 같은 말이었다. 개수는 필터 옆으로 옮겼다. 설정 창의 페이지 제목은 Subtitle(20px)로 줄였다.
- **카드마다 "소리 전달 · 알림 전달" 칩, 경로 줄, 세로로 쌓인 버튼 4개** — 거의 모든 카드에서 같거나(전달됨, 같은 그룹의 같은 경로) 겹쳤다(열기 = 카드 클릭 = 스레드 링크). 카드가 6줄에서 4줄이 됐다.
- **`claude://resume?session=<CLI id>`** — 위 "스레드 열기" 참고. 부작용이 있다.
- **Claude Desktop의 내부 세션 파일을 읽어 ID 매핑** — 다른 앱의 MSIX 가상화된 비공개 저장소다. Desktop이 CLI에 넘겨주는 `CLAUDE_CODE_HOST_SESSION_ID`를 CLI가 읽어 보내는 쪽이 낫다.

## TODO

- 사운드 파일 추가 UI ([IDEAS.md](../IDEAS.md) 10번)
- 창 위치 기억
- `PATCH /v1/settings`와의 정합성

## 알림 창 (NotificationPopup / PopupHost)

`PopupSettings.StayLevels`에 레벨이 하나라도 있거나 `ShowOverFullscreen`이 켜져 있으면 시각 채널(`TrayBalloonChannel`)이 셸 풍선 대신 `PopupHost.Show`를 부른다. 풍선으로는 둘 다 불가능하다 — 표시 시간은 Windows가 정하고, 전체화면 앱 앞에서는 셸이 알림을 보류한다. 채널 ID는 그대로라 켜고 끄기, 기록의 전달 결과, 규칙이 모두 같다.

- **포커스를 뺏지 않는다**: `AppWindow.Show(false)` — 활성화 없이 표시. 입력 중인 창의 커서가 그대로다. 검증: 표시 전후 `GetForegroundWindow`가 같다.
- **창 모양**: `OverlappedPresenter` + `SetBorderAndTitleBar(true, false)`(제목 표시줄 없는 둥근 카드), `IsShownInSwitchers = false`(작업 표시줄·Alt+Tab에 없음), `DesktopAcrylicBackdrop`.
- **전체화면 위**: `IsAlwaysOnTop` + `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)`로 topmost 띠의 맨 위에. 전체화면 영상 플레이어도 topmost인 경우가 많아서, 열린 창이 있는 동안 2초마다 다시 올린다(나중에 전체화면이 된 창이 덮는 경우). 독점 전체화면 D3D는 불가능.
- **전체화면 위 끔 + 전체화면 앞**: topmost가 아니고 포그라운드 창 **바로 뒤**에 넣는다(`SetWindowPos(hwnd, foreground)`). 전체화면을 나오면 보인다.
- **클릭**: 카드 클릭 = 알람 정지 + 닫기(풍선 클릭과 같음). X = 닫기만. X 옆 열기 아이콘("Claude/Codex에서 열기") = 스레드 열기 + 알람 정지 + 닫기. 예전엔 이름이 붙은 버튼이 한 줄을 차지해서 링크가 있는 창이 148px, 4개 쌓이면 화면 절반이었다. 지금은 모두 116px.
- **쌓기**: 주 모니터 작업 영역 오른쪽 아래부터 위로, 새 것이 아래. 최대 4개. 넘치면 가장 오래된 것부터 빠지는데, **유지하는 레벨의 창은 맨 위 오버플로 카드("외 N개")에 수를 더하고** 빠진다 — 아무 표시 없이 사라지면 "클릭할 때까지 유지"가 깨지고, 가장 오래 기다린 요청이 먼저 사라진다. 오버플로 카드가 있으면 슬롯 하나를 차지한다(카드 3 + 요약 1). 누르면 최근 알림 창을 열고 수를 0으로. 스스로 닫히는 레벨의 창은 세지 않는다.
- **보낸 쪽이 끝냄**(`/v1/resolve`): `PopupHost.CloseFor(ids)`가 그 알림의 창만 닫고, "외 N개"에 접혀 있던 것이면 수에서 뺀다(0이 되면 요약 카드도 닫힌다). 그래서 요약 카드는 수가 아니라 **알림 id 집합**을 기억한다. 실제로 닫거나 뺀 id만 돌려주고, 그게 기록의 "해결됨" 여부를 정한다.
- **자동 닫힘**: 그 레벨이 `StayLevels`에 없으면 `timeoutSec`(기본 8초) 뒤. 마우스가 올라가 있는 동안은 멈춘다.

검증(수동 스크립트로 확인): 포커스 유지, 12초 뒤에도 남아 있음, 3개 쌓임, 전체화면 topmost 창보다 위(먼저 떠 있던 것 포함), X로 하나만 닫힘, 카드 클릭으로 알람 1→0 + 닫힘, 유지 끔이면 4초(테스트 설정) 뒤 닫힘.
