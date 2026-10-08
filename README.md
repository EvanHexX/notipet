# notipet

> A Windows tray daemon that makes a sound when an AI coding agent (Claude Code, Codex) needs you — a permission prompt, a question, or a long task that just finished. Agents call it through hooks or a skill; notifications are grouped by project in a recent-notifications window, and a card can jump straight to the thread in the Claude or Codex desktop app. Local only: a loopback HTTP API with a bearer token, no outbound network. Documentation is in Korean. MIT licensed.

AI 코딩 에이전트가 사용자를 필요로 할 때 소리로 알려주는 윈도우 트레이 데몬.

Claude Code나 Codex로 긴 작업을 돌릴 때, 에이전트가 권한 승인을 기다리거나 40분짜리 작업을 끝낸 순간을 놓치기 쉽다. notipet은 트레이에 상주하면서 루프백 HTTP API를 열고, 에이전트가 훅으로 그 API를 부르면 레벨에 맞는 소리를 내고 트레이 알림을 띄운다.

```
Claude Code / Codex  ──hook──>  notipet.exe (CLI)  ──HTTP──>  NotipetTray.exe (트레이 데몬)
                                                                   ├─ 윈도우 사운드
                                                                   └─ 트레이 알림
```

## 빠른 시작

```powershell
git clone <this repo> C:\src\notipet
cd C:\src\notipet
.\scripts\publish.ps1          # bin\NotipetTray.exe 와 bin\notipet.exe 생성 (-Restart: 실행 중인 것 교체)
.\bin\NotipetTray.exe              # 트레이에 상주 시작
.\bin\notipet.exe ping         # ok  version=1.2.0  port=5523  pid=7032
.\bin\notipet.exe test         # 소리 + 알림 확인
```

문서와 예시의 `C:\src\notipet`은 예시 경로다. 실제로 받은 위치로 바꿔 읽으면 된다 — `install-hooks`와 `install-skill`은 실제 경로를 채워서 출력한다.

에이전트 연동 스니펫 출력:

```powershell
.\bin\notipet.exe install-hooks
```

출력된 블록을 `%USERPROFILE%\.claude\settings.json`과 `%USERPROFILE%\.codex\config.toml`에 붙여넣으면 된다. 전체 예시 파일은 [integrations/](integrations/)에 있다.

에이전트가 **스스로 판단해서** 구체적인 알림을 보내게 하려면 스킬을 설치한다 — 훅과 같이 쓰는 게 좋다 ([docs/SKILL.md](docs/SKILL.md)):

```powershell
.\bin\notipet.exe install-skill
```

**설치·연동·문제 해결을 포함한 전체 사용법은 [docs/USAGE.md](docs/USAGE.md)에 있다.** 아래는 요약이다.

| 문서 | 내용 |
|---|---|
| [docs/USAGE.md](docs/USAGE.md) | 설치, 연동, 트레이·옵션 창, 문제 해결 |
| [docs/CLI.md](docs/CLI.md) | CLI 명령 전체 |
| [docs/SKILL.md](docs/SKILL.md) | 에이전트 스킬 — 훅과의 차이, 설치, 겹치지 않게 하기 |
| [docs/settings.md](docs/settings.md) | `settings.json` 전체 스키마 |
| [docs/api.md](docs/api.md) | HTTP API |
| [docs/IDEAS.md](docs/IDEAS.md) | 다음에 할 만한 것 |

> **Codex 사용자 주의**: `config.toml`의 `notify` 키는 슬롯이 하나뿐이고, 이미 다른 통합이 쓰고 있을 수 있다. notipet은 `notify`를 건드리지 않고 Codex의 최신 hooks 시스템을 쓴다. 이유는 [integrations/codex/config.example.toml](integrations/codex/config.example.toml)에 적어뒀다.

## 알림 레벨

레벨이 소리의 성격을 결정한다. 기본값은 `settings.json`에서 바꿀 수 있다.

| 레벨 | 기본 동작 | 주로 언제 |
|---|---|---|
| `info` | 1회 | 잡다한 이벤트 |
| `success` | 1회 | 턴 완료 (`Stop`) |
| `attention` | 2회 반복 | **권한 대기 / 입력 대기** (`agent_needs_input`) |
| `warn` | 1회 | 경고 |
| `error` | 3회 반복 | 실패 |
| `critical` | 확인할 때까지 반복 | 놓치면 안 되는 것 |

`critical`의 반복은 트레이 아이콘 클릭, 알림 풍선 클릭, `notipet ack`, 또는 최대 지속 시간(기본 120초)에서 멈춘다. 이 상한은 설정과 무관하게 코드에서 강제된다 — 잘못 구성된 훅이 자리 비운 사이에 사이렌을 계속 울리는 일은 없어야 하기 때문이다.

## CLI

```
notipet send --title T --body B [--level L] [--tag T]    알림 보내기 (--body - 는 stdin)
             [--project P] [--thread-title T]            최근 알림 창의 그룹 / 스레드 이름 (에이전트·스레드 ID는 자동)
notipet status | ping | version | doctor                 상태, 진단
notipet history [--limit N] | history clear              최근 알림 (막힌 것도 이유와 함께)
notipet ack | mute [30m|off] | desk [on|off]             알람 정지, 음소거, PC 앞에 있음
notipet open [recent|settings]                           창 열기
notipet start | stop | restart                           데몬
notipet install-hooks | install-skill                    에이전트 연동
notipet help [명령]
```

동사 없이 부르면 에이전트 훅 페이로드로 해석한다(stdin JSON / 마지막 argv JSON). 이때는 stdout에 아무것도 쓰지 않는다. **종료 코드는 기본적으로 항상 0** — 알림 데몬이 죽었다고 에이전트 동작이 바뀌면 안 되기 때문이다(`--strict`로 옵트인). 전체는 [docs/CLI.md](docs/CLI.md).

## 트레이와 창

트레이 우클릭 메뉴(아이콘, 다크 모드 대응): 알람 정지 · **PC 앞에 있음**(체크) · 음소거 · 30분간 음소거 · 방해금지 · 최근 알림 · 옵션 · 테스트 알림 · 데이터 폴더 · 윈도우 시작 시 실행 · 종료.

트레이 좌클릭: 울리는 알람이 있으면 정지 → 아니면 최근 알림 창을 띄우거나(가려져 있어도 앞으로), 이미 올라와 있으면 닫기.

- **최근 알림** — **프로젝트별로 묶인** 카드 목록. 보낸 에이전트는 왼쪽 색 띠(주황 Claude, 보라 Codex)와 이름 칩으로, 스레드는 앱이 붙인 이름으로 보인다. **카드를 누르면 그 대화가 Claude/Codex 데스크톱 앱에서 열린다.** 전달/막힘 필터, 본문 복사, 폴더 열기, 개별 삭제, 전부 비우기. 프로젝트·에이전트를 모르는 알림은 "기타"로.
- **옵션** — Fluent 디자인, 페이지별 구성: 일반(언어 English/한국어, 시작 시 실행) · 사운드(레벨별 소리·반복) · PC 앞에 있음(알람 짧게, 소리 대체/없음) · 방해금지 · 기록(보관 개수, 비우기) · 정보.

트레이 아이콘 색이 상태를 나타낸다: 파랑(대기), 빨강(알람 울리는 중), 회색+빗금(음소거), 남색(방해금지), 주황+!(오디오 장치 없음).

## 설정

`%LOCALAPPDATA%\notipet\settings.json`. exe 옆에 `settings.json`이 있으면 그쪽을 우선한다(포터블 모드).

주요 항목과 전체 스키마는 [docs/settings.md](docs/settings.md)에 있다. 자주 건드리는 것들:

- `server.port` — 기본 `0`(임의 포트). Claude Code의 `http` 훅을 쓰려면 고정해야 한다.
- `sound.volume`, `sound.byLevel` — 레벨별 소리와 반복 방식.
- `quietHours` — 방해금지 시간대. 자정을 넘는 구간(`23:00`→`08:00`)을 제대로 처리한다.
- `rateLimit` — 소스별 토큰 버킷. 훅은 툴 호출마다 터질 수 있어서 이게 없으면 트레이가 기관총이 된다.

## HTTP API

`127.0.0.1`에만 바인딩되고, 모든 요청에 베어러 토큰이 필요하다. 토큰과 포트는 데몬이 `%LOCALAPPDATA%\notipet\runtime.json`에 쓴다.

루프백은 신뢰 경계가 아니다. 같은 사용자로 도는 프로세스는 그 파일을 읽을 수 있고 DPAPI도 도움이 안 된다. 토큰이 실제로 막는 것과 어떻게 막는지는 [docs/modules/discovery_auth.md](docs/modules/discovery_auth.md)에 정직하게 적어뒀다.

라우트 목록과 요청/응답 예시는 [docs/api.md](docs/api.md).

## 빌드와 검증

```powershell
dotnet build notipet.slnx
.\app\bin\Debug\net10.0-windows10.0.19041.0\win-x64\NotipetTray.exe --self-test   # 17개 헤드리스 검사
.\cli\bin\Debug\net10.0\win-x64\notipet.exe --self-test
.\app\bin\Debug\net10.0-windows10.0.19041.0\win-x64\NotipetTray.exe --test-sound attention   # 소리 확인
.\scripts\smoke.ps1 -FromBuildOutput -Configuration Debug                      # 실제 데몬 대상 E2E
.\scripts\ui-check.ps1 -FromBuildOutput -Configuration Debug                   # 모든 창을 실제로 열어 보기
```

`--self-test`는 무음·헤드리스다. 소리는 `--test-sound`로만 난다.

## 로드맵

- **1단계 (완료)**: 트레이 + 로컬 HTTP API + 레벨별 사운드 + 트레이 알림 + 에이전트 연동
- **2단계 (진행 중)**: 옵션 창·최근 알림 창·EN/KO·스킬 완료. 남은 것: 버튼 있는 리치 토스트, 전역 단축키
- **3단계**: 부재 감지 기반 라우팅 + 아이폰 푸시 — 자리에 있으면 소리만, 없으면 폰으로. 채널 후보 비교는 [docs/TODO.md](docs/TODO.md)에 정리돼 있다.

## 라이선스와 보안

MIT — [LICENSE](LICENSE). 보안 문제는 공개 이슈 대신 [SECURITY.md](SECURITY.md)의 방법으로 알려 주세요. 위협 모델(무엇을 막고 무엇을 막지 않는지)은 [docs/modules/discovery_auth.md](docs/modules/discovery_auth.md).
