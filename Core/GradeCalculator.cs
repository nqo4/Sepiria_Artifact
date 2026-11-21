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

        /// <summary>사이트에서 정한 빌드 방향(가방에 아직 없는 방향도 반영). 비어 있으면 그 항목은 무시.</summary>
        public class BuildFocus
        {
            public HashSet<string> Tags = new HashSet<string>();   // 콤보 고정(태그 키, 예: STURDY, WINDSONG)
            public HashSet<string> Damage = new HashSet<string>(); // 주요 피해 방식(data.json dt, 예: physical)
            public HashSet<string> Stats = new HashSet<string>();  // 주요 스탯(data.json st, 예: attack_speed)
        }

        public class Result
        {
            public double Total;                         // 빌드 점수(등급 계산용)
            public double TieBreak;                      // 희귀도·최대 레벨 — 등급에는 안 쓰고 같은 점수끼리 정렬할 때만
            public bool Caution;                         // 무기가 바꾸거나 없애는 동작에 의존(충돌) 또는 무기가 못 쓰는 동작에 의존
            public List<string> Unusable = new List<string>(); // 무기 종류 때문에 못 쓰는 키워드
            public List<string> Conflicts = new List<string>(); // 무기가 바꾸거나 없애는 키워드
            public List<string> Supported = new List<string>(); // 무기가 제공하는 키워드
            public List<string> HitTags = new List<string>();   // 가방 빌드·콤보 고정과 겹치는 태그
            public List<string> DamageHits = new List<string>(); // 고른 주요 피해 방식과 맞는 것(data.json dt)
            public List<string> DamageMisses = new List<string>(); // 피해 방식이 있는데 고른 것과 하나도 안 맞음(빌드 밖)
            public List<string> StatHits = new List<string>();   // 고른 주요 스탯과 맞는 것(data.json st)
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

        /// <summary>콤보 고정 태그 하나의 가치 = 가방 태그 3개분(추천 페이지의 pinBonus 3 과 같음).</summary>
        public const int PinnedTagWeight = 3;

        /// <summary>
        /// 빌드 점수 = 태그 시너지×3(가방 태그 수 + 콤보 고정이면 +3) + 주요 피해 방식 일치×2 − 빌드 밖 피해 방식 3
        ///           + 주요 스탯 일치×2 + 무기 지원×1.5 − 무기가 못 쓰는 효과×3.
        /// 희귀도·최대 레벨은 빌드와 무관한 아이템을 끌어올려서(예: 빌드에 없는 전설 아이템이 B) 등급에서 뺐다 — TieBreak 로만 둔다.
        /// </summary>
        /// <summary>무기가 이 키워드를 지원하나. 상태이상(status_*)은 같은 종류만, 일반 디버프 효과(debuff)는 무기가 아무 상태이상이나 걸면.</summary>
        public static bool WeaponSupports(HashSet<string> weaponKw, string k) =>
            weaponKw.Contains(k) || (k == "debuff" && weaponKw.Any(w => w.StartsWith("status_")));

        public static Result Score(JObj artifact, Dictionary<string, int> tagCounts, JObj weapon, BuildFocus focus = null)
        {
            var res = new Result();
            double synergy = 0;
            foreach (var t in Strings(artifact["s"]))
            {
                int c = (tagCounts.TryGetValue(t, out var n) ? n : 0) + (focus != null && focus.Tags.Contains(t) ? PinnedTagWeight : 0);
                if (c > 0) { synergy += c; res.HitTags.Add(t); }
            }
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
                    else if (WeaponSupports(provides, k)) { weaponBonus += 1.5; res.Supported.Add(k); }
                }
                if (res.Caution) weaponBonus = Math.Min(weaponBonus, 0.5); // 충돌 의심되면 무기 가점은 거의 안 줌
                if (res.Unusable.Count > 0) res.Caution = true;
            }
            if (focus != null)
            {
                var dts = Strings(artifact["dt"]).Distinct().ToList();
                res.DamageHits.AddRange(dts.Where(focus.Damage.Contains));
                // 피해 방식을 골라 뒀는데 이 아이템의 피해 방식이 하나도 안 맞으면 빌드 밖(예: 물리 빌드의 화염 전용 아이템)
                if (focus.Damage.Count > 0 && dts.Count > 0 && res.DamageHits.Count == 0) { res.DamageMisses.AddRange(dts); penalty += 3; }
                res.StatHits.AddRange(Strings(artifact["st"]).Distinct().Where(focus.Stats.Contains));
            }
            res.Total = synergy * 3 + res.DamageHits.Count * 2 + res.StatHits.Count * 2 + weaponBonus - penalty;
            return res;
        }

        /// <summary>
        /// 등급 이유를 한 줄로: 주의("대시공격 못 씀", "일반공격 충돌", "화염 피해(빌드 밖)") → 겹치는 콤보 "견고/바람노래 콤보"
        /// → 고른 피해 방식 "물리 피해" → 고른 스탯 "공격 속도" → 무기가 지원하는 "치명타".
        /// 각 덩어리는 ", " 로 잇고 최대 max 덩어리. 근거가 없으면 빈 문자열. 한국어 이름은 data.json 의 tagKo·kwKo·dmgKo·statKo.
        /// </summary>
        public static string Summary(Result r, Dictionary<string, int> tagCounts, JObj tagKo, JObj kwKo, int max = 3, JObj dmgKo = null, JObj statKo = null)
        {
            string Tag(string t) => tagKo?[t] as string ?? t;
            string Kw(string k) => kwKo?[k] as string ?? k;
            string Dmg(string d) => dmgKo?[d] as string ?? d;
            var parts = new List<string>();
            parts.AddRange(r.Unusable.Select(k => Kw(k) + " 못 씀")); // 주의가 먼저 보이게
            parts.AddRange(r.Conflicts.Select(k => Kw(k) + " 충돌"));
            if (r.DamageMisses.Count > 0) parts.Add(string.Join("/", r.DamageMisses.Select(Dmg)) + " 피해(빌드 밖)");
            if (r.HitTags.Count > 0) parts.Add(string.Join("/", r.HitTags.OrderByDescending(t => tagCounts.TryGetValue(t, out var c) ? c : 0).Select(Tag)) + " 콤보");
            if (r.DamageHits.Count > 0) parts.Add(string.Join("/", r.DamageHits.Select(Dmg)) + " 피해");
            if (r.StatHits.Count > 0) parts.Add(string.Join("/", r.StatHits.Select(st => statKo?[st] as string ?? st)));
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
                // 주의(충돌·못 쓰는 효과)가 있거나 빌드 근거가 없으면(점수 0 이하) 분포상 위치와 상관없이 C 이하
                if ((x.Caution || x.Total <= 0) && (g == "S" || g == "A" || g == "B")) g = "C";
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

        static readonly System.Text.RegularExpressions.Regex SpriteName = new System.Text.RegularExpressions.Regex(@"<sprite[^>]*?name=""?([A-Za-z0-9_]+)""?[^>]*>");
        static readonly System.Text.RegularExpressions.Regex RichTag = new System.Text.RegularExpressions.Regex(@"<[^>]+>");

        /// <summary>
        /// data.json 에 없는 게임 문장(기적 효과 등)을 prep_data_v2.py 와 같은 규칙(data.json rules)으로 분석:
        /// 키워드 아이콘 이름 → dt(피해 방식)·st(스탯), 순수 문장의 단어 → kw. 결과는 아티팩트 항목처럼 Score 에 넣을 수 있다.
        /// </summary>
        public static JObj TextFeatures(string richText, JObj rules, IEnumerable<string> tags = null)
        {
            richText = richText ?? "";
            var sprites = SpriteName.Matches(richText).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToList();
            var dtMap = MiniJson.AsObj(rules?["dtSprites"]);
            var stMap = MiniJson.AsObj(rules?["stSprites"]);
            var dt = new SortedSet<string>(sprites.SelectMany(n => dtMap != null && dtMap.TryGetValue(n, out var v) ? Strings(v) : Enumerable.Empty<string>()));
            var st = new SortedSet<string>(sprites.Select(n => stMap?[n] as string).Where(x => x != null));
            var plain = RichTag.Replace(richText, "");
            var kw = new SortedSet<string>();
            var exclude = MiniJson.AsObj(rules?["kwExclude"]);
            foreach (var pair in MiniJson.AsObj(rules?["kw"]) ?? new JObj())
            {
                var text = plain;
                foreach (var ex in Strings(exclude?[pair.Key])) text = text.Replace(ex, "");
                if (Strings(pair.Value).Any(text.Contains)) kw.Add(pair.Key);
            }
            return new JObj
            {
                ["s"] = (tags ?? Enumerable.Empty<string>()).Cast<object>().ToList(),
                ["kw"] = kw.Cast<object>().ToList(), ["dt"] = dt.Cast<object>().ToList(), ["st"] = st.Cast<object>().ToList(),
            };
        }

        /// <summary>기적처럼 효과 + 주는 아이템이 한 묶음인 선택지: 점수는 합, 근거 목록은 겹치지 않게 합침.</summary>
        public static Result Combine(Result main, IEnumerable<Result> extras)
        {
            var r = new Result { Total = main.Total, TieBreak = main.TieBreak, Caution = main.Caution };
            void Add(List<string> to, IEnumerable<string> from) { foreach (var x in from) if (!to.Contains(x)) to.Add(x); }
            foreach (var x in new[] { main }.Concat(extras ?? Enumerable.Empty<Result>()))
            {
                if (!ReferenceEquals(x, main)) { r.Total += x.Total; r.TieBreak = Math.Max(r.TieBreak, x.TieBreak); r.Caution |= x.Caution; }
                Add(r.Unusable, x.Unusable); Add(r.Conflicts, x.Conflicts); Add(r.Supported, x.Supported); Add(r.HitTags, x.HitTags);
                Add(r.DamageHits, x.DamageHits); Add(r.DamageMisses, x.DamageMisses); Add(r.StatHits, x.StatHits);
            }
            if (r.DamageHits.Count > 0) r.DamageMisses.Clear(); // 효과든 아이템이든 고른 피해 방식이 하나라도 맞으면 빌드 밖 표시는 뺌
            return r;
        }

        static IEnumerable<string> Strings(object list) => MiniJson.AsList(list).Select(o => o as string).Where(s => !string.IsNullOrEmpty(s));
    }
}
