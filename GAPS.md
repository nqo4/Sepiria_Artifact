# 남은 작업 / 복구 안 된 부분

클라우드 샌드박스 리셋으로 유실된 부분 중, 2026-11-19 로컬 재개 시점 기준으로 아직 남은 것들.

## ✅ 2026-11-19 해결

- `Core/*.cs` 6종(ReflectionFinder, MatrixExtractor, InventoryReader, InventoryWriter, MiniJson, MiniHttpServer) 재작성 — `Tests/Program.cs` 원본 스펙 43개 + 실게임 구조 테스트 14개 통과.
- `UnityStub/` 불필요 — 로컬 게임 폴더의 실제 `UnityEngine*.dll`, `BepInEx.dll`을 직접 참조하도록 변경 (`Directory.Build.props`의 `SephiriaGameDir`).
- `libs/BepInEx.dll`, `libs/0Harmony.dll` 불필요 — 게임의 `BepInEx/core`를 직접 참조. 0Harmony는 현재 코드에서 쓰지 않아 참조 제거.
- 실제 게임 타입 이름 확인 (Assembly-CSharp.dll 메타데이터, Mono.Cecil): `GridInventory`, `StoneTablet`, `ItemPosition`, `NewItemOwnInstance`. 자세한 필드는 `Core/InventoryReader.cs` 상단 주석.
  - 매트릭스는 2D 배열이 아니라 `Mirror.SyncDictionary<ItemPosition, T>` → `MatrixExtractor`에 `posDict` 형태 추가.
  - 쓰기는 필드 직접 수정 대신 게임 API `GridInventory.Swap(sbyte×4)` / `StoneTablet.Rotate()` 사용 (Mirror 동기화·권한 체크를 게임이 처리).

## ❌ 아직 남음 (우선순위 순)

1. **실게임 첫 연동 검증** — 플러그인 설치 → `/health` → `/debug` → `/inv` 모양 확인 → `/apply` 이동 1건.
   - 회전 방향 변환 `(4 - r) % 4` 는 엔진/게임 회전 방향 가정. 실게임에서 확인 필요.
   - `Rotate()` 후 아티팩트 레벨 재계산이 자동으로 되는지 확인 필요(안 되면 재계산 트리거를 찾아야 함).
   - 게임에 `AutoArrangeInventoryForBestCharmLevels(maxIterations, allowTabletRotation)` 가 이미 있음 — 참고/비교 대상.
2. **위키 원본 데이터 재스크래핑** — `data/artifacts_sephiria_wiki.json`(276), `weapons_sephiria_wiki.json`(161), `slabs_sephiria_wiki.json`(62).
   출처 `https://www.sephiria.wiki/artifact`, `/weapon`, `/large` — Next.js RSC flight 페이로드(`self.__next_f.push`) 추출 방식.
   이게 있어야 `scripts/prep_data_v2.py` → `Plugin/companion/data.json` 생성 가능.
3. **`Plugin/companion/engine.js`** (시뮬레이티드 어닐링 배치 엔진) 재작성.
4. **`recommend_template.html` 누락 부분** — 전역 상태 선언(`weapon, anchors, candidates, basket, pinnedTags, demoMode, candidateKind`), 라이브 폴링(`/inv`), `runPlacement`, `renderPlacementGrid`, `renderBasket`.
5. **`placement_db_OLD.json` 유실** — `scripts/build_slab_effectpattern.py`, `update_placement_db.py` 는 이 파일을 입력으로 받으므로 그대로는 실행 불가. 현재 `Plugin/companion/placement_db.json` 이 최종 산출물로 남아 있음.

## ⚠ 데이터 확인 필요

- 분배/헌정/배수진/방어수/기적 5개 석판의 회전 가능 여부가 이전 검증 결과와 다름(재타이핑 오타 의심) → `sephiria.wiki/large` 재확인.
  실게임의 `StoneTablet.isRotatable` 값을 `/inv`의 `rotatable`로 바로 볼 수 있으니 그걸로 교차검증하는 게 가장 확실.
- 석판 "저주": 위키 62개 목록에 없음. "고양"(home_town), "이음"(connection): 보라색 특수 칸, 의미 불명.
- 5×5 윈도우 밖 효과(정의/기반/동시성/반항/차양/경계)가 큰 보드에서도 적용되는지 미확인.
  실게임 `StoneTablet.query` / `GetRotatedQuery` / `ParseQuery` 에 효과 범위 정의가 문자열로 있음 — 위키 대신 이걸 읽으면 정확한 패턴을 얻을 수 있을 가능성.
