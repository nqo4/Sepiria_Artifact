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
    # 상태이상은 종류별로 따로(같은 종류만 무기 지원). "debuff" 는 모든 디버프에 통하는 효과("디버프 걸린 적에게").
    "status_burn": ["화상"],
    "status_poison": ["중독"],
    "status_bleed": ["출혈"],
    "status_frostbite": ["동상"],
    "status_freeze": ["빙결"],
    "status_shock": ["감전"],
    "status_wound": ["상처 악화"],
    "debuff": ["상태이상", "디버프"],
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
    # 무기 종류에 따라 못 쓰는 동작(GradeCalculator 가 감점): "대시" 중에서도 대시 공격만 — 대시 횟수 등은 모든 무기가 씀
    "dash_attack": ["대시 공격"],
}

KW_KO = {
    "basic_attack": "일반공격", "skill": "스킬", "special_attack": "특수공격", "crit": "치명타",
    "charge_ammo": "탄환/충전", "dash": "대시",
    "status_burn": "화상", "status_poison": "중독", "status_bleed": "출혈", "status_frostbite": "동상",
    "status_freeze": "빙결", "status_shock": "감전", "status_wound": "상처악화", "debuff": "디버프", "last_hit_chain": "연격/마지막",
    "guard": "가드", "summon": "소환/동료", "mp": "MP", "shield": "보호막",
    "ranged": "원거리/투사체", "melee": "근접", "reload": "재장전", "elemental": "속성피해",
    "perfect_guard": "퍼펙트가드", "lifesteal": "흡혈", "dash_attack": "대시공격",
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

# 무기가 아이템의 의존 동작을 "없애는" 경우만 충돌(redef)로 본다. 형태만 바뀌는 건(일반 공격이 3연타/화약탄으로 변경) 동작이 그대로라 충돌 아님.
#  1) 없앰: "비활성화", "사용할 수 없", "쓸 수 없" 등 — 그 구절(쉼표·"지만"으로 나눈 조각)에 나온 키워드만
#  2) 다른 종류로 바뀜: "A가 B로 변경"에서 B가 다른 공격 종류일 때(특수 공격: 강베기가 일반 공격: 거합베기로, 퓨리가 마법서: 복제로)
#     A에만 있는 공격 종류가 충돌. B에 종류가 없으면(3연타, '고급 재장전') 형태만 바뀐 것.
REMOVAL_WORDS = ["비활성화", "사용할 수 없", "쓸 수 없", "사라집니다", "제거됩니다", "불가능"]
ACTION_KW = ["basic_attack", "special_attack", "dash_attack"]
CONVERT = re.compile(r"^(.*?)(?:이|가) (.*?)(?:으로|로) (?:변경|대체)")


def split_sentences(text):
    if not text:
        return []
    return [s.strip() for s in re.split(r"[.\n]", text) if s.strip()]


# 바늘이 더 긴 다른 키워드의 일부일 때는 세지 않는다: "대시 공격"(dash_attack) 안의 "대시"는 대시 자체(dash)가 아님
# "화상 디버프"처럼 종류를 밝힌 디버프는 그 종류(status_burn)만 — 일반 디버프로 세지 않는다
NEEDLE_EXCLUDE = {"dash": ["대시 공격"], "debuff": [w + " 디버프" for w in ["화상", "중독", "출혈", "동상", "빙결", "감전", "상처 악화"]]}


def has_kw(sent, kw, needles):
    for ex in NEEDLE_EXCLUDE.get(kw, []):
        sent = sent.replace(ex, "")
    return any(n in sent for n in needles)


def extract_kw(text):
    hits = set()
    for sent in split_sentences(text):
        for kw, needles in KEYWORDS.items():
            if has_kw(sent, kw, needles):
                hits.add(kw)
    return sorted(hits)


def clauses(sent):
    # 마침표 없이 이어진 문장("…감소합니다 특수 공격이…")도 나눈다
    return [c.strip() for c in re.split(r",|지만|(?<=니다)\s", sent) if c.strip()]


def action_kws(part):
    found = {k for k in ACTION_KW if has_kw(part, k, KEYWORDS[k])}
    if "마법서" in part:
        found.add("magic_book")
    return found


def extract_redef(text):
    """무기 문장에서 아이템이 의존하는 동작을 없애는 키워드(위 규칙)."""
    hits = set()
    for sent in split_sentences(text):
        for c in clauses(sent):
            if any(w in c for w in REMOVAL_WORDS):
                hits.update(kw for kw, needles in KEYWORDS.items() if has_kw(c, kw, needles))
                continue
            m = CONVERT.search(c)
            if m:
                before, after = action_kws(m.group(1)), action_kws(m.group(2))
                if after:
                    hits.update(k for k in before - after if k in KEYWORDS)
    return sorted(hits)


assert extract_redef("일반 공격, 대시 공격이 3연타로 변경됩니다.") == []
assert extract_redef("더 이상 대시 공격을 쓸 수 없지만, 일반 공격으로 주는 피해가 20% 증가합니다.") == ["dash_attack"]
assert extract_redef("특수 공격: 강베기가 일반 공격: 거합베기로 변경됩니다.") == ["special_attack"]
assert extract_redef("특수 공격이 '고급 재장전'으로 변경됩니다.") == []
assert extract_redef("대시 공격이 비활성화되고, 일반 공격이 대시 공격 판정으로 변경됩니다.") == ["basic_attack", "dash_attack"]
assert extract_redef("일반 공격의 피해량이 45% 감소합니다 특수 공격이 '고급 재장전'으로 변경됩니다.") == []
assert extract_redef("무기 공격의 피해량이 20% 감소하지만, 화염속성 피해 기반으로 변경됩니다.") == []


def icon_url(asset_name):
    # 플러그인 /icon 라우트(게임 Sprite → PNG). 게임이 켜져 있어야 보인다. file:// 로 열면 페이지가 플러그인 주소를 앞에 붙여야 함.
    return "/icon?name=" + quote(asset_name)


KEYWORD_SPRITE = re.compile(r'<sprite="?Keyword"? name="?([A-Za-z0-9_]+)"?>')


COLOR_OPEN = re.compile(r'<color="?#?([0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?|red)"?>')
NAMED_COLOR = {"red": "FF0000"}


def clean_text(t):
    # 게임 툴팁 문장의 키워드 아이콘 <sprite="Keyword" name=MP> 는 {kw:MP} 로 남기고(페이지가 /keyword-icon 그림으로 바꿈),
    # 키워드 글자색 <color=#RRGGBB(AA)>..</color> 는 {c:RRGGBB}..{/c} 로 남기고(게임과 같은 색), 나머지 rich text 태그는 걷어낸다.
    if not t:
        return t
    t = KEYWORD_SPRITE.sub(lambda m: "{kw:" + m.group(1) + "}", t)
    t = COLOR_OPEN.sub(lambda m: "{c:" + NAMED_COLOR.get(m.group(1).lower(), m.group(1)[:6].upper()) + "}", t)
    t = t.replace("</color>", "{/c}")
    return re.sub(r"<[^>]+>", "", t).strip()


def plain(t):
    # 키워드 분석용: 아이콘 자리 표시까지 뺀 순수 문장
    return re.sub(r"\{kw:[A-Za-z0-9_]+\}|\{c:[0-9A-F]{6}\}|\{/c\}", "", t or "")


# 주요 피해 방식: 효과 문장의 키워드 아이콘 이름 → 피해 방식. 빌드 방향의 "주요 피해 방식" 선택과 맞으면 등급 점수가 오른다.
DAMAGE_TYPES = {
    "physical": ["PhysicalDamage"],
    "fire": ["FlameGround", "FlameSword", "BlueBurn"],
    "lightning": ["LightningDamage", "DarkCloud", "Plasma"],
    "ice": ["IceDamage", "FrostRelic", "DarkCloudIce"],
    "chaos": ["ChaosDamage"],
    "magic": ["MagicBook", "MagicDamageBonus", "CooldownRecovey", "MagicCriticalChance"],
    "companion": ["Companion", "FollowerDamage", "FollowerHP", "FollowerCritical", "FollowerDefense"],
    "true": ["TrueDamage"],
}
DAMAGE_KO = {
    "physical": "물리", "fire": "화염", "lightning": "번개(감전)", "ice": "얼음(빙결)",
    "chaos": "혼돈", "magic": "마법서", "companion": "동료", "true": "고정 피해",
}
_SPRITE_TO_DT = {sp: dt for dt, sps in DAMAGE_TYPES.items() for sp in sps}
_SPRITE_TO_DT_MULTI = {"HighestElementalDamage": ["fire", "lightning", "ice"]}  # 가장 높은 속성 피해 = 어느 속성이든


# 주요 스탯: 효과 문장의 키워드 아이콘 이름 → 스탯. 빌드 방향의 "주요 스탯" 선택과 맞으면 등급 점수가 오른다.
STATS = {
    "attack_speed": ["AttackSpeed", "SpecialAttackSpeed"],
    "crit": ["CriticalChance", "CriticalDamageRate", "WeaponCriticalChance", "MagicCriticalChance"],
    "defense": ["Defense"],
    "evasion": ["Evasion"],
    "move_speed": ["MoveSpeed"],
    "hp": ["HP", "HPRegen", "HPSteal"],
    "mp": ["MP", "MPRegen", "MPSteal"],
    "dash": ["DashRecovery"],
}
STAT_KO = {
    "attack_speed": "공격 속도", "crit": "치명타", "defense": "방어력", "evasion": "회피",
    "move_speed": "이동 속도", "hp": "최대 HP", "mp": "최대 MP", "dash": "대시 회복",
}
_SPRITE_TO_STAT = {sp: st for st, sps in STATS.items() for sp in sps}


def stats_of(raw_texts):
    return sorted({_SPRITE_TO_STAT[n] for t in raw_texts for n in KEYWORD_SPRITE.findall(t or "") if n in _SPRITE_TO_STAT})


def damage_types(raw_texts):
    hits = set()
    for t in raw_texts:
        for name in KEYWORD_SPRITE.findall(t or ""):
            if name in _SPRITE_TO_DT:
                hits.add(_SPRITE_TO_DT[name])
            hits.update(_SPRITE_TO_DT_MULTI.get(name, []))
    return sorted(hits)


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
        "kw": extract_kw(plain(content)),
        "dt": damage_types(a.get("effects") or []),  # 피해 방식(DAMAGE_TYPES)
        "st": stats_of(a.get("effects") or []),       # 주요 스탯(STATS)
        "cr": a.get("criteria"),          # 발동 조건(CharmActivateCriteria_XXX 의 XXX), 없으면 None
        "mb": bool(a.get("magicBook")),   # 마법서(Charm_Magic) — Near8MagicBook 조건이 봄
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
        "kw": extract_kw(plain(joined)),
        "redef": extract_redef(plain(joined)),
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
    "dmgKo": DAMAGE_KO,
    "statKo": STAT_KO,
    # 게임 안에서 데이터에 없는 문장(기적 효과 등)을 같은 규칙으로 분석하도록 플러그인에 넘기는 규칙(GradeCalculator.TextFeatures)
    "rules": {
        "kw": KEYWORDS,
        "kwExclude": NEEDLE_EXCLUDE,
        "dtSprites": {**{sp: [dt] for sp, dt in _SPRITE_TO_DT.items()}, **_SPRITE_TO_DT_MULTI},
        "stSprites": _SPRITE_TO_STAT,
    },
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
