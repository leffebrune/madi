# 다중 탭 구현 진행

완료 조건: 계획서 A01–A24, 관련 Release 테스트, 단일 EXE 게시, 실제 WinUI 확인, ARCHITECTURE.md 갱신. **진행 중이며 완료로 판정하지 않음.**

## 단계 0 — 기준선 (2026-09-12)

- 저장소/상위 경로 및 숨김 경로를 검사했으며 적용할 AGENTS.md 없음. 기존 사용자 변경은 추적되지 않은 `docs/` 계획서이며 보존한다.
- .NET SDK 10.0.401. 기존 3열 MainPage, 단일 MadiController/VM 실행 상태, 즉시 Dispose 종료 경로 확인.
- 기본 sandbox의 NuGet 복원이 NU1301(네트워크 제한)로 실패. 권한 적용 후 App Release 테스트 **37/37 통과**. 나머지 기준선 검사 진행 중.
- 구현 선택: 프로젝트 서비스는 한 번 구성하고, 공통 coordinator가 탭 선택 예약과 세션별 실행 Task를 소유한다. 기존 명령/대화 표현을 재사용하며 UI 캐시는 점유 근거로 사용하지 않는다.

## 단계 1 — 탭 컨텍스트와 점유 계약

- `SessionOwnershipRegistry.cs`, `SessionOwnershipRegistryTests.cs`: 원자적 선택 예약/확정, 탐색 세대, 실행 토큰, Work 자식 전체 삭제 보호, 닫기 중 예약/실행 배제 구현.
- App Release **43/43 통과** (기존 37 + 점유 6). 동시 선택의 단일 승자, 실패/늦은 탐색 롤백, 실행 중 비선택 점유 유지, 오래된 해제, 삭제 보호, 정리 대기 검증.

## 단계 2–4 — 실행/화면/공통 데이터 통합 (진행 중)

- `WorkspaceCoordinator`, `SessionRuntime`, `MadiController.Context`: 서비스 공유 컨텍스트, 세션별 Task/CTS 및 SessionId/RunId/순서 이벤트 기록, 소유 탭 이동, 활성 포인터 단일 쓰기, Work 충돌 검사, 삭제/종료 조정 추가. 통합 시나리오 테스트는 아직 작성/검증 중.
- `WorkspaceTabViewModel`, `MainViewModel.Workspace`: 세션별 초안/표시 캐시, 비활성 화면 이벤트 기록 후 재연결, 탭별 Work 편집기, 점유 안내 연결.
- `WorkspaceTabPage.xaml(.cs)`에 기존 3열 콘텐츠를 재사용하고 `MainPage`에 WinUI TabView 호스트 추가. `MainWindow` 비동기 종료와 재진입 방지 연결.
- `dotnet build src/Madi.GUI/Madi.GUI.csproj -c Release --nologo`: **성공, 경고 0/오류 0**. 실제 창 검증과 게시는 별도 미완료.
- Presentation 변경 중 기존 Work 버튼 Busy 회귀 1개 발견. 레거시 VM과 탭 VM의 활성 조건을 구분해 수정했으며 재검증 예정.
- `FileWorkSessionStore.Turns.cs`: 기존 JSONL+메타데이터 형식을 유지한 턴 커밋/복구 마커, 실패 시 두 파일 롤백, 정확한 재시도의 중복 방지, 손상된 꼬리의 명확한 오류.
- `Agent.cs`: 턴의 모델/Work 지침 고정, 취소 시 실제 하위 실행 Task 관찰 추가.
- Runtime Release **75/75 통과** (기존 69 + 저장 경계 6). append 전/도중/후 및 metadata 후 실패, append 도중 취소, 재시작 복구, 무표식 손상 오류 검증.
- Services 기준선 Release **9/9 통과**. Presentation 기준선 **18/18 통과**.

## 미완료 검증

- A01–A24 전체, Runtime/GUI/Services 기준선 및 변경 후 테스트, Release 게시, 실제 WinUI 확인, 최종 구조 문서.

## 2026-09-13 재개 및 실제 GUI 검사

- 이전 실행에서 App 통합 **49/49**, Presentation **21/21**, Runtime **77/77** 통과. 실제 Agent의 병렬 자식 Task 정리 대기 및 도구 라운드 간 설정 고정 포함.
- `build-gui.ps1 -Configuration Release`로 `artifacts/publish/madi-gui/release/win-x64/Madi.GUI.exe` 단일 파일 게시 성공 (첫 게시 220,817,200 bytes).
- `tests/gui-smoke-server.py` 추가: `127.0.0.1`만 사용하는 제어 가능한 Responses 스트림과 격리 MADI_HOME/프로젝트를 생성. 사용자 저장소/인증을 사용하지 않는다.
- 실제 게시 EXE에서 탭 1의 3열, Ctrl+Enter 전송, A 실행 중 탭 2 생성/Work·모델 상속, 백그라운드 완료의 미확인 표시 확인.
- GUI에서 빈 탭 콘텐츠가 세로 전체를 채우지 않는 문제 및 단독 초기 delta가 다음 이벤트까지 버퍼에 머무르는 문제 발견. TabView/TabViewItem의 콘텐츠 늘림, 40ms PeriodicTimer 배출, 목록 선택 바인딩을 수정. **수정 후 실제 창 재검증 중**.
- 희소 delta를 다음 provider 이벤트 전에 표시하는 회귀 테스트 추가 후 App **50/50 통과**.
- 전체 완료 판정은 아직 아님. 남은 GUI 인수 절차, 추가 경계 점검, 최종 Release/문서 정리가 필요하다.

## 체크포인트 1 — 핵심 구현 및 실행/점유 검증 (2026-09-13)

- Release 테스트 App **53/53**, Presentation **22/22**, Runtime **77/77**, Services **9/9**: 합계 **161/161 통과**. 같은 탭의 두 세션 병렬 실행, 로딩 취소, 종료 포인터, 공유 OAuth 로그아웃 차단/상태 전파, 저장 중 추가 편집 보존을 보강했다.
- 단일 EXE Release 재게시 성공. 실제 WinUI에서 A-final/B-final 동시 시작, A 실행 중 C 탐색, A 선택 및 새 탭 열기 거절, 목록 선택 복구, 소유 탭 이동, A 초안/자유 스크롤 복원, B만 취소 확인.
- A-final/B-success 성공 응답의 세션별 JSONL을 검사해 각각 정확히 한 번 저장됨을 확인. B-final 취소 응답은 저장하지 않았고, 백그라운드 A 완료 후 활성 포인터는 B 유지.
- 실행 중 탭 닫기 취소 시 B-close가 계속 진행되고, 닫기 확정 뒤에만 서버 연결 취소 및 탭 제거됨을 확인.
- 증거: `artifacts/gui-smoke-final-20260913/evidence/`의 parallel, duplicate-rejected, draft-scroll-restored, cancel-b-only, close-confirmation PNG 및 같은 디렉터리의 `requests.jsonl`.
- 모델/추론 선택 표시 및 2행 헤더 개선 완료. **약 920px 좁은 창에서 Work 우측이 잘리는 문제 발견; 최종 수정/재검증 필요.** 탭 넘침, 프로젝트 변경/종료의 최종 GUI 확인과 ARCHITECTURE.md 갱신도 남아 있다.
- 사용자 지시에 따라 이 지점을 중간 커밋으로 보존한다. 목표 완료로 선언하지 않는다.
