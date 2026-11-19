import json

from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent
RAW_PATH = ROOT / "data" / "raw_slab_tooltips.json"
# ⚠ placement_db_OLD.json 은 샌드박스 리셋 때 유실됨 — GAPS.md 참고
OLD_DB_PATH = ROOT / "data" / "placement_db_OLD.json"
OUT_PATH = ROOT / "data" / "slabs_effectpattern_wiki.json"

raw = json.load(open(RAW_PATH, encoding="utf-8"))
old_db = json.load(open(OLD_DB_PATH, encoding="utf-8"))
old_slabs = old_db["slabs"]
old_by_name = {s["name"]: s for s in old_slabs}

# imgKey(영문 value)는 raw 자체에 이미 있음 (ko_label<->value 매핑을 slabs_sephiria_wiki.json 없이도 해결).
# value 표기가 하이픈/언더스코어로 갈리는 경우(home-town vs home_town)는 old_db의 한글 이름으로 매칭한다.
out = []
for r in raw:
    value = r["imgKey"].replace("-", "_")
    center = r["centerIdx"]
    cells = []
    for idx, text, color in r["notable"]:
        row, col = divmod(idx, 5)
        crow, ccol = divmod(center, 5) if center >= 0 else (2, 2)
        dr, dc = row - crow, col - ccol
        if color in ("green500", "red500"):
            level = int(text) if text else None
            kind = "level"
        elif color == "purple600":
            level, kind = None, "special"
        else:
            level, kind = None, f"unknown:{color}"
        cells.append({"dr": dr, "dc": dc, "level": level, "kind": kind, "rawText": text, "rawColor": color})
    old = old_by_name.get(r["name"], {})
    out.append({
        "value": value,
        "ko_label": r["name"],
        "rotatable": r["rotatable"],
        "oldRotateFlag": old.get("rotatable", None),
        "centerIdx": center,
        "effectCells": cells,
        "source": "sephiria.wiki/large tooltip (scraped 2026-10-09)",
        "confidence": "wiki_scraped",
    })

with open(OUT_PATH, "w", encoding="utf-8") as f:
    json.dump(out, f, ensure_ascii=False, indent=2)

print(f"wrote {len(out)} entries to {OUT_PATH}")
mismatches = [o for o in out if o["oldRotateFlag"] is not None and o["rotatable"] != o["oldRotateFlag"]]
print(f"rotate-flag mismatches vs old data: {len(mismatches)}")
for m in mismatches:
    print(" ", m["value"], "scraped=", m["rotatable"], "old=", m["oldRotateFlag"])
