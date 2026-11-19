"""data/slabs_game.json(게임 /tablets 덤프) → Plugin/companion/placement_db.json 의 slabs 재생성.

- effectPattern [[dr, dc, level]]: 석판 기준 고정 칸(UP/DOWN/LEFT/RIGHT 반복, DIA*, KNIGHT*, O) 중 숫자 값.
- specialCells  [[dr, dc, value]]: 같은 고정 칸 중 숫자가 아닌 값(X=비활성, IGNORECRITERIA=조건 무시, MUL/2=배수).
- boardEffects  [{keyword, value}]: 보드 크기/위치에 따라 칸이 달라지는 키워드(HORIZONTAL, TOP, CHECKERBOARD 등).
  칸 계산은 엔진이 보드 크기를 알고 해야 한다. 의미는 게임 StoneTablet.ParseQuery 기준.
- 회전은 회전 0 기준. 게임 rotation r=1 은 UP→LEFT(화면 반시계). 엔진 시계 방향 1 = 게임 3.
고정 칸 해석은 게임 ParseQuery 결과(slabs_game.json 의 cells)와 일치하는지 스스로 검사한다.
사용: python scripts/build_placement_from_game.py
"""
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAME = ROOT / "data" / "slabs_game.json"
DB = ROOT / "Plugin" / "companion" / "placement_db.json"

STEP = {"UP": (-1, 0), "DOWN": (1, 0), "LEFT": (0, -1), "RIGHT": (0, 1)}  # (dr, dc)
FIXED = {
    "O": (0, 0),
    "DIAUPLEFT": (-1, -1), "DIAUPRIGHT": (-1, 1), "DIADOWNLEFT": (1, -1), "DIADOWNRIGHT": (1, 1),
    # 적재(UP/DIAUPLEFT/KNIGHTUPLEFT/UPUP)의 실게임 적용 칸으로 확인: KNIGHTUPLEFT = 위 2, 왼쪽 1
    "KNIGHTUPLEFT": (-2, -1), "KNIGHTUPRIGHT": (-2, 1), "KNIGHTDOWNLEFT": (2, -1), "KNIGHTDOWNRIGHT": (2, 1),
}


def fixed_offset(kw):
    if kw in FIXED:
        return FIXED[kw]
    m = re.fullmatch(r"(UP|DOWN|LEFT|RIGHT)\1{0,3}", kw)
    if m:
        dr, dc = STEP[m.group(1)]
        n = len(kw) // len(m.group(1))
        return dr * n, dc * n
    return None


def num(v):
    try:
        return int(v.lstrip("+"))
    except ValueError:
        return None


def build(t):
    pattern, special, board = [], [], []
    for line in (t["query"] or "").split("\n"):
        parts = line.split()
        if not parts:
            continue
        kw, value = parts[0], " ".join(parts[1:])
        off = fixed_offset(kw)
        if off is None:
            board.append({"keyword": kw, "value": num(value) if num(value) is not None else value})
        elif num(value) is not None:
            pattern.append([off[0], off[1], num(value)])
        else:
            special.append([off[0], off[1], value])

    # 자체 검사: 보드 키워드가 없으면 고정 칸 = 게임 ParseQuery 칸이어야 한다
    if not board:
        mine = sorted([(p[0], p[1], str(p[2])) for p in pattern] + [(s[0], s[1], s[2]) for s in special])
        game = sorted((c["dy"], c["dx"], str(num(c["value"])) if num(c["value"]) is not None else c["value"]) for c in t["cells"])
        assert mine == game, f"{t['name']}: 해석 불일치\n mine={mine}\n game={game}"

    cond = (t["conditionQuery"] or "").strip()
    entry = {
        "id": t["name"],
        "name": t["name"],
        "entityId": t["entityId"],
        "assetName": t["assetName"],
        "rarity": t["rarity"],
        "shape": [[0, 0]],  # 게임 석판은 한 칸(ItemPosition 하나)을 차지
        "rotatable": t["rotatable"],
        "conditionType": "none" if not cond else "placed:" + ",".join(l.split()[0] for l in cond.split("\n") if l.strip()),
        "confidence": "game_data",
        "effectMode": "pattern",
        "effectPattern": pattern,
        "query": t["query"],
        "source": "game StoneTablet.query + ParseQuery (2026-11-19)",
    }
    if special:
        entry["specialCells"] = special
    if board:
        entry["boardEffects"] = board
    if cond:
        entry["conditionQuery"] = cond
    return entry


def main():
    game = json.loads(GAME.read_text(encoding="utf-8"))["tablets"]
    db = json.loads(DB.read_text(encoding="utf-8"))
    usable = [t for t in game if t["name"] and t["name"] != "..." and (t["query"] or "").strip()]
    skipped = [t["assetName"] for t in game if t not in usable]
    db["slabs"] = [build(t) for t in sorted(usable, key=lambda t: t["entityId"])]
    db["_slabMeta"] = {
        "source": "게임 데이터 (SephiriaLive /tablets: StoneTablet 프리팹 query + StoneTablet.ParseQuery)",
        "capturedAt": "2026-11-19",
        "rawData": "data/slabs_game.json",
        "skipped": skipped,
        "notes": [
            "effectPattern 은 회전 0 기준 [dr, dc, level]. 게임 rotation r 은 반시계 r×90°(엔진 시계 방향 1 = 게임 3).",
            "boardEffects 는 보드 크기에 따라 칸이 달라지는 효과(HORIZONTAL=가로줄 전체, TOP/BOTTOM=맨 위/아래 줄, CHECKERBOARD 등) — 엔진이 보드 기준으로 계산해야 함.",
            "specialCells 값: X=비활성, IGNORECRITERIA=조건 무시, MUL/n=레벨 배수.",
        ],
    }
    DB.write_text(json.dumps(db, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(f"slabs {len(db['slabs'])}개 기록, 제외 {skipped}")


if __name__ == "__main__":
    main()
