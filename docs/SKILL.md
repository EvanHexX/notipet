# 에이전트가 스스로 알림을 보내게 하기 — 스킬

에이전트가 notipet으로 알림을 보내는 방법은 두 가지다. 성격이 달라서 **같이 쓰는 게 제일 낫다.**

| | 훅 (hooks) | 스킬 (skill) |
|---|---|---|
| 누가 결정하나 | 에이전트 **프로그램**이 이벤트마다 자동으로 | 에이전트(**LLM**)가 상황을 보고 판단해서 |
| 언제 | 턴 종료, 권한 대기, 서브에이전트 종료 등 정해진 이벤트 | "이건 사용자가 알아야 한다"고 판단한 순간 |
| 내용 | 이벤트 이름 수준 ("Turn complete", "Waiting for your input") | 구체적 ("마이그레이션 3/5단계에서 unique 제약 위반, 아무것도 커밋 안 됨") |
| 놓칠 수 있나 | 없다 — 이벤트가 나면 무조건 | 있다 — LLM이 잊거나 판단을 잘못하면 |
| 설정 | `notipet install-hooks` → 설정 파일에 붙여넣기 | `notipet install-skill` 한 줄 |

**권장:** 훅으로 바닥을 깔고(놓치지 않음), 스킬로 내용을 채운다(무엇이 필요한지 알 수 있음).

---

## 1. 설치

```powershell
notipet install-skill                 # Claude Code (사용자 전체)
notipet install-skill --codex         # Codex (skills를 지원하는 버전)
notipet install-skill --path E:\my-repo\.claude\skills   # 특정 프로젝트에만
```

`%USERPROFILE%\.claude\skills\notipet\SKILL.md`가 생긴다. 파일 안의 명령 경로는 **이 CLI의 실제 경로**로 채워지므로 PATH 설정이 필요 없다.

**다음 세션부터** 적용된다. 확인:

```powershell
notipet doctor      # [ok  ] claude skill  ...SKILL.md
```

에이전트에게 "끝나면 알려줘"라고 말하거나 긴 작업을 시키면 스킬이 쓰인다.

> Codex의 skills 폴더(`~/.codex/skills`)는 Codex 버전에 따라 지원 여부가 다르다. 지원하지 않는 버전이면 아래 [4절](#4-스킬을-지원하지-않는-에이전트)의 AGENTS.md 방식을 쓴다.

---

## 2. 스킬이 에이전트에게 가르치는 것

원본: [../integrations/claude/skills/notipet/SKILL.md](../integrations/claude/skills/notipet/SKILL.md) — `install-skill`은 이 파일을 CLI에 내장해 두었다가 그대로 쓴다. 한 벌만 있으므로 문서와 설치본이 어긋나지 않는다.

### 언제 보내나

| 상황 | 레벨 |
|---|---|
| 사용자가 필요하다 — 질문, 결정, 승인, 선택지 | `attention` |
| 긴 작업이 끝났다 — 빌드, 테스트, 마이그레이션, 리팩터링 (몇 분 이상) | `success` |
| 막혔거나 실패해서 사용자 없이는 진행할 수 없다 | `error` |
| 놓치면 안 된다 — 데이터 손실 위험, 파괴적 작업 직전, 운영 장애 | `critical` (드물게) |

### 보내지 않는 것

- 사용자가 지켜보고 있는 짧은 작업
- 단계마다, 파일마다, 툴 호출마다
- 같은 순간을 두 번 — **훅이 이미 "턴 끝남"을 알린다면, 더 구체적인 내용이 있을 때만 보낸다**

### 보내는 양식

```
notipet send --title "<프로젝트>: <무슨 일>" --body "<한두 줄>" --level <레벨> --tag "<프로젝트>:<순간>" \
             --project "<프로젝트>" --thread-title "<이 대화의 짧은 이름>"
```

| 항목 | 누가 채우나 | 빠지면 |
|---|---|---|
| 에이전트 (claude-code / codex) | **CLI가 자동으로** (환경 변수). 한 에이전트 안에서 다른 에이전트를 돌릴 때만 `--agent`로 지정 | `manual`, 회색 띠 |
| `--project` | LLM. 안 주면 CLI가 현재 폴더의 저장소 이름으로 | "기타" 그룹 |
| `--thread-title` | LLM. **한 대화에서는 같은 이름** | 앱이 붙인 이름 → 짧은 ID |
| 스레드 ID | **CLI가 자동으로** (`CLAUDE_CODE_SESSION_ID`, `CODEX_SESSION_ID`) — LLM은 자기 세션 ID를 모른다 | 스레드 링크 없음 |
| Claude Desktop 세션 ID | **CLI가 자동으로** (`CLAUDE_CODE_HOST_SESSION_ID`) | "Claude에서 열기" 없음 |

전부 선택이다. 아무것도 없어도 알림은 간다 — 최근 알림 창에서 "기타"로 분류될 뿐이다. 자세한 규칙은 [CLI.md](CLI.md#에이전트-안에서-부를-때--자동으로-채워지는-것).

### 규칙

- 제목은 프로젝트 이름으로 시작 (`quota-scope: 결정 필요`) — 팝업에는 제목과 본문만 보이기 때문
- `--thread-title`은 한 대화에서 같은 말로. 매번 다르게 쓰면 카드가 같은 스레드로 안 읽힌다
- 스킬 파일은 Claude용과 Codex용(`install-skill --codex`)이 **같은 파일**이다. 그래서 `--agent`를 박아 두지 않는다 — 박아 두면 Codex가 Claude로 기록된다
- 태그는 `<프로젝트>:<순간>`이면 충분하다. 같은 태그라도 **다른 대화(스레드)면 따로** 울리고, 같은 대화에서 30초 안에 반복되면 한 번으로 합쳐진다
- 본문은 한두 문장: 무슨 일이 있었고 무엇이 필요한가
- **비밀·토큰·비밀번호·큰 diff를 본문에 넣지 않는다** — 화면을 보는 누구나 읽을 수 있다
- **사용자가 쓰는 언어로** 쓴다
- 기다리기 **전에** 보낸다, 후가 아니라
- 실패해도 재시도 루프를 돌지 않는다 (CLI는 어차피 항상 0으로 종료)
- 사용자가 "조용히 해"라고 하면 그 세션 동안 보내지 않는다

---

## 3. 훅과 겹치지 않게 하기

훅과 스킬을 같이 쓰면 턴이 끝날 때 둘 다 울릴 수 있다. notipet의 중복 병합은 **태그 + 스레드**로 동작한다(같은 대화에서 같은 태그가 30초 안에 오면 한 번만 울림). 훅의 태그는 `claude-code:<세션>:<이벤트>` 모양이라 스킬의 태그와 겹치지 않는다 — 태그를 맞춰서 합치는 방법은 없다. 선택지:

**A. 그대로 둔다** — 훅의 알림은 "Turn complete", 스킬의 알림은 구체적인 내용. 제목이 달라서 둘 다 뜨지만, 하나는 내용이 있으니 오히려 유용할 수 있다.

**B. 훅의 `Stop`을 뺀다** — 턴 종료 알림은 스킬에 맡기고, 훅은 `Notification`(권한·입력 대기)만 남긴다. 권한 대기는 LLM이 판단할 틈이 없는 순간이라 훅이 맡아야 한다.

```json
{ "hooks": {
    "Notification": [ { "matcher": "agent_needs_input|permission_prompt|idle_prompt",
      "hooks": [ { "type": "command", "command": "C:\\src\\notipet\\bin\\notipet.exe",
                   "args": ["--source", "claude-code"], "timeout": 10, "async": true } ] } ]
} }
```

**C. 자리 착석을 활용한다** — 자리에 있을 때는 `notipet desk on`. 두 알림이 모두 짧은 소리 한 번으로 줄어든다. 옵션에서 "다른 소리로 대체 → 소리 없음"을 켜면 알림만 조용히 뜬다.

개인적으로는 **B**를 권한다.

---

## 4. 스킬을 지원하지 않는 에이전트

`SKILL.md`는 결국 에이전트에게 주는 지침이다. 스킬 폴더를 지원하지 않는 에이전트라면 같은 내용을 에이전트의 상시 지침 파일에 넣는다.

- **Codex**: 저장소 루트 또는 `%USERPROFILE%\.codex\AGENTS.md`
- **Claude Code**: 저장소의 `CLAUDE.md` 또는 `%USERPROFILE%\.claude\CLAUDE.md`
- **그 외**: 시스템 프롬프트 / 규칙 파일

붙여넣을 요약본: [../integrations/codex/AGENTS.notipet.md](../integrations/codex/AGENTS.notipet.md)

전체본이 필요하면:

```powershell
notipet install-skill --print > notipet-skill.md
```

`---`로 둘러싸인 맨 위 머리말(name/description)만 빼고 붙여넣으면 된다.

---

## 5. 직접 고치기

설치된 `SKILL.md`는 평범한 마크다운이라 고쳐도 된다. 자주 고칠 만한 것:

- **description** — 에이전트가 스킬을 **언제 불러올지**를 이것으로 판단한다. 너무 넓으면 쓸데없이 불리고, 너무 좁으면 안 불린다. "긴 작업이 끝나면 항상"처럼 원하는 기준을 명시한다.
- **레벨 기준** — "몇 분 이상"을 "10분 이상"으로 바꾸는 식.
- **언어** — 알림을 항상 한국어로 받고 싶다면 규칙에 명시.

고친 뒤 `install-skill`을 다시 돌리면 **고친 파일은 덮어쓰지 않고** 멈춘다. 새 버전으로 바꾸려면 `--force`.

---

## 6. 동작 확인

스킬이 실제로 쓰이는지 보려면 에이전트에게 이렇게 시켜 본다:

> 30초 정도 걸리는 작업을 하나 하고, 끝나면 notipet으로 알려줘.

그다음:

```powershell
notipet history --limit 3
```

`source`가 `claude-code`이고 제목이 구체적이면 스킬이 동작한 것이다. 훅이 보낸 알림은 제목이 `Claude Code - <폴더>`, 본문이 `Turn complete` 같은 이벤트 이름이다.

`[프로젝트]`와 `# 스레드 이름`도 같이 보이면 양식대로 온 것이다. 최근 알림 창에서는 그 프로젝트 그룹 아래, 에이전트 색 띠와 함께 보이고, 스레드 이름이 파란 링크면 눌러서 그 대화로 갈 수 있다.

> 이미 설치한 스킬은 자동으로 바뀌지 않는다. 새 양식을 쓰려면 `notipet install-skill --force`. `notipet send`를 부르는 기존 스킬이나 지침은 고치지 않아도 된다 — `--source codex`만 줘도 스레드 ID와 프로젝트는 CLI가 채운다.
