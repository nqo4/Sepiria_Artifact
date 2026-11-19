using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace SephiriaLive.Core
{
    /// <summary>
    /// 다양한 "격자" 표현을 행×열 셀 목록으로 정규화한다.
    ///   rect2d     : T[,]
    ///   jagged     : T[][]
    ///   listOfList : IList&lt;IList&lt;T&gt;&gt;
    ///   posDict    : 좌표(x,y) 키 → 값 딕셔너리. 실제 게임(GridInventory)은
    ///                Mirror.SyncDictionary&lt;ItemPosition, T&gt; 형태를 쓴다(2026-11 Assembly-CSharp 확인).
    /// Cells[row][col], 즉 Cells[y][x].
    /// </summary>
    public static class MatrixExtractor
    {
        public class Result
        {
            public bool Recognized;
            public string ShapeKind = "unsupported";
            public int Rows;
            public int Cols;
            public List<List<object>> Cells = new List<List<object>>();
            public string Note;

            public JObj ToJson(Func<object, object> cellConverter = null)
            {
                return new JObj
                {
                    ["recognized"] = Recognized,
                    ["shape"] = ShapeKind,
                    ["rows"] = Rows,
                    ["cols"] = Cols,
                    ["cells"] = Cells.Select(r => (object)r.Select(c => cellConverter != null ? cellConverter(c) : c).ToList()).ToList(),
                    ["note"] = Note,
                };
            }
        }

        public static Result Extract(object source)
        {
            var r = new Result();
            if (source == null) { r.ShapeKind = "null"; return r; }
            try
            {
                if (source is Array arr)
                {
                    if (arr.Rank == 2) return FromRect(arr);
                    if (arr.Rank == 1 && IsJagged(arr)) return FromRows(arr, "jagged");
                }
                if (source is IList list && !(source is string) && list.Count > 0 && list.Cast<object>().All(x => x == null || x is IList))
                    return FromRows(list, "listOfList");
                if (source is IEnumerable seq && !(source is string))
                {
                    var pd = TryFromPosDict(seq);
                    if (pd != null) return pd;
                }
                r.Note = "지원하지 않는 형태: " + ReflectionFinder.FriendlyTypeName(source.GetType());
            }
            catch (Exception ex)
            {
                r.Note = "추출 중 예외: " + ex.GetType().Name + ": " + ex.Message;
            }
            return r;
        }

        static bool IsJagged(Array arr)
        {
            var et = arr.GetType().GetElementType();
            return et != null && (et.IsArray || typeof(IList).IsAssignableFrom(et));
        }

        static Result FromRect(Array arr)
        {
            var r = new Result { Recognized = true, ShapeKind = "rect2d", Rows = arr.GetLength(0), Cols = arr.GetLength(1) };
            int r0 = arr.GetLowerBound(0), c0 = arr.GetLowerBound(1);
            for (int i = 0; i < r.Rows; i++)
            {
                var row = new List<object>(r.Cols);
                for (int j = 0; j < r.Cols; j++) row.Add(arr.GetValue(r0 + i, c0 + j));
                r.Cells.Add(row);
            }
            return r;
        }

        static Result FromRows(IEnumerable rows, string kind)
        {
            var r = new Result { Recognized = true, ShapeKind = kind };
            foreach (var rowObj in rows)
            {
                var row = new List<object>();
                if (rowObj is IEnumerable rowSeq)
                    foreach (var c in rowSeq) row.Add(c);
                r.Cells.Add(row);
            }
            r.Rows = r.Cells.Count;
            r.Cols = r.Cells.Count == 0 ? 0 : r.Cells.Max(x => x.Count);
            return r;
        }

        /// <summary>KeyValuePair&lt;좌표, 값&gt; 열거를 격자로 바꾼다. 좌표형 키가 아니면 null.</summary>
        static Result TryFromPosDict(IEnumerable seq)
        {
            var entries = new List<(int x, int y, object v)>();
            bool any = false;
            foreach (var item in seq)
            {
                if (item == null || !ReflectionFinder.IsKeyValuePair(item.GetType())) return null;
                var key = ReflectionFinder.GetMember(item, "Key");
                if (!ReflectionFinder.TryGetXY(key, out var x, out var y)) return null;
                if (x < 0 || y < 0 || x > 255 || y > 255) continue; // 서브백 등 음수 좌표는 격자 밖
                entries.Add((x, y, ReflectionFinder.GetMember(item, "Value")));
                any = true;
            }
            if (!any)
            {
                // 빈 딕셔너리: 딕셔너리 타입이기만 하면 0x0으로 인식
                var t = seq.GetType();
                var isDict = t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
                return isDict ? new Result { Recognized = true, ShapeKind = "posDict", Note = "비어 있음" } : null;
            }
            var r = new Result
            {
                Recognized = true,
                ShapeKind = "posDict",
                Rows = entries.Max(e => e.y) + 1,
                Cols = entries.Max(e => e.x) + 1,
            };
            for (int i = 0; i < r.Rows; i++) r.Cells.Add(Enumerable.Repeat<object>(null, r.Cols).ToList());
            foreach (var e in entries) r.Cells[e.y][e.x] = e.v;
            return r;
        }

        /// <summary>좌표 키 딕셔너리를 (x,y) → 값 맵으로. 실패하면 빈 맵.</summary>
        public static Dictionary<(int x, int y), object> ToPositionMap(object source)
        {
            var map = new Dictionary<(int, int), object>();
            if (!(source is IEnumerable seq) || source is string) return map;
            try
            {
                foreach (var item in seq)
                {
                    if (item == null || !ReflectionFinder.IsKeyValuePair(item.GetType())) continue;
                    var key = ReflectionFinder.GetMember(item, "Key");
                    if (ReflectionFinder.TryGetXY(key, out var x, out var y))
                        map[(x, y)] = ReflectionFinder.GetMember(item, "Value");
                }
            }
            catch { }
            return map;
        }
    }
}
