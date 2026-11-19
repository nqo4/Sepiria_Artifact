# SephiriaLive — 개발 재개 프롬프트 (로컬 Claude Code용)

> 이 문서를 로컬 Claude Code 세션 첫 메시지로 그대로 붙여넣어서 사용하세요.
> (원하면 프로젝트 루트에 `CLAUDE.md`로 저장해도 됩니다 — 그러면 세션 시작 시 자동으로 읽힙니다.)

---

## 프로젝트 개요

**SephiriaLive**는 스팀 게임 **Sephiria(세피리아, 앱 ID 2436940)**용 BepInEx 5.x 플러그인(모드) + 동반 로컬 웹페이지입니다.

이미 공개되어 있는 참고 프로젝트 **SephiriaSync**(사용자가 업로드해서 참고한 것)와 같은 카테고리지만, 차별점은 다음입니다:
- SephiriaSync는 인벤토리 미러링/원격 배치 적용 위주.
- SephiriaLive는 거기에 더해, **리그오브레전드 op.gg 데스크탑의 증강 추천 UI**에서 착안한 "현재 빌드 방향 + 선택지별 추천 등급" 기능을 추가합니다. 던전 입장 전 목표 무기/핵심 아이템을 입력하면, 선택지로 뜬 아이템/석판에 S~D 등급을 매기고, **무기가 특정 조건(일반공격/연격/탄환 등)을 재정의하거나 없애버리는지 따로 점검하는 "무기 호환성 점검" 단계**가 독립적으로 분리되어 있습니다. 석판 배치 최적화는 기존에 따로 공개했던 계산기의 엔진(`engine.js`, 시뮬레이션 어닐링)을 재사용합니다.

## ⚠️ 가장 중요한 전환점: 지금부터는 실게임 연동을 시작합니다

지금까지 클라우드 샌드박스에는 게임이 없어서 **"게임 없이 코드만 준비"** 원칙으로만 작업했습니다. `UnityStub/`는 `UnityEngine` 네임스페이스를 흉내만 낸 가짜 클래스이고, 리플렉션으로 찾는 필드명(`inventoryMatrix`, `levelMatrix`, `xIdx`, `stoneTablets` 등)은 전부 **추측**입니다. 실제 게임 메모리 구조와 다를 가능성이 꽤 있습니다.

로컬에는 실제 게임이 설치되어 있으니, 사용자가 명시적으로 **이제 실게임 연동/테스트를 시작**하기로 했습니다. 재개하면 가장 먼저 할 일:

1. **UnityStub를 실제 UnityEngine.dll로 교체.** `Plugin/SephiriaLive.Plugin.csproj`의 `<Reference Include="UnityEngine">` 항목이 지금은 `UnityStub/bin/.../UnityEngine.dll`을 가리킵니다. 이걸 사용자 게임 설치 폴더의 `Sephiria_Data/Managed/UnityEngine.dll` (및 필요시 `UnityEngine.CoreModule.dll`)로 바꿔서 다시 빌드해야 합니다.
2. **BepInEx/plugins/ 폴더에 설치**하고 게임을 실제로 실행.
3. `http://127.0.0.1:<포트>/debug` (포트 후보: 37788, 47022, 52612, 58123 — SephiriaSync가 쓰는 37777/47021/52611과 겹치지 않게 다른 대역을 씀)를 열어서 `ok:true`인지, `memberDump`에 그럴듯한 필드가 보이는지부터 확인.
4. `ReflectionFinder`가 찾은 필드명이 추측과 다르면, `Core/InventoryReader.cs`/`InventoryWriter.cs`의 필드 후보 이름 리스트를 실제 이름으로 교체.
5. `/inv`가 실제 인벤토리 모양과 비슷하게 나오는 걸 확인한 **다음에만** `/apply`(쓰기)를 시도. 세이브 파일 자체는 안 건드리지만, 잘못된 필드에 쓰면 그 실행 중 게임 상태가 이상해질 수 있음.
6. 위키에서 긁어온 석판 효과 패턴(아래 "알려진 데이터 한계" 참고)도 실제 게임에서 교차 검증.

**이 작업은 실제 게임 프로세스/세이브에 영향을 줄 수 있으니, 필드 추측이 틀렸을 때를 대비해 세이브 백업을 먼저 권장하세요.**

---

## 아키텍처 / 파일 구조

```
SephiriaLive/
├── Core/                       # 순수 로직 (유닛 테스트 가능, BepInEx/Unity 의존성 없음)
│   ├── ReflectionFinder.cs     # 방어적 리플렉션 유틸 — 절대 죽지 않고 로그만 남김
│   ├── MatrixExtractor.cs      # 2D배열/jagged배열/List<List<T>> 등 다양한 행렬 표현을 통일 추출
│   ├── InventoryReader.cs      # 인벤토리 스냅샷 빌드. LastFoundInstance에 마지막으로 찾은 인스턴스 캐싱
│   ├── InventoryWriter.cs      # 배치 커맨드(이동+회전) 적용
│   ├── MiniJson.cs             # 의존성 없는 자체 JSON Write/Parse
│   └── MiniHttpServer.cs       # 자체 raw-socket HTTP 서버 (netstandard2.0이라 외부 라이브러리 못 씀)
├── Plugin/
│   ├── SephiriaLivePlugin.cs   # BepInEx 진입점. 포트 바인딩, 라우트 등록, companion 페이지 서빙
│   ├── SephiriaLive.Plugin.csproj
│   └── companion/
│       ├── recommend_template.html   # ⭐ 편집은 항상 이 파일에서! (플레이스홀더 포함 원본)
│       ├── recommend.html            # 생성 결과물 (템플릿+data.json+placement_db.json+engine.js 조립됨) — 직접 수정 금지
│       ├── data.json                 # 아티팩트/무기/석판 요약 + 키워드/태그 번역 (전처리 스크립트로 생성)
│       ├── placement_db.json         # 배치 계산용 DB (아티팩트 조건, 석판 효과 패턴)
│       └── engine.js                 # 배치 최적화 엔진 (기존에 공개했던 계산기에서 그대로 재사용, 수정 안 함)
├── UnityStub/                  # UnityEngine 가짜 — 로컬 컴파일 통과용. 실게임에서는 실제 DLL로 교체.
├── libs/                       # BepInEx.dll, 0Harmony.dll (사용자가 올려준 실제 바이너리)
├── Tests/Program.cs            # 43개 테스트, 전부 가짜 객체로 Core 로직만 검증
└── data/                       # 위키에서 스크래핑한 원본 데이터
    ├── artifacts_sephiria_wiki.json      # 276개, sephiria.wiki/artifact
    ├── weapons_sephiria_wiki.json        # 161개, sephiria.wiki/weapon
    ├── slabs_sephiria_wiki.json          # 62개 요약, sephiria.wiki/large
    └── slabs_effectpattern_wiki.json     # 62개 석판의 실측 효과 패턴 (호버 툴팁 스크래핑)
```

**companion 페이지 수정 워크플로:**
1. `recommend_template.html` 편집 (HTML/CSS/JS 전부 이 파일에 있음, `__SEPHIRIA_DATA_JSON__` / `__SEPHIRIA_PLACEMENT_JSON__` / `__SEPHIRIA_ENGINE_JS__` 플레이스홀더만 빼고)
2. data.json을 바꿔야 하면 전처리 스크립트(아래 "재현 방법" 참고)로 재생성
3. 템플릿+3개 파일을 문자열 치환으로 합쳐서 `recommend.html` 재생성 (3줄짜리 파이썬 스크립트, 반복 작성했음 — `str.replace()` 3번)
4. Playwright(`/opt/pw-browsers/chromium-1194/chrome-linux/chrome` 또는 로컬 설치된 Chromium)로 로컬 `file://.../recommend.html` 열어서 동작 확인 — 콘솔 에러 체크 필수

---

## 오프라인 빌드 (네트워크 없이 BepInEx 플러그인 빌드하기)

`libs/BepInEx.dll`은 구버전 .NET Framework(3.5, mscorlib v2.0.0.0) 어셈블리라서, netstandard2.0 프로젝트에서 그대로 참조하면 CS0012가 하나씩 터집니다(mscorlib 추가 → System.Runtime 요구 → ... 끝없이). 해결책은 하나씩 땜질하지 않고, .NET SDK가 로컬에 이미 갖고 있는 `Microsoft.NETCore.App.Ref` 레퍼런스 어셈블리 팩 전체를 한 번에 참조에 넣는 것:

```xml
<PropertyGroup>
  <SephiriaRefPackDir>$(NetCoreTargetingPackRoot)/Microsoft.NETCore.App.Ref/$(BundledNETCoreAppPackageVersion)/ref/net8.0</SephiriaRefPackDir>
</PropertyGroup>
<ItemGroup Condition="'$(DisableImplicitFrameworkReferences)'=='true'">
  <ReferencePath Include="$(SephiriaRefPackDir)/*.dll" />
</ItemGroup>
```

이미 `Core.csproj`/`Plugin.csproj`에 적용되어 있습니다. 로컬에서도 `dotnet build`가 네트워크 없이 그대로 될 겁니다(순수 레퍼런스 어셈블리라 Private=false, 실행에는 안 쓰임).

---

## 코드 철학 (유지해야 할 것)

- **방어적 리플렉션:** `ReflectionFinder`/`UnityBridge`/`InventoryReader`/`InventoryWriter` 전부 "절대 크래시하지 않고 로그만 남긴다" 원칙. 실게임 연동 시작하면 이게 특히 중요해짐 — 필드 이름 추측이 틀려도 게임이 멈추면 안 됨.
- **Core는 BepInEx/Unity 의존성 0.** 그래야 `Tests/Program.cs`에서 가짜 객체로 유닛 테스트 가능. `Plugin.cs`는 얇게 유지(테스트 안 함, Unity 런타임 밖에서 로드 자체가 안 되니까).
- **데이터 신뢰도 투명하게 표기.** `placement_db.json`의 각 항목에 `confidence` 필드(`wiki_scraped` / `estimated`)가 있고, 애매한 부분은 `note`/`specialCells` 필드로 솔직하게 "모름"을 남겨둠. 새 데이터 추가할 때도 이 관례를 유지할 것 — 추측을 확정처럼 포장하지 않기.

---

## 알려진 데이터 한계 (실게임에서 교차검증 필요)

- **석판 "저주"**: `placement_db.json`에 있었던 기존 추정 항목인데, `sephiria.wiki/large`의 현재 62개 석판 목록에 이 이름이 없음. 이름이 바뀌었거나 아티팩트 태그("저주")와 혼동된 잘못된 항목일 가능성. `confidence: estimated`로 남겨둠 — 신뢰하지 말 것.
- **석판 "고양"(home_town), "이음"(connection)**: 호버 툴팁에 텍스트/아이콘 없는 보라색(`bg-purple-600`) 칸이 있는데 무슨 효과인지 위키만으론 알 수 없음. 레벨 계산에서 제외하고 `specialCells`로만 기록.
- **5×5 윈도우 한계**: 위키 툴팁은 석판 중심 기준 상하좌우 2칸(5×5)까지만 보여줌. 기존에 "보드 전체 행/열에 효과"로 추정했던 석판들(정의/기반/동시성/반항/차양/경계)이 실제로 더 큰 보드에서도 행/열 전체에 적용되는지는 확인 불가 — 안전하게 5×5 범위 내 패턴으로만 기록해둠. **실게임에서 큰 보드로 직접 확인 필요.**
- **"정의" 석판 오류 정정**: 기존 추정은 "보드 양쪽 가장자리 열"이었는데, 위키 실측으로는 "자기 자신이 있는 열 전체(자기 칸 제외)"가 맞음 — 이미 수정됨.
- **무기/아이템 호환성 점검은 텍스트 키워드 매칭 휴리스틱**이라 완벽하지 않음. `Plugin/companion/recommend_template.html`의 `KEYWORDS`/`REDEF_SIGNAL_WORDS`(실제로는 전처리 스크립트 쪽에 있음, 아래 참고)에 없는 새로운 패턴은 못 잡음.

---

## 데이터 재현 방법 (data.json / placement_db.json 재생성 스크립트 재작성 필요)

**주의: 이전 세션에서 쓴 전처리 파이썬 스크립트들(`prep_data_v2.py`, `build_slab_effectpattern.py`, `update_placement_db.py`, `assemble_recommend.py`)은 클라우드 샌드박스의 임시 스크래치 디렉터리에 있었고 세션이 끝나 사라졌습니다.** 로컬로 옮긴 리포에 이 파일들이 같이 와있지 않다면, 아래 설계를 참고해서 다시 작성해야 합니다 (원본 raw 데이터인 `data/*.json` 4개는 리포에 포함되어 있을 것이므로 재스크래핑은 필요 없음):

1. **`prep_data_v2.py`** — `data/artifacts_sephiria_wiki.json` + `data/weapons_sephiria_wiki.json` + `data/slabs_sephiria_wiki.json`를 읽어서 `Plugin/companion/data.json` 생성.
   - 키워드 사전(`KEYWORDS`, 18종): `basic_attack, skill, special_attack, crit, charge_ammo, dash, status, last_hit_chain, guard, summon, mp, shield, ranged, melee, reload, elemental, perfect_guard, lifesteal` — 각각 한국어 부분문자열 리스트로 매칭.
   - 재정의/제거 신호어(`REDEF_SIGNAL_WORDS`): `변경, 대체, 비활성화, 사용할 수 없, 쓸 수 없, 사라집니다, 제거됩니다, 불가능` — 문장 단위로 키워드와 신호어가 같이 나오면 그 무기의 `redef` 목록에 추가.
   - 출력 스키마: `artifacts: [{v,k,t,l,s,c,img,kw}]`, `weapons: [{v,k,tier,parent,c,img,kw,redef}]`, `slabs: [{v,k,t,r,img}]`, `tagKo`, `kwKo`.
2. **`build_slab_effectpattern.py`** — 호버 스크래핑 원본(`data/slabs_effectpattern_wiki.json`, 이미 있음)을 `placement_db.json` 스키마(`shape, rotatable, conditionType, confidence, effectMode:"pattern", effectPattern:[[dr,dc,level],...]`)로 변환.
3. **`assemble_recommend.py`** — `recommend_template.html`의 `__SEPHIRIA_DATA_JSON__`/`__SEPHIRIA_PLACEMENT_JSON__`/`__SEPHIRIA_ENGINE_JS__`를 각 파일 내용으로 3번 `str.replace()`해서 `recommend.html` 생성.

**Claude Code에게 요청할 것: 리포에 이 3개 스크립트가 없으면, 이 문서의 설계를 참고해서 `scripts/` 폴더 같은 곳에 다시 작성하고, `Plugin/companion/data.json` + `placement_db.json`이 지금 리포에 있는 것과 동일한 내용을 만들어내는지 diff로 확인해줘.**

---

## 지금까지 검증된 것 / 안 된 것

✅ 검증됨:
- `dotnet build` 오프라인으로 0 Warning/0 Error (Core, Plugin, UnityStub, Tests 전부)
- `Tests/Program.cs` 43개 테스트 전부 통과 (가짜 객체 기반, Unity/BepInEx 실제 런타임과는 무관)
- `recommend.html`을 Playwright로 로컬 실행 — 무기 선택→후보 호환성 점검(교만의 금관+화약초 연성 조합으로 충돌 정상 감지)→배치 계산까지 전체 흐름 콘솔 에러 없이 동작
- 위키 스크래핑 데이터(아티팩트 276, 무기 161, 석판 62) 전부 실제 사이트에서 직접 추출, 추측 아님

❌ 전혀 검증 안 됨 (로컬 재개 시 1순위):
- 실제 BepInEx 런타임에서 플러그인 로드 자체가 되는지
- `ReflectionFinder`가 실제 게임 타입에서 필드를 찾을 수 있는지 (필드명 전부 추측)
- `InventoryReader`/`InventoryWriter`가 실제 인벤토리 구조와 맞는지
- 로컬 HTTP 서버가 게임 프로세스 안에서 포트 바인딩/서빙이 실제로 되는지

---

## 재개 시 첫 할 일 체크리스트

1. [ ] 이 리포를 로컬 Claude Code 작업 디렉터리로 클론/복사
2. [ ] `dotnet build`로 전체 솔루션이 로컬에서도 빌드되는지 먼저 확인 (오프라인 레퍼런스 팩 경로가 로컬 .NET SDK 버전에 따라 조금 다를 수 있음)
3. [ ] `UnityStub` 참조를 실제 `Sephiria_Data/Managed/UnityEngine.dll`로 교체하고 재빌드
4. [ ] (세이브 백업 권장) BepInEx/plugins/에 설치 → 게임 실행 → `/debug` 확인
5. [ ] 리플렉션 필드명 실제 게임과 맞는지 검증, 틀리면 `Core/InventoryReader.cs`/`InventoryWriter.cs` 후보 이름 수정
6. [ ] `/inv` 스냅샷이 실제 인벤토리와 모양이 맞는지 확인
7. [ ] 확인되면 `/apply` 소규모 테스트 (이동 1건 정도부터)
8. [ ] 전처리 스크립트가 리포에 없으면 위 설계대로 재작성해서 `Plugin/companion/data.json`/`placement_db.json`이 그대로 재현되는지 확인
9. [ ] 석판 효과 패턴 중 "알려진 데이터 한계" 목록을 실게임에서 하나씩 교차검증 (특히 5×5 윈도우 밖 범위 여부)
