using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SephiriaLive.Core
{
    /// <summary>
    /// 석판의 효과 범위 정의를 게임 객체에서 직접 읽는다(순수 조회). 위키 패턴과 교차검증용.
    ///
    /// 실제 게임 구조 (2026-11-19, Assembly-CSharp.dll):
    ///   StoneTablet { string query, conditionQuery; int rotation; bool isRotatable; sbyte xIdx, yIdx; int entityID;
    ///                 SyncList&lt;AdditionEffectData&gt; EffectRange; SyncList&lt;AdditionCriteriaData&gt; CriteriaRange;
    ///                 string GetRotatedQuery(string query, int rotation) }
    ///   AdditionEffectData { ItemPosition position; EffectType effectType; int levelParam; bool isXWorldPosition, isYWorldPosition; ... }
    ///   AdditionCriteriaData { ItemPosition position; CriteriaType effectType; ... }
    /// EffectRange/CriteriaRange는 가방에 놓여 효과가 적용된 석판에서만 채워진다(프리팹은 비어 있음).
    /// </summary>
    public static class TabletReader
    {
        public static JObj Describe(object tablet)
        {
            var o = new JObj();
            if (tablet == null) return o;
            o["entityId"] = ReadInt(tablet, "entityID");
            o["x"] = ReadInt(tablet, "xIdx");
            o["y"] = ReadInt(tablet, "yIdx");
            o["rotation"] = ReadInt(tablet, "rotation");
            o["rotatable"] = ReflectionFinder.GetMember(tablet, "isRotatable");
            var query = ReflectionFinder.GetMember(tablet, "query") as string;
            o["query"] = query;
            o["conditionQuery"] = ReflectionFinder.GetMember(tablet, "conditionQuery") as string;

            var rotate = ReflectionFinder.FindMethod(tablet.GetType(), "GetRotatedQuery", 2);
            if (rotate != null && !string.IsNullOrEmpty(query))
            {
                var rotated = new List<object>();
                for (int r = 0; r < 4; r++)
                    rotated.Add(ReflectionFinder.TryInvoke(tablet, rotate, new object[] { query, r }, out var res, out var err) ? res as string : "error: " + err);
                o["rotatedQueries"] = rotated;
            }
            o["effectRange"] = Ranges(ReflectionFinder.GetMember(tablet, "EffectRange"), true);
            o["criteriaRange"] = Ranges(ReflectionFinder.GetMember(tablet, "CriteriaRange"), false);
            return o;
        }

        /// <summary>아이템 정의(ItemEntity)와 그 프리팹의 석판 컴포넌트로 카탈로그 항목을 만든다.</summary>
        public static JObj DescribeEntity(object entity, object tablet)
        {
            var o = Describe(tablet);
            o["entityId"] = ReadInt(entity, "id");
            o["name"] = ReflectionFinder.GetMember(entity, "Name")?.ToString();
            o["assetName"] = ReflectionFinder.GetMember(entity, "name")?.ToString();
            o["rarity"] = ReflectionFinder.GetMember(entity, "rarity")?.ToString();
            return o;
        }

        /// <summary>인벤토리의 stoneTablets(위치 → 석판)를 위치 순서대로 설명한다.</summary>
        public static List<object> DescribeInventory(object inventory)
        {
            var map = MatrixExtractor.ToPositionMap(ReflectionFinder.GetFirstMember(inventory, InventoryWriter.TabletCollectionNames, out _));
            return map.Where(kv => kv.Value != null).OrderBy(kv => kv.Key.y).ThenBy(kv => kv.Key.x)
                .Select(kv => (object)Describe(kv.Value)).ToList();
        }

        /// <summary>
        /// 게임 자신의 해석기 StoneTablet.ParseQuery(query, width, height, storage, originPos, rotation, out humanReadable)로
        /// query를 칸 목록으로 바꾼다. 결과는 origin 기준 상대 좌표(dx, dy) + 값 문자열("1", "-1", "X", "IGNORECRITERIA" 등).
        /// HORIZONTAL/TOP/CHECKERBOARD 같은 보드 기준 키워드는 보드 크기에 따라 칸이 달라진다.
        /// </summary>
        public static List<object> ParseCells(object tablet, string query, int width, int height, int ox, int oy, int rotation, out string error)
        {
            error = null;
            var cells = new List<object>();
            var parse = ReflectionFinder.FindMethod(tablet.GetType(), "ParseQuery", 7);
            if (parse == null || !parse.IsStatic) { error = "ParseQuery(7) 없음"; return cells; }
            if (string.IsNullOrEmpty(query)) return cells;
            var pos = Activator.CreateInstance(parse.GetParameters()[4].ParameterType);
            ReflectionFinder.SetMember(pos, "x", ox);
            ReflectionFinder.SetMember(pos, "y", oy);
            if (!ReflectionFinder.TryInvoke(null, parse, new object[] { query, width, height, width * height, pos, rotation, null }, out var res, out error))
                return cells;
            foreach (var m in ReflectionFinder.EnumerateValues(res))
            {
                if (m == null || !ReflectionFinder.TryGetXY(ReflectionFinder.GetMember(m, "position"), out var x, out var y)) continue;
                var c = new JObj { ["dx"] = x - ox, ["dy"] = y - oy, ["value"] = ReflectionFinder.GetMember(m, "value") as string };
                if (ReflectionFinder.GetMember(m, "isXWorldPosition") is bool xw && xw) c["xWorld"] = true;
                if (ReflectionFinder.GetMember(m, "isYWorldPosition") is bool yw && yw) c["yWorld"] = true;
                cells.Add(c);
            }
            return cells;
        }

        static List<object> Ranges(object syncList, bool isEffect)
        {
            var list = new List<object>();
            foreach (var d in ReflectionFinder.EnumerateValues(syncList))
            {
                if (d == null) continue;
                var o = new JObj();
                if (ReflectionFinder.TryGetXY(ReflectionFinder.GetMember(d, "position"), out var x, out var y)) { o["x"] = x; o["y"] = y; }
                o["type"] = ReflectionFinder.GetMember(d, "effectType")?.ToString();
                if (isEffect) o["level"] = ReadInt(d, "levelParam");
                if (ReflectionFinder.GetMember(d, "isXWorldPosition") is bool xw && xw) o["xWorld"] = true;
                if (ReflectionFinder.GetMember(d, "isYWorldPosition") is bool yw && yw) o["yWorld"] = true;
                list.Add(o);
            }
            return list;
        }

        static object ReadInt(object obj, string name)
        {
            var v = ReflectionFinder.GetMember(obj, name);
            return ReflectionFinder.IsNumber(v) ? (object)Convert.ToInt32(v, CultureInfo.InvariantCulture) : null;
        }
    }
}
