# cli_shim

## Purpose

에이전트 훅이 부르는 실행 파일. 훅 페이로드를 읽어 데몬의 HTTP API로 넘긴다.

## Related Files

- [cli/Program.cs](../../cli/Program.cs) — 동사, 호출 방식 감지, 플래그
- [cli/RuntimeDiscovery.cs](../../cli/RuntimeDiscovery.cs) — 데몬 탐색과 자동 기동
- [cli/HookSnippets.cs](../../cli/HookSnippets.cs) — `install-hooks`가 출력하는 설정 블록
- [shared/PayloadMapper.cs](../../shared/PayloadMapper.cs) — 실제 매핑 (데몬과 공유)

## Public APIs

```
notipet send --title T --body B [--level L] [--tag T] [--repeat M] [--sound N] [--volume V]
notipet ping | status
notipet ack [--id ID | --tag TAG | --all]
notipet mute [30m|2h|off]
notipet test [--level L]
notipet install-hooks [--claude] [--codex]
notipet --self-test
```

공통 플래그: `--source`, `--strict`, `--no-launch`, `--fire-and-forget`, `--quiet`, `--stdin`, `--json <payload>`

## Internal Flow

동사가 없으면 훅 호출로 보고 페이로드를 찾는다. **순서가 중요하다**:

1. `--json <payload>` — 명시가 이긴다
2. **마지막 argv가 중괄호로 시작** — Codex 레거시 `notify` 방식
3. `--stdin` 또는 `Console.IsInputRedirected` — Claude Code / Codex hooks 방식
4. 그 외 — 사용법 출력

형식 판별은 키 이름으로 한다: `last-assistant-message` / `turn-id` / `thread-id`(케밥)가 보이면 Codex `notify`, 아니면 snake_case 훅.

## State-Data Flow

```
Claude Code ──stdin JSON──┐
Codex hooks ──stdin JSON──┤
Codex notify ──argv JSON──┼─> ParseHookJson ─> PayloadMapper ─> NotifyRequest
수동 플래그 ──────────────┘                                          │
                                                                     v
                    RuntimeDiscovery.FindOrLaunchAsync ─> POST /v1/notify
```

## Important Constraints

- **기본 종료 코드는 항상 0이다.** 전달이 실패해도 그렇다. Claude Code는 특정 비-0 종료를 블로킹/피드백 신호로 해석하고, 알림 데몬이 죽었다는 이유로 에이전트가 다르게 행동하면 안 된다. `--strict`가 옵트인.
- **stdin 읽기에 타임아웃을 건다** (총 1초). 닫히지 않는 stdin을 기다리다 에이전트를 붙잡는 일이 없어야 한다.
- **Codex의 argv 페이로드는 파싱 실패가 정상 케이스다.** openai/codex#25141이 미해결이라 `input-messages`가 프롬프트 히스토리 전체로 불어나 윈도우 커맨드라인 한계를 넘을 수 있다. 실패하면 크래시가 아니라 "Turn complete (payload unreadable)" 일반 알림으로 떨어진다.
- **NativeAOT + source-gen JSON이 필수다.** 리플렉션 기반 STJ는 트림 경고가 나고 런타임에 실패한다.
- 자동 기동은 기본 on. 재부팅 후 첫 알림을 잃는 것보다 낫다는 판단이고, `--no-launch`가 반대 의견용이다.
- `HttpClient`는 `runtime.json`의 루프백 base URL에 고정된다. CLI에 다른 목적지는 없다.

## Known Problems

- Codex `Stop` 훅 페이로드에 `last_assistant_message`가 있는지 실제로 확인하지 못했다. 없으면 본문이 "Turn complete"로만 나온다.
- `install-hooks`는 출력만 하고 파일을 쓰지 않는다 (`--write`는 TODO).
- NativeAOT 퍼블리시에는 MSVC 빌드 툴이 필요하다. 없으면 `publish.ps1 -NoAot`가 ReadyToRun + single-file로 떨어진다 (기동 ~30~50ms, 여전히 충분).

## Regression Notes

- 세 가지 호출 방식이 전부 동작해야 한다. `ArgParsingSelfTest`가 Codex 케밥 페이로드, Claude snake_case 페이로드, 깨진 JSON을 고정한다.
- 데몬이 꺼진 상태에서 `notipet send`가 **0으로** 종료하고 stderr에만 이유를 적는지 확인한다.
- 마지막 위치의 플래그(`--strict`)를 값 있는 옵션으로 오독해 배열 밖을 읽지 않는지 확인한다.

## Rejected Approaches

- **데몬 exe에 모드 플래그로 붙이기** — 훅은 에이전트의 임계 경로에 있고, 호출할 때마다 기동 비용을 낸다. 이 머신에서 측정한 값(7회, `Start-Process -Wait`라 양쪽 모두 하네스 오버헤드 ~30ms 포함): CLI `--help` **48ms 중앙값**, 데몬 `--help` **68ms 중앙값**. 데몬의 `--help`는 WindowsAppSDK 초기화 전에 끝나므로 저게 데몬 쪽 최선이다. 차이는 계획 단계에서 추정했던 것(80~200ms vs 5~15ms)보다 작지만 실재하고, 나머지 반은 콘솔과 종료 코드 문제다 — `WinExe`는 둘 다 없어서 훅에서 stdout/stderr와 exit code를 다루기가 어색하다.
- **Codex의 `notify` 슬롯 사용** — 슬롯이 하나뿐이고 이 머신에서는 이미 computer-use 통합이 쓰고 있다. 덮어쓰면 그게 조용히 깨진다. 게다가 `notify`는 `agent-turn-complete` 하나만 주고, 권한 대기 이벤트 추가 요청은 not-planned로 닫혔다.
- **매핑을 CLI에만 두기** — 데몬의 `/hooks/*` 라우트도 같은 매핑이 필요하다. `shared/`에 둬서 한 벌만 존재한다.

## TODO

- `install-hooks --write`
- Codex `Stop` 페이로드 실측
