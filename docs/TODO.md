# TODO

## Short Term

- [ ] `CrashLog`의 출력 경로를 주입 가능하게 만들어 `--self-test`가 사용자 `crash.log`에 쓰지 않게 한다 ([regression.md](regression.md) 참고)
- [ ] 레벨별 기본 사운드를 며칠 실제로 써보고 조정한다. 특히 `attention`(권한 대기) 2회 반복이 유용한지 성가신지 — 이 설정 하나가 도구의 인상을 가른다
- [ ] `sources[].levelFloor`를 `SourceRule`에 실제로 연결한다 (모델에만 있고 읽히지 않음)
- [ ] `notipet install-hooks --write`: 현재는 출력만 한다. 확인 후 `settings.json` / `config.toml`을 실제로 편집하는 경로
- [ ] Codex `Stop` 훅 페이로드에 `last_assistant_message`가 있는지 실제로 찍어 확인하고 `PayloadMapper`를 맞춘다 (조사 단계에서 확인하지 못한 항목)
- [x] 최근 알림 카드에서 cwd 폴더 열기 (1.1)

## 2단계 — UI

- [x] 설정 창 (1.1): Fluent NavigationView, 볼륨·레벨별 사운드·PC 앞·방해금지·기록 보관 수·언어. 남은 것: 포트, 토큰 회전, 사운드 파일 추가
- [x] 최근 알림 창 (1.1): 억제 이유 표시, 필터, 비우기, 개별 삭제
- [ ] `WindowsToastChannel`: `AppNotificationManager`로 버튼 있는 리치 토스트
  - 위험: unpackaged에서는 AUMID와 시작 메뉴 바로가기가 필요하고 HKCU에 COM 액티베이터를 등록한다. 앱 이름이 틀리게 뜨거나 활성화 콜백이 안 오는 조용한 실패가 가능하다. **채널로 분리해 기본 off를 유지하고**, MVP의 시각 알림은 검증된 풍선이 계속 담당한다
- [ ] "알람 정지" 전역 단축키 — quota-scope의 `HotkeyWindow.cs`를 복사
- [ ] `PATCH /v1/settings` (허용 목록 방식). `outboundNetworkApproved`는 **절대 포함하지 않는다**
- [ ] 토큰 회전을 트레이 메뉴에서

- [x] EN/한국어 전환, 즉시 적용 (1.1)
- [x] 에이전트 스킬 + `install-skill` (1.1)
- 다음 후보와 근거는 [IDEAS.md](IDEAS.md)

## 3단계 — 부재 감지와 아이폰

- [ ] `PresenceRule`이 실제로 라우팅하도록: `{"when":{"presence":"Away","levelAtLeast":"attention"},"channels":["mobile_push"]}`
- [ ] `docs/modules/mobile_push.md`를 **결정 문서로 먼저 쓰고** 승인받은 뒤 채널을 작성한다
- [ ] `MobilePushChannel` — 아래 결론에 따라 Bark부터

### 아이폰 채널 조사 결론 (2026-09-20 기준)

먼저 알아야 할 제약: iOS에서 **무음 스위치와 Focus를 모두 뚫는 것은 Critical Alerts뿐**이고, 그 entitlement는 Apple이 수동 심사한다(혈당 모니터, 환자 모니터링, 응급 대응, 기상 경보 수준). "AI 에이전트가 끝났다"로는 승인되지 않는다. 직접 iOS 앱을 만드는 길은 $99/년 + APNs HTTP/2 배관을 들이고도 Time Sensitive(Focus만 통과)가 천장이고, 무료 개인 팀 계정은 푸시 entitlement 자체를 못 받는다. → **이미 entitlement를 가진 앱에 얹는 것이 유일한 현실적 경로다.**

| | 비용 | 무음 뚫기 | Focus 뚫기 | 확인까지 반복 | 자체 호스팅 | Apple 계정 |
|---|---|---|---|---|---|---|
| **Bark** | 무료 | O (`level=critical` + `volume` 0~10) | O | 부분 (`call=1` ≈ 30초) | O (APNs 인증서가 서버에 내장) | 불필요 |
| **Pushover** | $4.99 1회 | O (2020년부터) | O | O (`priority=2` + `retry`/`expire` + receipt) | X | 불필요 |
| Telegram 봇 | 무료 | X | 수동 허용만 | X | X | 불필요 |
| ntfy | 무료(~250/일) | 아직 아님 | 아직 아님 | X | 부분 (iOS는 ntfy.sh 경유) | 불필요 |
| 자체 iOS 앱 | $99/년 | 승인 안 됨 | O | 직접 구현 | — | 필요 |
| iOS PWA 웹푸시 | 무료+호스팅 | X | X | X | O | 불필요 |

**1순위 Bark.** 무료, 오픈소스(Finb/Bark + bark-server, MIT), Docker로 자체 호스팅, **APNs 인증서가 bark-server에 내장돼 있어** 내 서버가 공식 App Store 앱으로 직접 푸시한다 — Apple 계정도 인증서 관리도 없다. `level=critical` + `volume` 0~10으로 진짜 무음 뚫기가 되고 `ciphertext`로 E2E 암호화 페이로드도 된다. 윈도우 쪽은 아웃바운드 HTTP 한 번.
약점: **확인(ack) 개념이 없다**(`call=1`은 30초 연속 울림이지 무한 반복이 아니다), 그리고 유지관리자 한 명의 App Store 등록과 내장 인증서에 의존한다.

**2순위 Pushover.** ack 의미론이 실제로 필요하다고 판명되면 올린다. $4.99 1회, 월 1만 건 무료. `priority=2` + `retry=60` + `expire=3600`이면 확인할 때까지 1분마다 1시간 동안 다시 울린다. NAT 뒤이므로 `callback` 파라미터 대신 **receipt를 아웃바운드로 폴링**한다.

**ntfy는 보류.** iOS Critical Alerts는 앱 PR이 병합됐지만 서버 PR(#1908)이 미병합이고 App Store 빌드 반영이 확인되지 않았으며, 공식 known-issues에 iOS 26에서 알림 소리가 안 나고 다른 앱 소리까지 깨지는 버그가 있다 — 알람 용도에는 결격.

구현은 Apprise식 URL 스킴(`bark://`, `pushover://`)을 설정 형식으로 흉내 내되 Python 의존성은 들이지 않는다.

## Later

- [ ] quota-scope 연계(옵트인, 기본 off): quota-scope의 임계치 경고가 자체 풍선 대신 notipet API를 POST하게. 설정 한 줄 + HttpClient 호출 한 번. **quota-scope에 notipet을 합치는 것과는 다른 얘기다** — 그건 하지 않는다, 이유는 [PROJECT_MAP.md](PROJECT_MAP.md)
- [ ] `sound.library` 관리 UI (파일 추가/미리듣기)
- [ ] 히스토리 영속화 옵션. 기본은 계속 메모리만 — 히스토리에는 프롬프트 텍스트가 들어갈 수 있다
- [ ] 다중 로그온 세션에서 데몬이 둘 뜨는 동작을 실제로 RDP로 확인 (세션별 `runtime-{id}.json`으로 완화해 뒀지만 검증 안 됨)
- [ ] `HttpListener`가 향후 투자 대상이 아니라는 점 — `INotipetHttpServer` 심 뒤에 있으니 필요해지면 한 파일 교체
