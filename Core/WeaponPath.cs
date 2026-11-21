using System.Collections.Generic;

namespace SephiriaLive.Core
{
    /// <summary>
    /// 무기 강화 경로: 선택지가 목표 무기 자체이거나 목표 무기의 조상(강화 전 단계)인지.
    /// parentOf = 에셋 이름 → 강화 전 무기 에셋 이름(data.json weapons[].parent, 기본 무기는 없음).
    /// </summary>
    public static class WeaponPath
    {
        public static bool LeadsTo(string option, string target, IDictionary<string, string> parentOf)
        {
            if (string.IsNullOrEmpty(option) || string.IsNullOrEmpty(target)) return false;
            var seen = new HashSet<string>();
            for (var cur = target; cur != null && seen.Add(cur); parentOf.TryGetValue(cur, out cur))
                if (cur == option) return true;
            return false;
        }
    }
}
