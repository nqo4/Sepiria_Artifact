import json

from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent
NEW_SLAB_PATH = ROOT / "data" / "slabs_effectpattern_wiki.json"
# ⚠ placement_db_OLD.json 은 샌드박스 리셋 때 유실됨 — GAPS.md 참고
OLD_DB_PATH = ROOT / "data" / "placement_db_OLD.json"
OUT_PATH = ROOT / "Plugin" / "companion" / "placement_db.json"

new_slabs = json.load(open(NEW_SLAB_PATH, encoding="utf-8"))
db = json.load(open(OLD_DB_PATH, encoding="utf-8"))

old_slab_by_id = {s["id"]: s for s in db["slabs"]}
new_by_ko = {s["ko_label"]: s for s in new_slabs}

MACRO_MODES_OLD = {"edge-columns-full", "row-except-self", "col-except-self", "diagonal-through-anchor", "col-bottom-each", "top-bottom-rows-full"}

built = []
unmatched_old = []
for ko, old in old_slab_by_id.items():
    ns = new_by_ko.get(ko)
    if ns is None:
        unmatched_old.append(ko)
        old = dict(old)
        old["confidence"] = "estimated"
        old["note"] = (f"'{ko}' 은(는) 2026-10-09 기준 https://www.sephiria.wiki/large 에 나열된 62개 석판 목록에 없음. "
                        f"이름이 바뀌었거나, 게임이 업데이트되어 제거되었거나, 애초에 잘못 추정된 항목일 수 있음 "
                        f"(참고: '저주'는 아티팩트 태그로도 쓰이는 이름이라 혼동되었을 가능성 있음). 실제 게임에서 직접 확인 전까지는 신뢰하지 말 것.")
        built.append(old)
        continue

    pattern = []
    special_cells = []
    for c in ns["effectCells"]:
        if c["kind"] == "level" and c["level"] is not None:
            pattern.append([c["dr"], c["dc"], c["level"]])
        else:
            special_cells.append({"dr": c["dr"], "dc": c["dc"], "rawColor": c["rawColor"], "rawText": c["rawText"]})

    had_macro = old.get("effectMode") in MACRO_MODES_OLD
    note = None
    if had_macro:
        note = (f"기존(estimated) 데이터는 effectMode='{old.get('effectMode')}'(보드 전체 행/열에 걸치는 효과)로 추정했으나, "
                f"위키 툴팁은 슬랏 중심 기준 5x5 범위까지만 보여주므로 이 범위를 벗어나는 보드에서도 실제로 "
                f"행/열 전체에 효과가 적용되는지는 이 스크래핑만으로 확인할 수 없음. 안전하게 5x5 범위 내 패턴으로만 기록함. "
                f"실제 게임에서 더 큰 보드로 재확인 필요.")

    entry = {
        "id": old["id"],
        "name": old["name"],
        "shape": old.get("shape", [[0, 0]]),
        "rotatable": ns["rotatable"],
        "conditionType": "확인필요(보라색 특수효과: 의미 불명, 수동 확인 필요)" if special_cells else "none",
        "confidence": "wiki_scraped",
        "effectMode": "pattern",
        "effectPattern": pattern,
    }
    if special_cells:
        entry["specialCells"] = special_cells
    if note:
        entry["note"] = note
    entry["source"] = ns["source"]
    built.append(entry)

print("unmatched_old (no scrape found, old guess kept):", unmatched_old)
print("built count:", len(built))

db["slabs"] = built
db["_slabMeta"] = {
    "scrapedFrom": "https://www.sephiria.wiki/large",
    "scrapedAt": "2026-10-09",
    "method": "hover tooltip DOM scraping (5x5 effect-pattern grid), see data/slabs_effectpattern_wiki.json for raw per-slab data",
    "limitations": [
        "툴팁이 보여주는 범위는 슬랏 중심 기준 5x5(상하좌우 2칸)로 보임. 이보다 넓은 보드에서 행/열 전체에 걸치는 효과가 있는지는 확인 불가.",
        "보라색(purple-600) 표시 셀은 텍스트/아이콘이 전혀 없어 어떤 효과인지 위키만으로는 알 수 없음 (고양/home_town, 이음/connection 2종). 수동 확인 필요.",
    ],
}

with open(OUT_PATH, "w", encoding="utf-8") as f:
    json.dump(db, f, ensure_ascii=False, separators=(",", ":"))

print("wrote", OUT_PATH)
