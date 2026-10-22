# cli_shim

## Purpose

에이전트 훅이 부르는 실행 파일. 훅 페이로드를 읽어 데몬의 HTTP API로 넘긴다.

## Related Files

- [cli/Program.cs](../../cli/Program.cs) — 동사, 호출 방식 감지, 플래그, `doctor`
- [cli/Help.cs](../../cli/Help.cs) — 명령 목록과 도움말 (한 표에서 개요와 명령별 도움말을 만든다)
- [cli/RuntimeDiscovery.cs](../../cli/RuntimeDiscovery.cs) — 데몬 탐색과 자동 기동
- [cli/ClaudeHooks.cs](../../cli/ClaudeHooks.cs) — Claude Code 훅: 출력, settings.json 넣기·빼기, 플러그인 hooks.json ([claude_plugin.md](claude_plugin.md))
- [cli/CodexHooks.cs](../../cli/CodexHooks.cs) — Codex 훅: config.toml 넣기·빼기, `approvals_reviewer` 읽기 ([codex_plugin.md](codex_plugin.md))
- [cli/SkillInstaller.cs](../../cli/SkillInstaller.cs) — `install-skill`
- [shared/PayloadMapper.cs](../../shared/PayloadMapper.cs) — 실제 매핑 (데몬과 공유)

## Public APIs

```
notipet send --title T --body B [--level L] [--tag T] [...]   # alert = send의 별칭
notipet test [--level L]
notipet status | ping | version [--json] | doctor
notipet history [--limit N] [--short] [--json] | history clear
notipet ack [--id ID | --tag TAG | --all]
notipet resolve [--tag TAG] [--id ID] [--thread ID] [--agent A]
notipet alerts [on | off | reset | mode all|selected] [--project [NAME] | --thread ID] [--events E,F]
notipet mute [30m | 2h | off] | unmute
notipet desk [on | off | toggle]
notipet open [recent | settings]
notipet start | stop | restart
notipet install-hooks [--claude | --codex] [--write | --remove] [--command EXE]
notipet install-skill [--claude | --codex | --path DIR] [--command CMD] [--print] [--force]
notipet help [명령] | --self-test
```

명령별 옵션은 `notipet help <명령>`과 [docs/CLI.md](../CLI.md).

공통 플래그: `--source`(=`--agent`), `--strict`, `--no-launch`, `--fire-and-forget`, `--quiet`, `--verbose`, `--stdin`, `--json <payload>`

## Internal Flow

동사가 없으면 훅 호출로 보고 페이로드를 찾는다. **순서가 중요하다**:

1. `--json <payload>` — 명시가 이긴다
2. **마지막 argv가 중괄호로 시작** — Codex 레거시 `notify` 방식
3. `--stdin` 또는 `Console.IsInputRedirected` — Claude Code / Codex hooks 방식
4. 그 외 — 터미널에서 인수 없이 친 `notipet`이면 도움말. 훅에서 불렸으면(옵션이 있거나, 파이프가 읽을 것을 주지 않았으면) **stdout에 아무것도 쓰지 않는다**(1.11.1). Codex의 동기 훅은 stdout을 에이전트에게 넘기기 때문이다. `--verbose`면 stderr에 한 줄.

형식 판별은 키 이름으로 한다: `last-assistant-message` / `turn-id` / `thread-id`(케밥)가 보이면 Codex `notify`, 아니면 snake_case 훅.

JSON으로는 맞는데 필드 타입이 바뀐 페이로드(문자열 자리에 객체·배열)는 버리지 않고 문자열 필드만 골라 읽는다(1.11.1, `LenientHookEvent`). 에이전트가 페이로드 모양을 바꿨다고 notipet이 조용해지면 안 된다. 깨진 JSON은 그대로 읽지 못한 것으로 처리한다.

훅 페이로드 하나는 `PayloadMapper.PlanForHookEvent`로 "끝낼 것"(`/v1/resolve`)과 "알릴 것"(`/v1/notify`)이 된다. 해제를 먼저 보낸다. 해제는 데몬이 이미 떠 있을 때만 보낸다 — 꺼져 있으면 울리는 것도 없다. Codex `PermissionRequest`는 `config.toml`의 `approvals_reviewer`가 `user`가 아니면 아무것도 하지 않는다(`CodexHooks.ApprovalsAutoReviewed`). 데몬의 `/hooks/codex` 라우트에는 이 확인이 없다([http_api.md](http_api.md)).

`alert`은 `send`의 별칭이다(Codex `notify` 설정이 쓴다). 다만 `notipet alert on|off|reset|mode|status|list`는 `alerts`를 잘못 친 것으로 보고, 아무것도 보내지 않고 stderr에 ``did you mean `notipet alerts ...`?``를 쓴 뒤 1로 끝난다(1.11.1). `alerts`는 더 이상 `alert`을 별칭으로 받지 않는다.

`--fire-and-forget`은 응답을 기다리지 않지만 요청이 실제로 나가도록 최대 400ms 기다린다(1.11.1). 예전에는 바로 끝나서 요청을 보내기도 전에 프로세스가 끝났다.

## State-Data Flow

```
Claude Code ──stdin JSON──┐
Codex hooks ──stdin JSON──┼─> ParseHookPlan ─> HookPlan  (PayloadMapper.PlanForHookEvent;
Codex notify ──argv JSON──┘        │                      케밥이면 FromCodexNotify, 알림만)
                                   ├─ Resolve / Replied ─> RuntimeDiscovery.FindAsync (띄우지 않음) ─> POST /v1/resolve
                                   └─ Notify ──┐
수동 플래그 (send) ─> NotifyRequest ───────────┴─> RuntimeDiscovery.FindOrLaunchAsync ─> POST /v1/notify
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
- `send --ttl`(`ttlSec`)은 지금 효과가 없다. 데몬은 받은 순간을 기준으로 바로 규칙을 돌려 만료될 틈이 없다([api.md](../api.md#post-v1notify)).
- NativeAOT 퍼블리시에는 MSVC 빌드 툴이 필요하다. 없으면 `publish.ps1 -NoAot`가 ReadyToRun + single-file로 떨어진다 (기동 ~30~50ms, 여전히 충분).

## Regression Notes

- 세 가지 호출 방식이 전부 동작해야 한다. `ArgParsingSelfTest`가 Codex 케밥 페이로드, Claude snake_case 페이로드, 깨진 JSON, 필드 타입이 바뀐 페이로드(여전히 그 이벤트로 울린다)를 고정한다.
- 훅으로 불렸는데 읽을 페이로드가 없으면 stdout이 비어 있어야 한다(도움말을 찍으면 Codex 동기 훅이 그것을 에이전트에게 넘긴다).
- 데몬이 꺼진 상태에서 `notipet send`가 **0으로** 종료하고 stderr에만 이유를 적는지 확인한다.
- 마지막 위치의 플래그(`--strict`)를 값 있는 옵션으로 오독해 배열 밖을 읽지 않는지 확인한다.

## Rejected Approaches

- **데몬 exe에 모드 플래그로 붙이기** — 훅은 에이전트의 임계 경로에 있고, 호출할 때마다 기동 비용을 낸다. 이 머신에서 측정한 값(7회, `Start-Process -Wait`라 양쪽 모두 하네스 오버헤드 ~30ms 포함): CLI `--help` **48ms 중앙값**, 데몬 `--help` **68ms 중앙값**. 데몬의 `--help`는 WindowsAppSDK 초기화 전에 끝나므로 저게 데몬 쪽 최선이다. 차이는 계획 단계에서 추정했던 것(80~200ms vs 5~15ms)보다 작지만 실재하고, 나머지 반은 콘솔과 종료 코드 문제다 — `WinExe`는 둘 다 없어서 훅에서 stdout/stderr와 exit code를 다루기가 어색하다.
- **Codex의 `notify` 슬롯 사용** — 슬롯이 하나뿐이고 이 머신에서는 이미 computer-use 통합이 쓰고 있다. 덮어쓰면 그게 조용히 깨진다. 게다가 `notify`는 `agent-turn-complete` 하나만 주고, 권한 대기 이벤트 추가 요청은 not-planned로 닫혔다.
- **매핑을 CLI에만 두기** — 데몬의 `/hooks/*` 라우트도 같은 매핑이 필요하다. `shared/`에 둬서 한 벌만 존재한다.

## TODO

- Codex `Stop` 페이로드 실측
