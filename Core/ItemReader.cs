using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SephiriaLive.Core
{
    /// <summary>
    /// 게임의 아이템 정의(아티팩트·무기)를 읽어 JSON으로 만든다(순수 조회). 위키 스크래핑 대체용.
    ///
    /// 실제 게임 구조 (2026-11-19, Assembly-CSharp.dll):
    ///   ItemEntity : ScriptableObject { int id; EItemType type(Charm=아티팩트); EItemRarity rarity; List&lt;string&gt; categories;
    ///                                  GameObject resourcePrefab; string Name(현지화); string Context(설명문) }
    ///   Charm_Basic (resourcePrefab 컴포넌트) { int maxLevel; virtual BuildEffectString(...) / GetEffectStringCount() / GetEffectString(...) }
    ///   WeaponEntity : ScriptableObject { int id; bool enabled; string Name; bool isDefaultWeapon; GameObject mainWeaponPrefab;
    ///                                  List&lt;EnhancementMetadata { bool enabled; WeaponEntity enhanced }&gt; standardEnhancements(다음 강화 무기) }
    ///     (enhanceFromId 는 실게임에서 -1 이라 쓰지 않음)
    ///   WeaponSimple (mainWeaponPrefab) { EWeaponType weaponType; WeaponAddon[] addons } — WeaponAddon.GetEffectText() = 툴팁 강화 효과 문장
    ///   WeaponBasic (mainWeaponPrefab) { bool hasUniqueEffect; LocalizedString uniqueEffectName, uniqueEffectDescription }
    /// Unity 객체 탐색/컴포넌트 조회는 플러그인이 하고, 여기서는 찾은 객체만 읽는다.
    /// </summary>
    public static class ItemReader
    {
        public static JObj DescribeCharm(object entity, object charm)
        {
            var o = Entity(entity);
            o["categories"] = ReflectionFinder.EnumerateValues(ReflectionFinder.GetMember(entity, "categories")).Select(c => (object)c?.ToString()).ToList();
            o["flavor"] = Str(ReflectionFinder.GetMember(entity, "Context"));
            if (charm == null) return o;

            int maxLevel = ReadInt(charm, "maxLevel") ?? 0;
            o["maxLevel"] = maxLevel;
            // 발동 조건(CharmActivateCriteria_XXX → "XXX", 없으면 null)과 마법서 여부(Near8MagicBook 조건이 보는 Charm_Magic)
            var criteria = ReflectionFinder.GetMember(charm, "criteria");
            o["criteria"] = criteria?.GetType().Name.Replace("CharmActivateCriteria_", "");
            o["magicBook"] = IsOrInherits(charm.GetType(), "Charm_Magic");
            // 1순위: 게임 툴팁과 같은 BuildEffectString(avatar=null, bullet, endl, level, offset, showAllLevel, ignoreAvatarStatus) — {MP} 같은 값이 채워짐.
            var build = ReflectionFinder.FindMethod(charm.GetType(), "BuildEffectString", 7);
            string berr = null;
            if (build != null && ReflectionFinder.TryInvoke(charm, build, new object[] { null, "", "\n", maxLevel, 0, true, true }, out var built, out berr)
                && !string.IsNullOrEmpty(built as string))
            {
                o["effects"] = ((string)built).Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).Cast<object>().ToList();
                return o;
            }
            // 2순위: 서브클래스가 오버라이드하는 GetEffectStringCount()/GetEffectString() — 자리표시자({MP})가 남을 수 있음.
            var count = ReflectionFinder.FindMethod(charm.GetType(), "GetEffectStringCount", 0);
            var get = ReflectionFinder.FindMethod(charm.GetType(), "GetEffectString", 4);
            var effects = new List<object>();
            if (count != null && get != null && ReflectionFinder.TryInvoke(charm, count, new object[0], out var n, out _) && n is int effectCount)
                for (int i = 0; i < effectCount; i++)
                    if (ReflectionFinder.TryInvoke(charm, get, new object[] { i, maxLevel, 0, true }, out var s, out _)) effects.Add(Str(s));
            o["effects"] = effects;
            o["effectsRaw"] = true;
            if (berr != null) o["buildError"] = berr;
            return o;
        }

        /// <param name="weaponSimple">mainWeaponPrefab 의 WeaponSimple(강화 효과 = addons[].GetEffectText(), 게임 무기 툴팁과 같은 경로). 없으면 null.</param>
        /// <param name="weaponBasic">mainWeaponPrefab 의 WeaponBasic(고유 효과). 없으면 null.</param>
        public static JObj DescribeWeapon(object weaponEntity, object weaponSimple, object weaponBasic)
        {
            var o = new JObj
            {
                ["id"] = ReadInt(weaponEntity, "id"),
                ["name"] = Str(ReflectionFinder.GetMember(weaponEntity, "Name")),
                ["assetName"] = Str(ReflectionFinder.GetMember(weaponEntity, "name")),
                ["enabled"] = ReflectionFinder.GetMember(weaponEntity, "enabled"),
                ["isDefault"] = ReflectionFinder.GetMember(weaponEntity, "isDefaultWeapon"),
                // standardEnhancements: 이 무기에서 강화할 수 있는 다음 무기들(EnhancementMetadata { bool enabled; WeaponEntity enhanced })
                ["enhancesTo"] = ReflectionFinder.EnumerateValues(ReflectionFinder.GetMember(weaponEntity, "standardEnhancements"))
                    .Select(e => ReflectionFinder.GetMember(e, "enhanced"))
                    .Select(t => t == null ? null : ReadInt(t, "id")).Where(id => id.HasValue).Select(id => (object)id.Value).ToList(),
            };
            if (weaponSimple != null)
            {
                o["weaponType"] = Str(ReflectionFinder.GetMember(weaponSimple, "weaponType"));
                var texts = new List<object>();
                foreach (var addon in ReflectionFinder.EnumerateValues(ReflectionFinder.GetMember(weaponSimple, "addons")))
                {
                    if (addon == null) continue;
                    var get = ReflectionFinder.FindMethod(addon.GetType(), "GetEffectText", 0);
                    if (get != null && ReflectionFinder.TryInvoke(addon, get, new object[0], out var t, out _) && !string.IsNullOrWhiteSpace(t as string))
                        texts.Add(((string)t).Trim());
                }
                o["effects"] = texts;
            }
            if (weaponBasic != null && ReflectionFinder.GetMember(weaponBasic, "hasUniqueEffect") is bool has && has)
            {
                o["uniqueEffectName"] = Str(ReflectionFinder.GetMember(weaponBasic, "uniqueEffectName"));
                o["uniqueEffectDescription"] = Str(ReflectionFinder.GetMember(weaponBasic, "uniqueEffectDescription"));
            }
            return o;
        }

        /// <summary>enhancesTo(부모 → 자식)로 parentId 와 강화 단계(기본 무기 = 1)를 매긴다. 순환이 있으면 멈춘다.</summary>
        public static void AssignTiers(List<JObj> weapons)
        {
            var parentOf = new Dictionary<int, int>();
            foreach (var w in weapons)
                if (w["id"] is int pid)
                    foreach (var c in MiniJson.AsList(w["enhancesTo"]))
                        if (c is int cid && !parentOf.ContainsKey(cid)) parentOf[cid] = pid;
            foreach (var w in weapons)
            {
                if (!(w["id"] is int id)) continue;
                w["parentId"] = parentOf.TryGetValue(id, out var p) ? (object)p : null;
                int tier = 1;
                var seen = new HashSet<int> { id };
                while (parentOf.TryGetValue(id, out var up) && seen.Add(up)) { tier++; id = up; }
                w["tier"] = tier;
            }
        }

        static JObj Entity(object entity) => new JObj
        {
            ["id"] = ReadInt(entity, "id"),
            ["name"] = Str(ReflectionFinder.GetMember(entity, "Name")),
            ["assetName"] = Str(ReflectionFinder.GetMember(entity, "name")),
            ["type"] = Str(ReflectionFinder.GetMember(entity, "type")),
            ["rarity"] = Str(ReflectionFinder.GetMember(entity, "rarity")),
        };

        static string Str(object v) => v?.ToString();

        static bool IsOrInherits(Type t, string name)
        {
            for (; t != null; t = t.BaseType) if (t.Name == name) return true;
            return false;
        }

        static int? ReadInt(object obj, string name)
        {
            var v = ReflectionFinder.GetMember(obj, name);
            return ReflectionFinder.IsNumber(v) ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : (int?)null;
        }
    }
}
