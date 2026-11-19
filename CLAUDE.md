# SephiriaLive — Claude Code 작업 메모

사용자와는 **한국어**로 소통한다.

## 프로젝트

스팀 게임 Sephiria(앱 ID 2436940)용 BepInEx 5.x 플러그인 + 동반 로컬 웹페이지.
참고 프로젝트 SephiriaSync(인벤토리 미러링/원격 배치)와 달리, op.gg 증강 추천 UI에서 착안한
"현재 빌드 방향 + 선택지별 S~D 추천 등급"과, 등급 계산과 분리된 "무기 호환성 점검"이 차별점.
배치 최적화는 기존 계산기의 `engine.js`(시뮬레이티드 어닐링)를 재사용할 예정.

- 저장소: https://github.com/nqo4/Sepiria_Artifact (공개) — **게임/BepInEx DLL은 절대 커밋 금지**
- 남은 작업과 데이터 불확실성: **`GAPS.md`** 를 먼저 볼 것
- 새 PC 환경 준비, 단계별 로드맵, 실게임 확인 절차: **`docs/DEVELOPMENT_GUIDE.md`**

## 구조

```
Directory.Build.props  게임 경로(SephiriaGameDir, 기본 Steam 경로)
Core/      netstandard2.0, BepInEx/Unity 의존성 0 — 반드시 유지(테스트 가능해야 함)
Plugin/    net472, BepInEx 진입점(얇게 유지) + companion/
Tests/     net8.0 콘솔, 자체 Check() 러너. 원본 스펙(상단) + 실게임 구조 흉내 테스트(하단)
data/      위키 스크래핑 원본
scripts/   전처리 (prep_data_v2 → data.json, assemble_recommend → recommend.html)
```

## 명령

```powershell
dotnet run --project Tests                            # 테스트 (전부 통과해야 커밋)
dotnet build Plugin -c Release                        # 게임 DLL 참조 빌드
dotnet build Plugin -c Release -p:DeployToGame=true   # BepInEx/plugins/SephiriaLive 로 복사
```

## 실제 게임 구조 (Assembly-CSharp.dll, 2026-11-19 확인)

- `GridInventory : Mirror.NetworkBehaviour` — `Width`(byte), `Height`(prop),
  `inventoryMatrix`/`levelMatrix`/`maxLevelMatrix`/`stoneTablets` 모두 `SyncDictionary<ItemPosition, T>`.
  쓰기 API: `Swap(sbyte xL, sbyte yL, sbyte xR, sbyte yR)` (서버면 LocalSwap, 클라면 CmdSwap).
- `StoneTablet` — `int rotation`, `bool isRotatable`, `sbyte xIdx, yIdx`, `string query`(효과 범위 정의), `Rotate()`(+1, 3→0).
- `ItemPosition { sbyte x, y }`, `NewItemOwnInstance { InstanceID, EntityID, Quantity, XIdx, YIdx, StoneTablet, Entity, Name }`.
- 게임 메타데이터 재조사: `BepInEx/core/Mono.Cecil.dll`을 스크래치로 복사 → `[Reflection.Assembly]::Load(bytes)` → `ModuleDefinition.ReadModule`.
  (게임 폴더에서 바로 Add-Type 하면 0x80131515로 막힘)
- 게임 환경: Unity 6000.3, BepInEx 5.4.23.2, Mirror 네트워킹(싱글도 호스트).

## 원칙

- 방어적 리플렉션: 절대 크래시하지 않고 에러를 기록. 필드명은 후보 배열로 관리.
- Unity API / Mirror 컬렉션 접근은 **메인 스레드에서만** (Plugin의 `RunOnMainThread`).
- 로컬 HTTP 서버는 외부 Origin 차단(CSRF 방지) 유지.
- 데이터 신뢰도는 `confidence`(`wiki_scraped`/`estimated`)로 표기, 추측은 `note`로 솔직하게.
- 실게임 쓰기(`/apply`)는 `/inv` 모양 확인 후 이동 1건부터.
