import json, re

from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent
ARTIFACTS_RAW = ROOT / "data" / "artifacts_sephiria_wiki.json"
WEAPONS_RAW = ROOT / "data" / "weapons_sephiria_wiki.json"
SLABS_RAW = ROOT / "data" / "slabs_sephiria_wiki.json"
OUT_PATH = ROOT / "Plugin" / "companion" / "data.json"

artifacts = json.load(open(ARTIFACTS_RAW, encoding="utf-8"))
weapons = json.load(open(WEAPONS_RAW, encoding="utf-8"))
slabs = json.load(open(SLABS_RAW, encoding="utf-8"))

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

TAG_KO = {
    "firmness": "견고", "yinggalbul": "잉걸불", "spring_song": "봄의 노래", "precision": "정밀",
    "glacier": "빙하", "academy": "마법공학", "lake": "호수", "extrium": "엑스트리움",
    "magic_engineering": "마법공학2", "guardian": "수호", "shadow": "그림자", "colleague": "동료",
    "sun_sword": "태양검", "ice_weapon": "얼음무기", "mystery": "신비", "planet": "행성",
    "element": "원소", "curse": "저주", "alchemy": "연금술", "bargaining": "거래",
}

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


def clean_text(t):
    # 위키가 $$x...$ 형태의 강조 마크업을 쓰는데(ex: $$s일반 공격$), 표시용으로는 걷어낸다.
    if not t:
        return t
    t = re.sub(r"\$\$?([a-zA-Z]?)", "", t)
    t = t.replace("$", "")
    return t.strip()


# ---------- 아티팩트 ----------
out_artifacts = []
for a in artifacts:
    content = a.get("effect", {}).get("content", "") or ""
    kw = extract_kw(content)
    out_artifacts.append({
        "v": a["value"],
        "k": a["label_kor"],
        "t": a["tier"],
        "l": a.get("level", 0),
        "s": a.get("effect", {}).get("sets", []) or [],
        "c": clean_text(content),
        "img": a["image"],
        "kw": kw,
    })

# ---------- 무기 ----------
out_weapons = []
for w in weapons:
    reward = w.get("effects", {}).get("reward", []) or []
    texts = [r for r in reward if isinstance(r, str) and r.strip()]
    joined = " ".join(texts)
    kw = extract_kw(joined)
    redef = extract_redef(joined)
    out_weapons.append({
        "v": w["value"],
        "k": w["value_kor"],
        "tier": w["tier"],
        "parent": w.get("parent"),
        "c": clean_text(joined),
        "img": w["image"],
        "kw": kw,
        "redef": redef,
    })

# ---------- 석판 (요약 목록 그대로, 효과 패턴은 placement_db.json 쪽에서 따로 관리) ----------
out_slabs = []
for s in slabs:
    out_slabs.append({
        "v": s["value"],
        "k": s["ko_label"],
        "t": s["tier"],
        "r": bool(s.get("rotate", False)),
        "img": s["image"],
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
