using System;
using System.Collections.Generic;
using System.Linq;

namespace SephiriaLive.Core
{
    /// <summary>
    /// 아이템 선택지 등급(S~D) — 추천 페이지(recommend_template.html)의 scoreArtifactCandidate/percentile/gradeFor 와 같은 계산.
    /// 입력은 data.json 항목(아티팩트: s=태그, t=등급, l=최대 레벨, kw=키워드 / 무기: kw=제공 키워드, redef=바꾸거나 없애는 키워드, type=무기 종류).
    /// 등급은 화면에 뜬 선택지끼리가 아니라 **전체 아티팩트를 같은 기준으로 점수 매긴 분포에서의 위치**(절대평가)로 정한다.
    /// 석판·포션 등 아티팩트가 아닌 것은 등급을 매기지 않는다.
    /// </summary>
    public static class GradeCalculator
    {
        static readonly Dictionary<string, int> TierRank = new Dictionary<string, int> { ["Common"] = 1, ["Uncommon"] = 2, ["Rare"] = 3, ["Legend"] = 4 };

        static readonly string[] Melee = { "SwordAndShield", "GreatSword", "Dagger", "Katana" };
        /// <summary>
        /// 무기 종류가 정해 주는 동작: 키워드 → 그 동작을 쓸 수 있는 무기 종류. 목록에 없는 무기면 그 효과는 못 씀(감점·C 이하).
        /// shortcut: 데이터로 확인된 것만(대시 공격·근접·재장전). 다른 무기 전용 동작이 확인되면 여기에 추가.
        /// </summary>
        public static readonly Dictionary<string, string[]> WeaponOnly = new Dictionary<string, string[]>
        {
            ["dash_attack"] = Melee,
            ["melee"] = Melee,
            ["reload"] = new[] { "Crossbow" },
        };

        public class Result
        {
            public double Total;                         // 빌드 점수(등급 계산용): 태그 시너지 + 무기 궁합만
            public double TieBreak;                      // 희귀도·최대 레벨 — 등급에는 안 쓰고 같은 점수끼리 정렬할 때만
            public bool Caution;                         // 무기가 바꾸거나 없애는 동작에 의존(충돌) 또는 무기가 못 쓰는 동작에 의존
            public List<string> Unusable = new List<string>(); // 무기 종류 때문에 못 쓰는 키워드
            public List<string> Conflicts = new List<string>(); // 무기가 바꾸거나 없애는 키워드
            public List<string> Supported = new List<string>(); // 무기가 제공하는 키워드
            public List<string> HitTags = new List<string>();   // 가방 빌드와 겹치는 태그
            public List<string> DamageHits = new List<string>(); // 사용자가 고른 주요 피해 방식과 맞는 것(data.json dt)
            public string Grade;                         // S/A/B/C/D
        }

        /// <summary>현재 가방 아티팩트들의 태그 수(빌드 방향).</summary>
        public static Dictionary<string, int> TagCounts(IEnumerable<JObj> inventoryArtifacts)
        {
            var counts = new Dictionary<string, int>();
            foreach (var a in inventoryArtifacts)
                foreach (var t in Strings(a?["s"]))
                    counts[t] = counts.TryGetValue(t, out var c) ? c + 1 : 1;
            return counts;
        }

        /// <summary>
        /// 빌드 점수 = 태그 시너지×3 + 주요 피해 방식 일치×2 + 무기 지원×1.5 − 무기가 못 쓰는 효과×3.
        /// damageFocus: 사용자가 고른 주요 피해 방식(physical/fire/lightning/ice/…, 여러 개), 없으면 null.
        /// 희귀도·최대 레벨은 빌드와 무관한 아이템을 끌어올려서(예: 빌드에 없는 전설 아이템이 B) 등급에서 뺐다 — TieBreak 로만 둔다.
        /// </summary>
        public static Result Score(JObj artifact, Dictionary<string, int> tagCounts, JObj weapon, ICollection<string> damageFocus = null)
        {
            var res = new Result();
            double synergy = 0;
            foreach (var t in Strings(artifact["s"]))
                if (tagCounts.TryGetValue(t, out var c) && c > 0) { synergy += c; res.HitTags.Add(t); }
            var tier = artifact["t"] as string;
            // 희귀도·레벨: 등급에는 안 씀(같은 빌드 점수끼리 정렬용)
            res.TieBreak = (tier != null && TierRank.TryGetValue(tier, out var r) ? r : 1) + Math.Min(MiniJson.AsInt(artifact["l"]), 8) * 0.1;

            double weaponBonus = 0, penalty = 0;
            if (weapon != null)
            {
                var provides = new HashSet<string>(Strings(weapon["kw"]));
                var redef = new HashSet<string>(Strings(weapon["redef"]));
                var type = weapon["type"] as string;
                foreach (var k in Strings(artifact["kw"]))
                {
                    if (type != null && WeaponOnly.TryGetValue(k, out var allowed) && !allowed.Contains(type)) { res.Unusable.Add(k); penalty += 3; }
                    else if (redef.Contains(k)) { res.Caution = true; res.Conflicts.Add(k); }
                    else if (provides.Contains(k)) { weaponBonus += 1.5; res.Supported.Add(k); }
                }
                if (res.Caution) weaponBonus = Math.Min(weaponBonus, 0.5); // 충돌 의심되면 무기 가점은 거의 안 줌
                if (res.Unusable.Count > 0) res.Caution = true;
            }
            if (damageFocus != null)
                foreach (var d in Strings(artifact["dt"]).Distinct())
                    if (damageFocus.Contains(d)) res.DamageHits.Add(d);
            res.Total = synergy * 3 + res.DamageHits.Count * 2 + weaponBonus - penalty;
            return res;
        }

        /// <summary>
        /// 등급 이유를 한 줄로: 주의("대시공격 못 씀", "일반공격 충돌") → 겹치는 콤보를 묶어 "정밀/그림자 콤보"(가방에 많은 태그 순)
        /// → 고른 피해 방식 "화염 피해" → 무기가 지원하는 "치명타".
        /// 각 덩어리는 ", " 로 잇고 최대 max 덩어리. 근거가 없으면 빈 문자열. 한국어 이름은 data.json 의 tagKo·kwKo.
        /// </summary>
        public static string Summary(Result r, Dictionary<string, int> tagCounts, JObj tagKo, JObj kwKo, int max = 3, JObj dmgKo = null)
        {
            string Tag(string t) => tagKo?[t] as string ?? t;
            string Kw(string k) => kwKo?[k] as string ?? k;
            var parts = new List<string>();
            parts.AddRange(r.Unusable.Select(k => Kw(k) + " 못 씀")); // 주의가 먼저 보이게
            parts.AddRange(r.Conflicts.Select(k => Kw(k) + " 충돌"));
            if (r.HitTags.Count > 0) parts.Add(string.Join("/", r.HitTags.OrderByDescending(t => tagCounts[t]).Select(Tag)) + " 콤보");
            if (r.DamageHits.Count > 0) parts.Add(string.Join("/", r.DamageHits.Select(d => dmgKo?[d] as string ?? d)) + " 피해");
            parts.AddRange(r.Supported.Select(Kw));
            return string.Join(", ", parts.Take(max));
        }

        /// <summary>
        /// 절대평가: 전체 아티팩트 점수(allTotals) 중 이 점수의 백분위로 등급. 상위 8% S, 25% A, 50% B, 75% C, 나머지 D.
        /// 충돌·못 쓰는 효과가 있으면 C 이하.
        /// </summary>
        public static void AssignGrades(IEnumerable<Result> targets, IList<double> allTotals)
        {
            var sorted = allTotals.OrderBy(t => t).ToArray();
            foreach (var x in targets)
            {
                var pct = Percentile(sorted, x.Total);
                var g = pct >= 0.92 ? "S" : pct >= 0.75 ? "A" : pct >= 0.5 ? "B" : pct >= 0.25 ? "C" : "D";
                if (x.Caution && (g == "S" || g == "A" || g == "B")) g = "C";
                x.Grade = g;
            }
        }

        /// <summary>sorted(오름차순) 중 value 보다 작은 것의 비율(같은 값은 절반만) — 0~1</summary>
        public static double Percentile(double[] sorted, double value)
        {
            if (sorted.Length == 0) return 0;
            int less = 0, equal = 0;
            foreach (var t in sorted) { if (t < value - 1e-9) less++; else if (Math.Abs(t - value) <= 1e-9) equal++; }
            return (less + equal * 0.5) / sorted.Length;
        }

        static IEnumerable<string> Strings(object list) => MiniJson.AsList(list).Select(o => o as string).Where(s => !string.IsNullOrEmpty(s));
    }
}
