# SephiriaLive 개발 가이드

다른 PC(또는 새 Claude Code 세션)에서 이 프로젝트 개발을 이어가기 위한 안내서입니다.
마지막 갱신: 2026-11-19 (커밋 `b135f03` 기준)

---

## 1. 지금 상태 한눈에 보기

| 영역 | 상태 |
|---|---|
| `Core/` (JSON, HTTP 서버, 리플렉션, 인벤토리 읽기/쓰기) | ✅ 완성, 테스트 57개 통과, GitHub 자동 테스트 통과 |
| `Plugin/` (BepInEx 진입점) | ✅ 실제 게임 DLL로 빌드됨, 게임 폴더에 설치함 — **게임 안에서 실행 확인은 아직** |
| 실게임 연동 (`/health` → `/inv` → `/apply`) | ❌ 아직 시도 안 함 ← **다음 할 일** |
| 위키 데이터 (아티팩트 276·무기 161·석판 62) | ❌ 유실, 다시 가져와야 함 |
| 배치 엔진 `engine.js` | ❌ 유실, 다시 만들어야 함 |
| 추천 페이지 `recommend_template.html` | ⚠ 일부만 남음 (CSS·점수 계산 O, 상태/폴링/배치 렌더링 X) |

남은 작업과 데이터 불확실성의 상세 목록은 루트의 [`GAPS.md`](../GAPS.md)에 있습니다.

---

## 2. 새 PC 준비

### 2-1. 필요한 것

| 항목 | 확인 방법 | 비고 |
|---|---|---|
| Git | `git --version` | |
| GitHub CLI (선택) | `gh auth status` | 푸시/CI 확인용 |
| .NET 8 SDK | `dotnet --list-sdks` | 없으면 `winget install Microsoft.DotNet.SDK.8` |
| Python 3.10+ | `python --version` | 데이터 전처리 스크립트용 |
| Node.js (선택) | `node --version` | `engine.js` 단독 테스트용 |
| Sephiria (Steam) | 게임 폴더 존재 | Plugin 빌드에 필요. Core/Tests는 게임 없이 됨 |
| BepInEx 5.x | `게임폴더\BepInEx\core\BepInEx.dll` | 현재 PC는 5.4.23.2 |

> 설치 직후 Git Bash에서 `dotnet`을 못 찾으면 터미널을 다시 열거나 `export PATH="$PATH:/c/Program Files/dotnet"`.

### 2-2. 받기

```powershell
git clone https://github.com/nqo4/Sepiria_Artifact.git
cd Sepiria_Artifact
```

### 2-3. 게임 경로가 다를 때

기본값은 `C:\Program Files (x86)\Steam\steamapps\common\Sephiria` 입니다 (`Directory.Build.props`).
다르면 빌드할 때마다 지정하거나, 환경변수로 한 번만 설정합니다.

```powershell
[Environment]::SetEnvironmentVariable("SephiriaGameDir", "D:\SteamLibrary\steamapps\common\Sephiria", "User")
```

### 2-4. 잘 되는지 확인

```powershell
dotnet run --project Tests            # "=== 57 passed, 0 failed ===" 가 나와야 함
dotnet build Plugin -c Release        # "빌드했습니다." / 경고 0
```

---

## 3. 자주 쓰는 명령

```powershell
dotnet run --project Tests                                   # Core 테스트 (커밋 전 필수)
dotnet build SephiriaLive.sln -c Release                     # 전체 빌드
dotnet build Plugin -c Release -p:DeployToGame=true          # 빌드 + 게임에 설치
```

- 설치 위치: `게임폴더\BepInEx\plugins\SephiriaLive\` (`SephiriaLive.dll`, `SephiriaLive.Core.dll`, 있으면 `companion\recommend.html`)
- **게임이 켜져 있으면 DLL이 잠겨 설치가 실패합니다.** 게임을 끄고 설치하세요.
- 로그: `게임폴더\BepInEx\LogOutput.log` — `[SephiriaLive]` 로 검색.

---

## 4. 저장소 구조

```
Directory.Build.props   게임 경로 설정
SephiriaLive.sln
Core/                   netstandard2.0 · BepInEx/Unity 의존성 0 (반드시 유지)
  MiniJson.cs           JSON 쓰기/파싱 (JObj = Dictionary, 없는 키는 null)
  MiniHttpServer.cs     127.0.0.1 전용 HTTP 서버, 외부 Origin 차단
  ReflectionFinder.cs   방어적 리플렉션 (예외를 밖으로 안 던짐)
  MatrixExtractor.cs    2D배열/jagged/List<List>/좌표키 딕셔너리 → 행×열
  InventoryReader.cs    GridInventory 찾기 → JSON 스냅샷
  InventoryWriter.cs    배치 명령 적용 (game-api 모드 / field 모드)
Plugin/                 net472 · BepInEx 진입점 (얇게 유지)
  SephiriaLivePlugin.cs 포트 바인딩, 라우트, 메인 스레드 디스패치
  companion/            추천 페이지 템플릿 + placement_db.json
Tests/Program.cs        자체 Check() 러너. 상단=원본 스펙 43개, 하단=실게임 구조 흉내 14개
data/                   위키 스크래핑 원본
scripts/                전처리 파이썬 스크립트
docs/original/          클라우드에서 넘겨받은 원본 파일 (참고용, 빌드에 안 씀)
.github/workflows/      Core 테스트 CI (Plugin은 게임 DLL이 없어 CI에서 빌드 안 함)
```

---

## 5. 꼭 알아야 할 설계 원칙

1. **Core는 Unity/BepInEx를 참조하지 않는다.** Unity가 필요한 것(예: 씬에서 객체 찾기)은 Plugin이 델리게이트로 넣어준다 (`InventoryReader.InstanceFinder`).
2. **방어적 리플렉션.** 필드 이름이 틀려도 크래시하지 않고 `errors`에 기록한다. 이름은 후보 배열로 관리한다 (`InventoryReader.*Names`, `InventoryWriter.*Names`).
3. **게임 상태는 메인 스레드에서만 건드린다.** HTTP 요청은 백그라운드 스레드에서 오므로 `RunOnMainThread`로 `Update()`에 넘긴다 (타임아웃 3초 → 503).
4. **쓰기는 게임 API를 우선 쓴다.** `GridInventory.Swap()` / `StoneTablet.Rotate()`가 있으면 그것으로 처리하고(Mirror 동기화·권한 체크를 게임이 처리), 없을 때만 필드에 직접 쓴다.
5. **외부 웹사이트 요청 차단.** Origin 헤더가 로컬(`127.0.0.1:포트`, `localhost:포트`)이나 `null`(file://)이 아니면 403.
6. **데이터 신뢰도는 솔직하게.** `confidence: wiki_scraped | estimated`, 모르는 건 `note`로 남기고 추측을 확정처럼 쓰지 않는다.
7. **게임/BepInEx DLL은 절대 커밋하지 않는다.** 공개 저장소다. `.gitignore`가 `*.dll`을 막고 있다.

---

## 6. 실제 게임 구조 (Assembly-CSharp.dll에서 확인)

```
GridInventory : Mirror.NetworkBehaviour
  byte Width / byte Height(프로퍼티)
  SyncDictionary<ItemPosition, NewItemOwnInstance> inventoryMatrix
  SyncDictionary<ItemPosition, int> levelMatrix, maxLevelMatrix, disableMatrix, multiplyLevelMatrix, dungeonTempLevels
  SyncDictionary<ItemPosition, StoneTablet> stoneTablets
  void Swap(sbyte xL, sbyte yL, sbyte xR, sbyte yR)   // 서버면 LocalSwap, 클라면 CmdSwap
  bool AutoArrangeInventoryForBestCharmLevels(int maxIterations, bool allowTabletRotation)  // 게임 자체 자동배치!
ItemPosition { sbyte x, y }
NewItemOwnInstance { int InstanceID, EntityID; sbyte Quantity, XIdx, YIdx; StoneTablet StoneTablet; ItemEntity Entity; string Name }
StoneTablet : Mirror.NetworkBehaviour
  int rotation; bool isRotatable; sbyte xIdx, yIdx; int entityID, instanceID
  string query, conditionQuery        // 효과 범위 정의 문자열 (위키보다 정확할 가능성)
  void Rotate()                       // rotation +1, 3 다음 0. DungeonManager.IsTabletRotatable 검사
  string GetRotatedQuery(string, int) / List ParseQuery(...)
ItemEntity : ScriptableObject { int id; EItemType type; EItemRarity rarity; List categories; ... }
```

환경: Unity 6000.3 · BepInEx 5.4.23.2 · Mirror 네트워킹(싱글플레이도 호스트로 동작).

### 게임 코드를 다시 조사하는 방법

게임 업데이트로 이름이 바뀌었을 때 등. BepInEx에 들어 있는 Mono.Cecil을 씁니다 (게임 폴더에서 바로 `Add-Type` 하면 0x80131515로 막히므로 바이트로 읽어 로드).

```powershell
$G="C:\Program Files (x86)\Steam\steamapps\common\Sephiria"
$S="$env:TEMP\cecil"; New-Item -ItemType Directory -Force $S | Out-Null
Copy-Item "$G\BepInEx\core\Mono.Cecil.dll" $S -Force
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes("$S\Mono.Cecil.dll")) | Out-Null
$m=[Mono.Cecil.ModuleDefinition]::ReadModule("$G\Sephiria_Data\Managed\Assembly-CSharp.dll")
$t=$m.GetType('GridInventory')
$t.Fields | ForEach-Object { "$($_.FieldType.Name) $($_.Name)" }
$t.Methods | Where-Object Name -match 'Swap|Rotate' | ForEach-Object { $_.Name }
```

---

## 7. 다음 작업 로드맵 (순서대로)

### 1단계 — 실게임 첫 연동 확인 ← 지금 여기

세이브를 먼저 백업하세요.

1. 게임 실행 → 캐릭터로 마을이나 던전에 들어가기.
2. `LogOutput.log`에 `[SephiriaLive] 준비 완료 — http://127.0.0.1:37788/debug` 가 찍혔는지 확인.
   - 안 찍히면: 플러그인 로드 실패. 로그에서 `SephiriaLive` 관련 예외 확인 (netstandard 로드 문제 등).
3. `http://127.0.0.1:37788/health` → `ok:true`.
4. `/inv` → `items`가 실제 가방과 같은 위치·이름인지, `width`/`height`가 맞는지.
   - `instanceCount`가 2 이상이면 `chosenBy`로 어떤 기준으로 골랐는지 확인. 엉뚱한 인벤토리면 `InventoryReader.ChooseInstance` 수정.
   - `error: no_inventory_instance` → 메뉴 화면이라 인벤토리가 없는 것. 게임에 들어간 뒤 다시.
5. `/debug` → `memberDump`로 실제 필드 값 확인.
6. 모양이 맞으면 `/apply`로 **이동 1건**:
   ```powershell
   Invoke-RestMethod -Method Post -Uri http://127.0.0.1:37788/apply -ContentType 'application/json' -Body '{"placements":[{"fromX":0,"fromY":0,"toX":1,"toY":0}]}'
   ```
7. 회전 1건 (`"rotation":1` 추가) → 게임 화면에서 회전 방향 확인.
   - 지금은 엔진 회전 r → 게임 rotation `(4 - r) % 4` 로 가정. 반대면 `InventoryWriter.EngineToGameRotation` 수정 + 테스트 갱신.
8. 회전 후 아티팩트 레벨이 자동으로 다시 계산되는지 확인. 안 되면 재계산을 일으키는 메서드를 찾아야 함 (`StoneTablet.ApplyEffect/RemoveEffect`, `GridInventory` 의 OnItemUpdated 계열 등 후보).

확인된 사실은 `GAPS.md`와 `CLAUDE.md`의 "실제 게임 구조"에 반영.

**알려진 위험**
- 게임이 새 Input System만 쓰면 F10 단축키가 동작하지 않음 (로그에 경고 1번 남기고 꺼짐 — 정상).
- 클라이언트로 남의 방에 들어간 경우 `Swap`이 `CmdSwap`(서버 요청)이 되어 결과가 늦게 반영될 수 있음. 싱글(호스트)에서 먼저 테스트.

### 2단계 — 석판 효과를 게임에서 직접 읽기 (위키보다 정확)

- `StoneTablet.query` / `GetRotatedQuery` / `ParseQuery` 를 리플렉션으로 호출해서 석판별 실제 효과 범위를 뽑는 `/tablets` 디버그 라우트 추가.
- 결과를 `data/slabs_effectpattern_wiki.json`과 비교 → 특히 GAPS.md의 의심 항목(분배·헌정·배수진·방어수·기적의 회전 여부, 5×5 밖 범위 석판, 고양·이음 보라색 칸).
- 맞으면 `placement_db.json`을 게임 데이터 기반으로 다시 생성하고 `confidence: game_data` 같은 새 등급 부여.

### 3단계 — 위키 데이터 다시 가져오기

- 출처: `https://www.sephiria.wiki/artifact`, `/weapon`, `/large`.
- 방법: Next.js RSC flight 페이로드 — 페이지 HTML의 `self.__next_f.push([1,"..."])` 조각들을 이어 붙여 JSON 배열 부분을 파싱.
- 저장: `data/artifacts_sephiria_wiki.json`, `weapons_sephiria_wiki.json`, `slabs_sephiria_wiki.json`.
- 스키마는 `scripts/prep_data_v2.py`가 읽는 필드에 맞출 것:
  - 아티팩트: `value, label_kor, tier, level, effect.content, effect.sets, image`
  - 무기: `value, value_kor, tier, parent, effects.reward[], image`
  - 석판: `value, ko_label, tier, rotate, image`
- 그다음 `python scripts/prep_data_v2.py` → `Plugin/companion/data.json` 생성.
- 스크래퍼 스크립트도 `scripts/`에 커밋 (다음에 다시 유실되지 않게).
- 대안: 게임의 `ItemEntity` ScriptableObject를 플러그인에서 읽어 덤프하는 방식이 더 정확할 수 있음 (2단계와 같은 방식).

### 4단계 — 배치 엔진 `engine.js`

- 시뮬레이티드 어닐링으로 석판 위치·회전과 아티팩트 위치를 바꿔가며 총 레벨 점수를 최대화.
- 입력: `/inv` 스냅샷 + `placement_db.json` (석판 `effectPattern: [[dr, dc, level], ...]`, `rotatable`).
- 출력: `/apply` 형식 `{"placements":[{fromX,fromY,toX,toY,rotation?}]}`.
- 회전 시 패턴 좌표 변환 `(dr, dc) → (dc, -dr)` (시계 방향 1회) — 게임 회전 방향과 1단계 결과를 맞출 것.
- Node로 단독 테스트할 수 있게 순수 함수로 작성 (`module.exports`와 브라우저 전역 둘 다 지원).
- 참고: 게임 자체 `AutoArrangeInventoryForBestCharmLevels`가 있으니, 결과 비교용 디버그 라우트를 만들어 두면 엔진 검증에 유용.

### 5단계 — 추천 페이지 완성

- `Plugin/companion/recommend_template.html`에서 빠진 것:
  - 전역 상태 선언: `weapon, anchors, candidates, basket, pinnedTags, demoMode, candidateKind`
  - `TIER_RANK`, `ARTIFACTS/SLABS/WEAPONS/TAG_KO/KW_KO` 를 `DATA`에서 꺼내는 부분
  - `/inv` 1초 폴링 (포트 후보 4개 순서대로 시도, 실패 시 데모 모드)
  - `runPlacement`, `renderPlacementGrid`, `renderBasket`, "적용" 버튼 → `/apply`
- 조립: `python scripts/assemble_recommend.py` → `recommend.html` (생성물, 커밋 안 함)
- 확인: 브라우저로 `file://.../recommend.html` 열고 콘솔 에러 0, 그다음 게임 켜고 `http://127.0.0.1:37788/`.

### 그 밖에

- `scripts/build_slab_effectpattern.py`, `update_placement_db.py`는 유실된 `placement_db_OLD.json`을 입력으로 받아서 지금은 실행 불가. 2단계를 하면 필요 없어짐.
- 자동 스캔 루프(1초마다 스냅샷 캐시)는 아직 없음 — `/inv` 요청 때마다 메인 스레드에서 새로 읽음.

---

## 8. 작업 규칙

- 커밋 전 `dotnet run --project Tests` 전부 통과.
- Core를 고치면 테스트를 같이 추가. 실게임 구조를 흉내 내는 가짜 클래스는 `Tests/Program.cs` 하단에 있음 (`FakeGameInventory`, `FakeStoneTablet`, `FakePos`).
- 실게임 쓰기 동작을 바꿨다면, 게임에서 이동 1건 → 회전 1건 순서로 다시 확인.
- 실게임에서 새로 확인한 사실은 `CLAUDE.md`(구조)와 `GAPS.md`(남은 일)에 바로 반영.
- 커밋 메시지는 한국어, 무엇을 왜 바꿨는지.

---

## 9. 새 Claude Code 세션 시작용 프롬프트

다른 PC에서 Claude Code를 열고 아래를 붙여 넣으면 됩니다 (`CLAUDE.md`는 자동으로 읽힘).

```
docs/DEVELOPMENT_GUIDE.md 와 GAPS.md 를 읽고, 2장 "새 PC 준비"대로 환경을 확인해줘.
테스트가 통과하면 7장 로드맵의 다음 단계부터 이어서 진행해줘.
게임에 쓰는 작업(/apply, 플러그인 설치)은 하기 전에 나한테 먼저 물어봐줘.
```
