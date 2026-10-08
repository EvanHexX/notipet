# PROJECT_MAP

기능 이름 → 실제 파일 경로. 코드를 만지기 전에 여기서 위치를 찾고, 해당 모듈 문서를 먼저 읽는다.

> 쓰는 방법을 찾고 있다면 여기가 아니라 [USAGE.md](USAGE.md)다. 이 문서는 코드 위치만 다룬다.

## 진입점과 수명 관리

| 기능 | 경로 |
|---|---|
| 데몬 `Main`, 단일 인스턴스, `--self-test` / `--test-sound` / `--settings` / `--export-icon` | [app/Program.cs](../app/Program.cs) |
| WinUI Application 객체, 미처리 예외 처리 | [app/App.xaml.cs](../app/App.xaml.cs) |
| 트레이·서버·사운드·runtime.json 수명 전체 | [app/TrayController.cs](../app/TrayController.cs) |
| CLI 진입점, 명령 분기, 호출 방식 자동 감지 | [cli/Program.cs](../cli/Program.cs) |
| CLI 명령 표와 도움말 (`help`, `<명령> --help`) | [cli/Help.cs](../cli/Help.cs) |
| `install-skill` — 내장 SKILL.md를 경로 채워 설치 | [cli/SkillInstaller.cs](../cli/SkillInstaller.cs) |

## 트레이 셸

| 기능 | 경로 |
|---|---|
| 네이티브 트레이 아이콘, 컨텍스트 메뉴, 풍선 알림, 세션 잠금/장치 변경 메시지 | [app/Tray/TrayIconHost.cs](../app/Tray/TrayIconHost.cs) |
| 상태별 트레이 아이콘, 앱 아이콘 타일 렌더링 | [app/Tray/TrayIconRenderer.cs](../app/Tray/TrayIconRenderer.cs) |
| 메뉴 아이콘(Segoe Fluent Icons → 비트맵), 다크 메뉴 | [app/Tray/MenuGlyphs.cs](../app/Tray/MenuGlyphs.cs) |
| `Notipet.ico` 생성 (`--export-icon`) | [app/Tray/IconExport.cs](../app/Tray/IconExport.cs) |
| HKCU Run 키 자동 시작 | [app/Autostart.cs](../app/Autostart.cs) |

## 창 (WinUI)

| 기능 | 경로 |
|---|---|
| 최근 알림 카드 목록, 프로젝트 그룹, 필터, 비우기, 트레이 토글 | [app/Windows/HistoryWindow.cs](../app/Windows/HistoryWindow.cs) |
| 카드를 프로젝트별로 나누기 (순수 함수) | [app/Windows/HistoryGrouping.cs](../app/Windows/HistoryGrouping.cs) |
| 옵션 창 (NavigationView: 일반/사운드/PC 앞/방해금지/기록/정보) | [app/Windows/SettingsWindow.cs](../app/Windows/SettingsWindow.cs) |
| Fluent 공용 부품 (카드, 토글, 아이콘 버튼, 제목 표시줄, Mica) + 글리프 표 | [app/Windows/Fluent.cs](../app/Windows/Fluent.cs) |
| 화면에 보이는 값의 EN/KO 이름 (레벨, 소리, 사유, 상대 시간) | [app/Windows/UiText.cs](../app/Windows/UiText.cs) |
| 창이 트레이 컨트롤러에 요구하는 것 | [app/Windows/INotipetHost.cs](../app/Windows/INotipetHost.cs) |
| **필수** WinUI 컨트롤 스타일 | [app/App.xaml](../app/App.xaml) |

## 사운드 (MVP 본체)

| 기능 | 경로 |
|---|---|
| 엔진 인터페이스 | [app/Sound/ISoundEngine.cs](../app/Sound/ISoundEngine.cs) |
| 주 엔진 (`Windows.Media.Playback.MediaPlayer`) | [app/Sound/MediaPlayerSoundEngine.cs](../app/Sound/MediaPlayerSoundEngine.cs) |
| 폴백 엔진 (`winmm` `PlaySoundW`) | [app/Sound/PlaySoundEngine.cs](../app/Sound/PlaySoundEngine.cs) |
| 윈도우 사운드 별칭 → 실제 .wav 경로 | [app/Sound/SystemSoundCatalog.cs](../app/Sound/SystemSoundCatalog.cs) |
| 요청+설정+레벨 기본값 → `ResolvedSound`, 경로 허용 검사 | [app/Sound/SoundResolver.cs](../app/Sound/SoundResolver.cs) |
| 반복 재생 루프, 지속 시간 상한 | [app/Sound/AlarmSession.cs](../app/Sound/AlarmSession.cs) |
| 울리는 중인 알람 추적, 확인(ack) 수렴 지점 | [app/Sound/AlarmRegistry.cs](../app/Sound/AlarmRegistry.cs) |
| 엔진 선택, 동시 알람 정책 | [app/Sound/SoundService.cs](../app/Sound/SoundService.cs) |

## 배달 경로

| 기능 | 경로 |
|---|---|
| 검증된 알림 객체 | [app/Core/NotificationEnvelope.cs](../app/Core/NotificationEnvelope.cs) |
| 와이어 요청 → 봉투 (절단·경고·레벨 파싱) | [app/Core/EnvelopeFactory.cs](../app/Core/EnvelopeFactory.cs) |
| 규칙 평가 → 채널 선택 → 채널별 격리 실행 | [app/Core/Dispatcher.cs](../app/Core/Dispatcher.cs) |
| 히스토리 링 버퍼, 중복 병합, 앱 스레드 이름 | [app/Core/HistoryStore.cs](../app/Core/HistoryStore.cs) |
| 카드 → Claude/Codex 데스크톱 앱 딥링크 | [app/Core/ThreadLinks.cs](../app/Core/ThreadLinks.cs) |
| 에이전트 앱이 붙인 스레드 이름 조회 (Codex session_index, Claude sessions) | [app/Core/ThreadTitleLookup.cs](../app/Core/ThreadTitleLookup.cs) |
| 채널 인터페이스 | [app/Channels/INotificationChannel.cs](../app/Channels/INotificationChannel.cs) |
| 사운드 채널 | [app/Channels/WindowsSoundChannel.cs](../app/Channels/WindowsSoundChannel.cs) |
| 트레이 알림 채널 | [app/Channels/TrayBalloonChannel.cs](../app/Channels/TrayBalloonChannel.cs) |

## 규칙

| 기능 | 경로 |
|---|---|
| 평가 순서, TTL·소스·음소거·방해금지·중복 규칙 | [app/Rules/RuleEngine.cs](../app/Rules/RuleEngine.cs) |
| 토큰 버킷 레이트 리밋 | [app/Rules/RateLimitRule.cs](../app/Rules/RateLimitRule.cs) |
| 부재 감지 (유휴 시간 + 세션 잠금 + Focus Assist) | [app/Presence/PresenceMonitor.cs](../app/Presence/PresenceMonitor.cs) |

## HTTP

| 기능 | 경로 |
|---|---|
| `HttpListener` 수명, 포트 선택, 라우트 테이블 | [app/Http/NotipetHttpServer.cs](../app/Http/NotipetHttpServer.cs) |
| 라우트 정의와 핸들러 | [app/Http/ApiRoutes.cs](../app/Http/ApiRoutes.cs) |
| JSON 읽기/쓰기, `HttpApiException` | [app/Http/HttpJson.cs](../app/Http/HttpJson.cs) |
| 베어러 토큰, Origin/Sec-Fetch-Site/Host 검사 | [app/Http/AuthGuard.cs](../app/Http/AuthGuard.cs) |
| `runtime.json` 쓰기/삭제/stale 판정 | [app/Http/RuntimeFile.cs](../app/Http/RuntimeFile.cs) |
| CLI 쪽 데몬 탐색과 자동 기동 | [cli/RuntimeDiscovery.cs](../cli/RuntimeDiscovery.cs) |

## 공유 (양쪽 exe에 컴파일됨)

`shared/` 파일을 고치면 **데몬과 CLI가 둘 다 바뀐다.**

| 기능 | 경로 |
|---|---|
| HTTP 와이어 계약 전체 | [shared/Wire.cs](../shared/Wire.cs) |
| 레벨 enum과 관용적 파서 | [shared/NotificationLevel.cs](../shared/NotificationLevel.cs) |
| 에이전트 훅 페이로드 DTO | [shared/AgentEvents.cs](../shared/AgentEvents.cs) |
| 훅 페이로드 → NotifyRequest 매핑 | [shared/PayloadMapper.cs](../shared/PayloadMapper.cs) |
| 에이전트 이름 정규화, 프로젝트 이름(저장소 루트), 스레드 ID 검증 | [shared/AgentIdentity.cs](../shared/AgentIdentity.cs) |
| JSON 소스 제너레이터 컨텍스트 (NativeAOT 필수) | [shared/NotipetJson.cs](../shared/NotipetJson.cs) |

## 설정과 경로

| 기능 | 경로 |
|---|---|
| 설정 모델·로드·저장·정규화 | [app/Settings/AppSettings.cs](../app/Settings/AppSettings.cs) |
| 데이터 디렉터리, 포터블 모드, DACL | [app/Paths.cs](../app/Paths.cs) |
| EN/KO 문자열 | [app/Loc.cs](../app/Loc.cs) |
| 크래시 로그 | [app/CrashLog.cs](../app/CrashLog.cs) |

## 검증

| 기능 | 경로 |
|---|---|
| 헤드리스 검사 집계 | [app/SelfTest/SelfTestRunner.cs](../app/SelfTest/SelfTestRunner.cs) |
| HTTP 엔드투엔드 검사 (실제 서버 + 소켓) | [app/SelfTest/HttpSelfTest.cs](../app/SelfTest/HttpSelfTest.cs) |
| 실제 데몬 대상 스모크 | [scripts/smoke.ps1](../scripts/smoke.ps1) |
| 모든 창·페이지·트레이 메뉴를 실제로 열어 보기 + 스크린샷 | [scripts/ui-check.ps1](../scripts/ui-check.ps1) |
| 배포 (`-Restart`로 실행 중인 데몬 교체) | [scripts/publish.ps1](../scripts/publish.ps1) |

개별 `RunSelfTest()`는 검증 대상 클래스 안에 함께 있다. 새 규칙이나 파서를 추가하면 그 클래스에 `RunSelfTest()`를 만들고 `SelfTestRunner`의 목록에 넣는다.

## 에이전트 연동

| 기능 | 경로 |
|---|---|
| 스킬 원본 (CLI에 내장, `install-skill`이 설치) | [integrations/claude/skills/notipet/SKILL.md](../integrations/claude/skills/notipet/SKILL.md) |
| 스킬 미지원 에이전트용 AGENTS.md 조각 | [integrations/codex/AGENTS.notipet.md](../integrations/codex/AGENTS.notipet.md) |
| 훅 설정 예시 | [integrations/](../integrations/) |

문서: [CLI.md](CLI.md) · [SKILL.md](SKILL.md) · [IDEAS.md](IDEAS.md)

## quota-scope에서 가져온 코드

[quota-scope](https://github.com/EvanHexX/quota-scope)(MIT)에서 **복사**해 왔고, 이후로는 독립적으로 진화한다. 두 저장소가 서로의 소스에 의존하지 않게 하기 위해서다.

| notipet | 원본 | 변경 |
|---|---|---|
| `app/Tray/TrayIconHost.cs` | `app-winui/Tray/TrayIconHost.cs` | `dwInfoFlags`(+`NIIF_NOSOUND`), 풍선 클릭, 체크 표시·서브메뉴, 세션 잠금·장치 변경 메시지 추가 |
| `app/Program.cs` | `app-winui/Program.cs` | 골격만. 단일 인스턴스 뮤텍스 이름과 진단 플래그가 다름 |
| `app/Autostart.cs` | `app-winui/Autostart.cs` | `ValueName`만 |
| `app/Loc.cs` | `app-winui/Loc.cs` | 상단 절반만 |
| `app/CrashLog.cs` | `app-winui/CrashLog.cs` | 경로를 `%LOCALAPPDATA%`로 |
| `app/Settings/AppSettings.cs` | `app/AppSettings.cs` | 패턴만 차용. 모델은 새로 썼고 `[JsonExtensionData]`를 추가함 |
| `app/Notipet.App.csproj` | `app-winui/QuotaScope.WinUI.csproj` | `TargetPlatformMinVersion`을 19041로 올림(MediaPlayer 요구) |
| `app/Http/*` 의 라우트·JSON 헬퍼 스타일 | 작성자의 다른 Node 서버 프로젝트 | `json()`/`readJson()`/`requireInternalSecret()` 패턴을 C#으로 옮김 |
