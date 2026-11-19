"""게임 데이터(data/items_game.json, data/slabs_game.json) → Plugin/companion/data.json.
두 입력은 플러그인 /items, /tablets 덤프(위키 스크래핑 대체, 2026-11-19).
v(식별자)는 게임 에셋 이름(예: 1188_HPSteal) — /inv 의 assetName 과 같아서 라이브 인벤토리와 바로 매칭된다.
"""
import json, re
from urllib.parse import quote

from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent
ITEMS_RAW = ROOT / "data" / "items_game.json"
SLABS_RAW = ROOT / "data" / "slabs_game.json"
OUT_PATH = ROOT / "Plugin" / "companion" / "data.json"

items = json.load(open(ITEMS_RAW, encoding="utf-8"))
artifacts = items["artifacts"]
weapons = items["weapons"]
slabs = json.load(open(SLABS_RAW, encoding="utf-8"))["tablets"]

# ---------- 키워드 사전 (v2: 기존 12종 + 신규 6종) ----------
KEYWORDS = {
    "basic_attack": ["일반 공격", "기본 공격"],
    "skill": ["스킬"],
    "special_attack": ["특수 공격"],
    "crit": ["치명타"],
    "charge_ammo": ["충전", "탄환"],
    "dash": ["대시"],
    "status": ["상태이상", "디버프", "중독", "출혈", "화상", "빙결", "감전"],
    "last_hit_chain": ["연격", "마지막 타", "마지막 공격", "연속 공격"],
    "guard": ["가드"],
    "summon": ["소환", "동료"],
    "mp": ["MP"],
    "shield": ["보호막"],
    # --- v2 신규 ---
    "ranged": ["투사체", "원거리"],
    "melee": ["근접"],
    "reload": ["재장전"],
    "elemental": ["속성 피해", "얼음속성", "번개속성", "화염속성", "먹구름", "빛속성", "암속성", "혼돈 속성"],
    "perfect_guard": ["퍼펙트 가드"],
    "lifesteal": ["흡혈"],
}

KW_KO = {
    "basic_attack": "일반공격", "skill": "스킬", "special_attack": "특수공격", "crit": "치명타",
    "charge_ammo": "탄환/충전", "dash": "대시", "status": "상태이상", "last_hit_chain": "연격/마지막",
    "guard": "가드", "summon": "소환/동료", "mp": "MP", "shield": "보호막",
    "ranged": "원거리/투사체", "melee": "근접", "reload": "재장전", "elemental": "속성피해",
    "perfect_guard": "퍼펙트가드", "lifesteal": "흡혈",
}

# 게임 ItemEntity.categories 키 → 한국어. 기존 placement_db.json 아티팩트 태그(한국어)와 이름으로 맞춰 다수결로 정함.
TAG_KO = {
    "STURDY": "견고", "EMBER": "잉걸불", "WINDSONG": "바람노래", "PRECISION": "정밀",
    "GLACIER": "빙하", "ACADEMY": "아카데미", "LAKE": "호수", "MAGITECH": "마법공학",
    "DARKCLOUD": "먹구름", "GUARDIAN": "수호", "SHADOW": "그림자", "COMPANION": "동료",
    "FLAMESWORD": "태양검", "FROST": "얼음무구", "MYSTIC": "신비", "PLANET": "행성",
    "ELEMENTAL": "원소", "CURSE": "저주", "ALCHEMY": "연금술", "SAVVY": "교섭",
}

# 개발용 자리표시자 이름(Item_..._Name, "...", "저주 아이템 이름")은 실제 아이템이 아님
PLACEHOLDER_NAME = re.compile(r"^(Item_.*|\.\.\.|.*아이템 이름)$")

# 어떤 문장에 키워드가 등장하고, 그 문장이 "그 키워드의 원래 동작을 바꾸거나/없애버린다"는
# 신호 단어도 같이 있으면 '재정의/제거(redef)'로 간주한다.
# v1은 "변경"/"대체"만 봤는데, "비활성화", "사용할 수 없"(더 이상 ~할 수 없다), "쓸 수 없",
# "사라집니다", "제거됩니다", "불가능" 같은 표현도 실제로는 "그 키워드에 의존하는 효과가
# 의도대로 안 터진다"는 같은 의미라서 누락하면 위험하다 (사용자가 지적한 "더 다양한 경우의 수").
REDEF_SIGNAL_WORDS = ["변경", "대체", "비활성화", "사용할 수 없", "쓸 수 없", "사라집니다", "제거됩니다", "불가능"]


def split_sentences(text):
    if not text:
        return []
    return [s.strip() for s in re.split(r"[.\n]", text) if s.strip()]


def extract_kw(text):
    hits = set()
    for sent in split_sentences(text):
        for kw, needles in KEYWORDS.items():
            if any(n in sent for n in needles):
                hits.add(kw)
    return sorted(hits)


def extract_redef(text):
    """이 텍스트가 어떤 키워드의 '원래 동작'을 바꾸거나 없애버리는지 문장 단위로 탐지."""
    hits = set()
    for sent in split_sentences(text):
        if not any(sig in sent for sig in REDEF_SIGNAL_WORDS):
            continue
        for kw, needles in KEYWORDS.items():
            if any(n in sent for n in needles):
                hits.add(kw)
    return sorted(hits)


def icon_url(asset_name):
    # 플러그인 /icon 라우트(게임 Sprite → PNG). 게임이 켜져 있어야 보인다. file:// 로 열면 페이지가 플러그인 주소를 앞에 붙여야 함.
    return "/icon?name=" + quote(asset_name)


def clean_text(t):
    # 게임 툴팁 문장의 Unity rich text 태그(<color=..>, <sprite=..>)를 걷어낸다.
    if not t:
        return t
    return re.sub(r"<[^>]+>", "", t).strip()


# ---------- 아티팩트 ----------
out_artifacts = []
for a in artifacts:
    if not a.get("name") or PLACEHOLDER_NAME.match(a["name"]):
        continue
    content = "\n".join(clean_text(e) for e in a.get("effects") or [])
    out_artifacts.append({
        "v": a["assetName"],
        "k": a["name"],
        "t": a["rarity"],
        "l": a.get("maxLevel", 0),
        "s": [c for c in a.get("categories") or [] if c],
        "c": content,
        "img": icon_url(a["assetName"]),
        "kw": extract_kw(content),
    })

# ---------- 무기 ----------
by_id = {w["id"]: w for w in weapons}
out_weapons = []
for w in weapons:
    if not w.get("name") or not w.get("enabled", True) or PLACEHOLDER_NAME.match(w["name"]):
        continue
    texts = [clean_text(e) for e in w.get("effects") or []]
    if w.get("uniqueEffectDescription"):
        texts.append(clean_text(w["uniqueEffectDescription"]))
    joined = " ".join(texts)
    parent = by_id.get(w.get("parentId"))
    out_weapons.append({
        "v": w["assetName"],
        "k": w["name"],
        "tier": w["tier"],
        "type": w.get("weaponType"),
        "parent": parent["assetName"] if parent else None,
        "c": joined,
        "img": icon_url(w["assetName"]),
        "kw": extract_kw(joined),
        "redef": extract_redef(joined),
    })

# ---------- 석판 (요약 목록, 효과 패턴은 placement_db.json 쪽에서 관리) ----------
out_slabs = []
for s in slabs:
    if not s.get("name") or PLACEHOLDER_NAME.match(s["name"]) or not (s.get("query") or "").strip():
        continue
    out_slabs.append({
        "v": s["assetName"],
        "k": s["name"],
        "t": s["rarity"],
        "r": bool(s.get("rotatable")),
        "img": icon_url(s["assetName"]),
    })

out = {
    "artifacts": out_artifacts,
    "slabs": out_slabs,
    "weapons": out_weapons,
    "tagKo": TAG_KO,
    "kwKo": KW_KO,
}

with open(OUT_PATH, "w", encoding="utf-8") as f:
    json.dump(out, f, ensure_ascii=False, separators=(",", ":"))

print(f"artifacts={len(out_artifacts)} weapons={len(out_weapons)} slabs={len(out_slabs)}")
redef_weapons = [w for w in out_weapons if w["redef"]]
print(f"weapons with redef (v2): {len(redef_weapons)}")
kw_artifacts = [a for a in out_artifacts if a["kw"]]
print(f"artifacts with kw: {len(kw_artifacts)}")

for w in redef_weapons[:8]:
    print(" ", w["k"], "->", w["redef"], "|", w["c"][:60])
