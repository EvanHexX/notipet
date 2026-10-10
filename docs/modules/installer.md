# installer

## Purpose

Notipet을 설치형 앱으로 배포하고 업데이트한다. [Velopack](https://velopack.io)(MIT)으로 사용자 폴더에 관리자 권한 없이 설치하고, GitHub Releases에서 업데이트를 받는다. 저장소에서 `publish.ps1`로 만드는 `bin\`은 개발용으로 그대로 남는다.

## Related Files

- [app/Program.cs](../../app/Program.cs) — `Main` 첫 줄의 `VelopackApp.Build()...Run()`: 설치·업데이트·제거 때 Velopack이 특수 인수로 앱을 실행하면 여기서 처리하고 끝난다
- [app/Update/Installation.cs](../../app/Update/Installation.cs) — 설치·업데이트·제거 때 하는 일(PATH, 자동 시작, 실행 중인 앱 정리)
- [app/Update/Updater.cs](../../app/Update/Updater.cs) — 업데이트 확인·받기·적용. `app/`에서 유일하게 외부와 통신하는 곳
- [app/TrayController.cs](../../app/TrayController.cs) — 트레이 메뉴 "업데이트 확인", 자동 확인 타이머, 업데이트 직전 정리(`PrepareForUpdate`)
- [app/Windows/SettingsWindow.cs](../../app/Windows/SettingsWindow.cs) — 설정 → 정보 → 업데이트
- [scripts/pack.ps1](../../scripts/pack.ps1) — 설치 파일과 업데이트 피드 만들기
- [shared/BuildVersion.cs](../../shared/BuildVersion.cs) — 버전은 빌드에서 읽는다(csproj `<Version>` 또는 `-p:Version=`)

## Public APIs

```
scripts\pack.ps1 [-Version 1.5.0] [-Out artifacts\releases] [-Delta] [-NoAot]
scripts\publish.ps1 [-OutDir DIR] [-Version V]      # pack.ps1이 부른다

Updater.CheckAsync()                   → UpdateState (UpToDate | Available | Failed | NotInstalled)
Updater.DownloadAndRestartAsync(prep)  → 받고, prep() 후 Velopack이 교체·재시작
NOTIPET_UPDATE_FEED=<폴더>              → GitHub 대신 그 폴더(vpk pack 결과)를 피드로 (테스트용, 저장 안 됨)
```

설정: `updates.autoCheck`(기본 false), `updates.lastAutoCheck`, `updates.notifiedVersion` — [settings.md](../settings.md).

## Internal Flow

**설치** (`NotipetApp-win-Setup.exe`):
1. `%LOCALAPPDATA%\NotipetApp\current\`에 두 exe와 런타임을 푼다. 시작 메뉴에 "Notipet" 바로 가기(바탕 화면은 만들지 않는다), "앱 및 기능"에 "Notipet" 등록.
2. 설치 직후 훅(`OnAfterInstallFastCallback` → `Installation.OnInstalled`): `current\`를 사용자 PATH(`HKCU\Environment`)에 추가하고 `WM_SETTINGCHANGE`를 알린다 — 새 터미널에서 `notipet`이 된다. "Windows 시작 시 실행"이 다른 경로(예: `bin\`)를 가리키면 설치 경로로 바꾼다.

**업데이트** (사용자가 누를 때만):
1. 트레이 "업데이트 확인" 또는 설정 → 정보 → "지금 확인" → `CheckAsync` — GitHub Releases의 `releases.win.json`을 읽는다. 받지 않는다.
2. 새 버전이 있으면 트레이 메뉴가 "X(으)로 업데이트"로, 정보 페이지 버튼이 "X 설치"로 바뀐다.
3. 누르면 `DownloadAndRestartAsync`: 받고(델타가 있으면 델타) → `PrepareForUpdate`(daemon.log, 감시 장치·runtime 복구 중지, 알람 정지, runtime.json 삭제, 트레이 아이콘 제거) → `ApplyUpdatesAndRestart`로 Velopack이 이 프로세스를 끝내고 `current\`를 바꾼 뒤 새 버전을 띄운다. 업데이트 훅이 PATH·자동 시작을 다시 맞춘다.

**자동 확인** (`updates.autoCheck`를 켰을 때만): 시작 3분 뒤부터 3시간마다 깨어나, 마지막 자동 확인에서 24시간이 지났으면 확인만 한다. 같은 새 버전은 한 번만 알린다(`notifiedVersion`).

**제거** ("앱 및 기능" 또는 `Update.exe --uninstall`): 훅(`OnBeforeUninstallFastCallback`)이 PATH에서 빼고, 자동 시작이 설치 경로를 가리키면 지우고, 설치 폴더에서 돌던 데몬을 멈추고 그 `runtime.json`을 지운다. Velopack이 `%LOCALAPPDATA%\NotipetApp`을 지운다. **`%LOCALAPPDATA%\notipet`(설정·로그)은 남는다** — 폴더가 다르기 때문이다.

## State-Data Flow

```
%LOCALAPPDATA%\NotipetApp\          ← Velopack이 관리. 제거하면 통째로 사라진다
  Update.exe, Notipet.exe(실행기), packages\
  current\NotipetTray.exe, notipet.exe, ...   ← 업데이트해도 경로가 같다 (PATH·자동 시작·훅이 여기를 가리켜도 된다)
%LOCALAPPDATA%\notipet\             ← 앱 데이터. 설치·업데이트·제거와 무관
  settings.json, runtime.json, daemon.log, crash.log, daemon.path
```

## Important Constraints

- **외부 통신은 업데이트뿐이고, 사용자가 시킬 때만.** AGENTS.md "Local only"의 예외. 기본은 누를 때만 확인하고, 자동 확인은 설정에서 켜야 한다(API로는 못 바꾼다). 확인은 받지 않고, 받기·설치는 항상 따로 누른다 — 알람을 울리던 데몬이 스스로 바뀌면 그 알람과 최근 알림이 사라진다.
- **패키지 ID는 `NotipetApp`이다, `notipet`이 아니다.** Velopack은 `%LOCALAPPDATA%\<ID>`를 통째로 관리·삭제한다. `notipet`이었다면(대소문자 무시) 제거할 때 사용자 설정까지 지워졌다.
- **`VelopackApp...Run()`은 `Main`의 맨 처음이다.** 설치·제거 훅이 단일 인스턴스 뮤텍스나 XAML보다 먼저 처리돼야 한다. 훅은 30초 안에 끝나야 하고(넘으면 Velopack이 죽인다) 던지지 않는다.
- `SetAutoApplyOnStartup(false)` — 받아 둔 업데이트를 시작할 때 저절로 적용하지 않는다.
- 설치하지 않은 빌드(`bin\`, 빌드 출력)는 업데이트할 수 없다. 정보 페이지가 그렇게 말하고 트레이 메뉴에 항목이 없다.
- 버전은 빌드에서 읽는다(`BuildVersion.Of`). 상수를 고치는 대신 csproj `<Version>`을 바꾼다.

## Known Problems

- **서명하지 않았다.** 처음 실행하면 SmartScreen이 "Windows의 PC 보호" 창을 띄운다("추가 정보 → 실행"). 코드 서명 인증서가 생기면 `vpk pack --signParams`로.
- 델타 업데이트는 `pack.ps1 -Delta`로 직전 GitHub 릴리스를 받아 와야 만들어진다. 없으면 전체 패키지(~95MB)를 받는다.
- 업데이트·재시작 때 최근 알림 기록(메모리)은 사라진다.
- 업데이트 확인은 GitHub 비인증 API 한도(IP당 시간당 60회)를 쓴다. 사람이 누르는 빈도로는 문제없다.

## Regression Notes

- 설치 → PATH에 `current\`, 시작 메뉴 바로 가기, "앱 및 기능" 항목, daemon.log `installed`.
- 업데이트: rc.1 설치 → `NOTIPET_UPDATE_FEED`로 rc.2 피드 → 정보 → 지금 확인 → "rc.2 설치" → 재시작 후 `notipet ping`이 rc.2. daemon.log에 `quit: updating to` → `updated to` → `started`.
- 제거 → 앱 폴더·PATH·바로 가기·등록 항목이 없고, 설치 폴더의 데몬이 멈췄고, `%LOCALAPPDATA%\notipet\settings.json`은 남아 있다.

## Rejected Approaches

- **MSIX** — 코드 서명 인증서(유료) 또는 Store 등록이 필요하다. Store로 갈 때 다시 본다(그때는 Store가 업데이트하므로 이 업데이트 기능을 숨긴다).
- **Inno Setup** — 설치 마법사는 쉽지만 업데이트가 "새 설치 파일 다시 실행"뿐이다.
- **자동으로 받아서 설치** — 위 Important Constraints.
- **PATH의 `notipet`만으로 훅 연결** — 설치 경로(`current\`)가 업데이트해도 바뀌지 않으니 절대 경로가 더 단단하다. 이미 떠 있는 에이전트 앱은 바뀐 PATH를 모른다.

## TODO

- 코드 서명
- ~~Codex 플러그인~~ → [codex_plugin.md](codex_plugin.md) (1.5.1). `install-hooks --codex --write`는 설치된 `current\notipet.exe`를 훅에 적으므로 이 경로는 바꾸지 않는다(바꾸면 모든 사용자가 훅을 다시 신뢰해야 한다)
