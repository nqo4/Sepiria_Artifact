# 남은 작업 / 복구 안 된 부분

클라우드 샌드박스 리셋으로 유실된 부분 중, 2026-11-19 로컬 재개 시점 기준으로 아직 남은 것들.

## ✅ 2026-11-19 해결

- `Core/*.cs` 6종(ReflectionFinder, MatrixExtractor, InventoryReader, InventoryWriter, MiniJson, MiniHttpServer) 재작성 — `Tests/Program.cs` 원본 스펙 43개 + 실게임 구조 테스트 14개 통과.
- `UnityStub/` 불필요 — 로컬 게임 폴더의 실제 `UnityEngine*.dll`, `BepInEx.dll`을 직접 참조하도록 변경 (`Directory.Build.props`의 `SephiriaGameDir`).
- `libs/BepInEx.dll`, `libs/0Harmony.dll` 불필요 — 게임의 `BepInEx/core`를 직접 참조. 0Harmony는 현재 코드에서 쓰지 않아 참조 제거.
- 실제 게임 타입 이름 확인 (Assembly-CSharp.dll 메타데이터, Mono.Cecil): `GridInventory`, `StoneTablet`, `ItemPosition`, `NewItemOwnInstance`. 자세한 필드는 `Core/InventoryReader.cs` 상단 주석.
  - 매트릭스는 2D 배열이 아니라 `Mirror.SyncDictionary<ItemPosition, T>` → `MatrixExtractor`에 `posDict` 형태 추가.
  - 쓰기는 필드 직접 수정 대신 게임 API `GridInventory.Swap(sbyte×4)` / `GridInventory.DoClickAction(ItemPosition)` 사용 (Mirror 동기화·권한 체크를 게임이 처리).
- **실게임 첫 연동 검증 완료** — `/health`·`/inv`·`/apply` 이동·회전 모두 화면 반영 확인. 상세는 `CLAUDE.md` "실게임 검증 결과".
  - 회전 변환 `(4 - r) % 4` 맞음(엔진 1 = 시계 방향).
  - `StoneTablet.Rotate()` 단독은 레벨 재계산이 안 됨 → `DoClickAction`(Permission으로 감싼 게임 클릭 경로)으로 변경해 해결.
- **석판 효과를 게임에서 직접 읽기 완료** — `/tablets` 라우트(`Core/TabletReader.cs`). 석판 71개의 `query`와 게임 `ParseQuery` 칸 → `data/slabs_game.json`.
  - `Plugin/companion/placement_db.json`의 slabs를 게임 데이터로 재생성(`scripts/build_placement_from_game.py`, 68개, `confidence: game_data`).
  - 위키 대비: 45개 일치, 11개는 위키가 5×5까지만 기록(HORIZONTAL/VERTICAL/대각선 줄/UPUPUP — 큰 보드에서도 줄 전체 적용),
    차양·경계는 보드 맨 위/아래 줄 기준(TOP/BOTTOM), 고양·이음·환대의 보라색 칸 = `IGNORECRITERIA`(조건 무시), **착취는 위키가 90° 돌아가 있었음**.
  - 회전 가능 여부: 기적/헌정/분배/배수진/방어수 모두 게임 프리팹 `isRotatable = true`(위키 false는 오류). 던전이 석판별로 덮어쓸 수 있음(`DungeonManager.overrideTabletRotatable`).
  - 게임에만 있는 석판: 억압, 생장, 반향, 지옥, 천국, 저주(+내부용 3개 제외). 석판은 모두 한 칸 차지(`shape: [[0,0]]`).
  - `placement_db_OLD.json` 유실 문제 해소 — 구 스크립트(`build_slab_effectpattern.py`, `update_placement_db.py`)는 더 이상 필요 없음.

- **아티팩트·무기 데이터를 게임에서 직접 읽기 완료 (3단계)** — `/items` 라우트(`Core/ItemReader.cs`) → `data/items_game.json`
  (아티팩트 354, 무기 195). `scripts/prep_data_v2.py`가 위키 대신 게임 데이터로 `Plugin/companion/data.json` 생성(아티팩트 346·무기 190·석판 68).
  - 효과 문장: 아티팩트 `Charm_Basic.BuildEffectString`(최대 레벨 툴팁), 무기 `WeaponSimple.addons[].GetEffectText()`.
  - 무기 티어/상위 무기: `WeaponEntity.standardEnhancements`(부모→자식)로 계산(`enhanceFromId`는 실게임에서 -1).
  - 식별자 `v` = 게임 에셋 이름(`1188_HPSteal`) — `/inv`의 `assetName`과 같아서 라이브 인벤토리와 바로 매칭.
  - 태그 키가 게임 키(`STURDY`, `EMBER` …)로 바뀜. 한국어는 `TAG_KO`(기존 DB 태그와 다수결로 맞춤).
  - 이미지: `/icon?name=<에셋 이름>` 라우트가 게임 아이콘(Sprite)을 PNG로 변환해 보냄. `data.json`의 `img`가 이 주소(604개 중 601개 존재, 특수 무기 3개 없음).

- **배치 엔진 `Plugin/companion/engine.js` 완료 (4단계)** — `parseQuery`(게임 ParseQuery 재현), `evaluate`(게임 ApplyEffect/ReleasePermission 규칙), `optimize`(시뮬레이티드 어닐링, `/apply` 형식 출력), `boardFromSnapshot`.
  - 검증 `node scripts/test_engine.js`(CI 포함): 게임 `/tablets/fixtures` 결과(6×5·7×6 보드 × 회전 4 × 모든 칸, 39,168건)와 칸 단위 일치,
    실게임 스냅샷 levelMatrix 일치(`data/engine_fixtures/inv_*.json`).

- **추천 페이지 완성 (5단계)** — `Plugin/companion/recommend_template.html` 재작성(`python scripts/assemble_recommend.py` → `recommend.html`, 빌드 시 플러그인 폴더로 복사).
  - 게임 연결(같은 출처 → 후보 포트 4개, 2초 폴링), 데모 모드(실게임 스냅샷과 같은 가방), 게임 등급 기준 `TIER_RANK`, 게임 아이콘(`/icon`),
    바구니(가방에 없는 아이템은 가상 배치), 배치 계산(`SephEngine.optimize` 시드 3개 중 최고 + 점수 무관 이동 되돌리기), 결과 그리드, "게임에 적용"(`/apply`).
  - 실게임 확인(2026-11-19): 페이지에서 적용 → 방어수 (2,0)→(2,1) 이동, 베루트의 낫 Lv 0→2 — 엔진 예측과 일치.

## ❌ 아직 남음 (우선순위 순)

1. **엔진 결과를 게임 자동 배치와 비교** — 게임 `AutoArrangeInventoryForBestCharmLevels(maxIterations, allowTabletRotation)` 결과와 점수 비교(가방에 석판·아티팩트가 많을 때).
2. **엔진 점수 개선** — 지금은 레벨 합만 최대화. 아티팩트별 레벨 가치, 아티팩트 자체 발동 조건(`IGNORECRITERIA`와 연결), 전역 보너스·각인 미반영.
3. `placement_db.json`의 `artifacts`(267개, 한국어 태그·conditionType)는 예전 데이터이고 지금 페이지·엔진은 쓰지 않음 — 정리(삭제 또는 `data/items_game.json` 기준 재생성) 결정 필요.
4. 페이지를 `file://`로 열 때는 아이콘이 연결된 플러그인 주소를 앞에 붙여 보임. 게임이 꺼져 있으면 아이콘 없음(이름만).

## ⚠ 데이터 확인 필요

- 아티팩트 4개는 효과 문장이 비어 있고, 5개 문장에는 `{DAMAGE}` 같은 자리표시자가 남음(`BuildEffectString` 대신 원문). 자리표시자 이름이 `Item_..._Name`인 개발용 항목은 `data.json`에서 제외.
- 다른 사람 방(클라이언트)에서의 `CmdSwap`/`CmdDoClickAction` 동작은 미확인.
- `mysticPositions`(GridInventory의 SyncList<ItemPosition>)의 의미 — 미확인.
- 보드 키워드 중 `X_PLUS/X_MINUS/Y_PLUS/Y_MINUS`, `KNIGHT*_INVERT`, `IDX/RIDX`는 현재 석판 목록에서 거의/전혀 안 쓰임. 쓰이면 ParseQuery로 확인.
- 엔진이 아직 단순화한 것: 조건 칸 종류(AnyItem/OnlyCharm)를 구분하지 않음(현재 석판은 PLACED만 사용), `IGNORECRITERIA`는 기록만 하고 점수에 반영 안 함(아티팩트 자체 발동 조건 미구현),
  레벨 계산의 전역 보너스(`DungeonManager.GetGlobalItemStatValue`)·각인(`engravings`, `fixedEngravingsOnServer`) 미반영, `IDX/RIDX/X_PLUS` 등 미사용 키워드 미구현.
- 최대 레벨을 넘는 레벨은 점수에서 잘라냄(clamp). 아티팩트별 효과 가치(레벨 1과 3의 차이)는 반영 안 함 — 지금은 레벨 합만 최대화.
