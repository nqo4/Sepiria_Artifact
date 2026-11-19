using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace SephiriaLive.Core
{
    /// <summary>
    /// 방어적 리플렉션 도우미. 어떤 메서드도 예외를 밖으로 던지지 않는다 — 못 찾으면 null/false를 돌려준다.
    /// (게임 업데이트로 필드명이 바뀌어도 크래시 대신 "못 찾음"으로 끝나게 하기 위함)
    /// </summary>
    public static class ReflectionFinder
    {
        public const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public const BindingFlags All = AllInstance | BindingFlags.Static;

        public class MemberDump
        {
            public string name;
            public string kind;   // "field" | "property"
            public string type;
            public string value;  // 짧은 미리보기 문자열
        }

        static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        /// <summary>현재 AppDomain의 모든 어셈블리에서 단순 이름(Name) 또는 전체 이름(FullName)으로 타입을 찾는다.</summary>
        public static Type FindTypeByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (TypeCache)
            {
                if (TypeCache.TryGetValue(name, out var cached) && cached != null) return cached;
            }
            Type found = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var t in SafeGetTypes(asm))
                {
                    if (t == null) continue;
                    if (t.FullName == name) { found = t; break; }
                    if (found == null && t.Name == name) found = t;
                }
                if (found != null && found.FullName == name) break;
            }
            if (found != null) lock (TypeCache) TypeCache[name] = found;
            return found;
        }

        static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
            catch { return Enumerable.Empty<Type>(); }
        }

        static FieldInfo FindField(Type t, string name)
        {
            for (var cur = t; cur != null; cur = cur.BaseType)
            {
                var f = cur.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        static PropertyInfo FindProperty(Type t, string name)
        {
            for (var cur = t; cur != null; cur = cur.BaseType)
            {
                try
                {
                    var p = cur.GetProperty(name, All | BindingFlags.DeclaredOnly);
                    if (p != null && p.GetIndexParameters().Length == 0) return p;
                }
                catch (AmbiguousMatchException) { }
            }
            return null;
        }

        public static bool HasMember(object obj, string name)
        {
            if (obj == null) return false;
            var t = obj.GetType();
            return FindField(t, name) != null || FindProperty(t, name) != null;
        }

        /// <summary>필드 → 프로퍼티 순으로 찾아 값을 읽는다. 없거나 실패하면 null.</summary>
        public static object GetMember(object obj, string name)
        {
            TryGetMember(obj, name, out var v);
            return v;
        }

        public static bool TryGetMember(object obj, string name, out object value)
        {
            value = null;
            if (obj == null || string.IsNullOrEmpty(name)) return false;
            try
            {
                var t = obj.GetType();
                var f = FindField(t, name);
                if (f != null) { value = f.GetValue(f.IsStatic ? null : obj); return true; }
                var p = FindProperty(t, name);
                if (p != null && p.CanRead) { value = p.GetValue(p.GetGetMethod(true).IsStatic ? null : obj, null); return true; }
            }
            catch { }
            return false;
        }

        /// <summary>후보 이름 중 처음으로 존재하는 멤버를 읽는다. 어떤 이름이 맞았는지 out으로 알려준다.</summary>
        public static object GetFirstMember(object obj, IEnumerable<string> names, out string matchedName)
        {
            matchedName = null;
            if (obj == null) return null;
            foreach (var n in names)
            {
                if (TryGetMember(obj, n, out var v)) { matchedName = n; return v; }
            }
            return null;
        }

        /// <summary>필드/프로퍼티에 값을 쓴다. 타입이 다르면(int→sbyte 등) 변환을 시도한다.</summary>
        public static bool SetMember(object obj, string name, object value)
        {
            if (obj == null) return false;
            try
            {
                var t = obj.GetType();
                var f = FindField(t, name);
                if (f != null) { f.SetValue(f.IsStatic ? null : obj, ConvertTo(value, f.FieldType)); return true; }
                var p = FindProperty(t, name);
                if (p != null && p.CanWrite) { p.SetValue(obj, ConvertTo(value, p.PropertyType), null); return true; }
            }
            catch { }
            return false;
        }

        public static object ConvertTo(object value, Type target)
        {
            if (value == null) return null;
            if (target.IsInstanceOfType(value)) return value;
            if (target.IsEnum) return Enum.ToObject(target, Convert.ToInt64(value, CultureInfo.InvariantCulture));
            return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }

        /// <summary>이름과 인자 개수로 메서드를 찾는다(상속 포함, 비공개 포함).</summary>
        public static MethodInfo FindMethod(Type t, string name, int paramCount)
        {
            for (var cur = t; cur != null; cur = cur.BaseType)
            {
                foreach (var m in cur.GetMethods(All | BindingFlags.DeclaredOnly))
                {
                    if (m.Name == name && !m.IsGenericMethodDefinition && m.GetParameters().Length == paramCount) return m;
                }
            }
            return null;
        }

        /// <summary>메서드를 호출하고 인자를 파라미터 타입에 맞게 변환한다. 실패하면 false와 에러 메시지.</summary>
        public static bool TryInvoke(object obj, MethodInfo m, object[] args, out object result, out string error)
        {
            result = null; error = null;
            try
            {
                var ps = m.GetParameters();
                var converted = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++) converted[i] = ConvertTo(args[i], ps[i].ParameterType);
                result = m.Invoke(m.IsStatic ? null : obj, converted);
                return true;
            }
            catch (TargetInvocationException tie)
            {
                error = $"{m.Name}: {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}";
            }
            catch (Exception ex)
            {
                error = $"{m.Name}: {ex.GetType().Name}: {ex.Message}";
            }
            return false;
        }

        /// <summary>디버그용: 객체의 모든 필드/프로퍼티 이름, 타입, 값 미리보기.</summary>
        public static List<MemberDump> DumpMembers(object obj, int maxValueLength = 120)
        {
            var list = new List<MemberDump>();
            if (obj == null) return list;
            var seen = new HashSet<string>();
            for (var cur = obj.GetType(); cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                FieldInfo[] fields;
                try { fields = cur.GetFields(AllInstance | BindingFlags.DeclaredOnly); } catch { continue; }
                foreach (var f in fields)
                {
                    if (!seen.Add(f.Name)) continue;
                    string preview;
                    try { preview = Preview(f.GetValue(obj), maxValueLength); } catch (Exception ex) { preview = "<" + ex.GetType().Name + ">"; }
                    list.Add(new MemberDump { name = f.Name, kind = "field", type = FriendlyTypeName(f.FieldType), value = preview });
                }
                PropertyInfo[] props;
                try { props = cur.GetProperties(AllInstance | BindingFlags.DeclaredOnly); } catch { continue; }
                foreach (var p in props)
                {
                    if (p.GetIndexParameters().Length != 0 || !p.CanRead || !seen.Add(p.Name)) continue;
                    // 프로퍼티 getter는 부작용이 있을 수 있어 값은 읽지 않는다(이름/타입만).
                    list.Add(new MemberDump { name = p.Name, kind = "property", type = FriendlyTypeName(p.PropertyType), value = null });
                }
            }
            return list;
        }

        public static List<object> DumpToJson(List<MemberDump> dump)
        {
            return dump.Select(d => (object)new JObj { ["name"] = d.name, ["kind"] = d.kind, ["type"] = d.type, ["value"] = d.value }).ToList();
        }

        static string Preview(object v, int max)
        {
            if (v == null) return "null";
            string s;
            if (v is string str) s = "\"" + str + "\"";
            else if (v is ICollection col) s = $"{FriendlyTypeName(v.GetType())} (count={col.Count})";
            else
            {
                try { s = v.ToString(); } catch { s = v.GetType().Name; }
            }
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        public static string FriendlyTypeName(Type t)
        {
            if (t == null) return "null";
            if (!t.IsGenericType) return t.Name;
            var baseName = t.Name;
            var tick = baseName.IndexOf('`');
            if (tick >= 0) baseName = baseName.Substring(0, tick);
            return baseName + "<" + string.Join(",", t.GetGenericArguments().Select(FriendlyTypeName)) + ">";
        }

        /// <summary>
        /// 컬렉션의 원소를 꺼낸다. 딕셔너리(KeyValuePair 열거)면 Value만 꺼낸다.
        /// Mirror SyncDictionary/SyncList처럼 IDictionary를 구현하지 않는 컬렉션도 IEnumerable이면 처리된다.
        /// </summary>
        public static List<object> EnumerateValues(object collection)
        {
            var list = new List<object>();
            if (collection == null || collection is string || !(collection is IEnumerable seq)) return list;
            try
            {
                foreach (var item in seq)
                {
                    if (item != null && IsKeyValuePair(item.GetType())) list.Add(GetMember(item, "Value"));
                    else list.Add(item);
                }
            }
            catch { }
            return list;
        }

        public static bool IsKeyValuePair(Type t) =>
            t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);

        /// <summary>x/y 좌표를 가진 객체(ItemPosition, Vector2Int 등)에서 좌표를 읽는다.</summary>
        public static bool TryGetXY(object pos, out int x, out int y)
        {
            x = y = 0;
            if (pos == null) return false;
            foreach (var (xn, yn) in new[] { ("x", "y"), ("X", "Y"), ("xIdx", "yIdx"), ("XIdx", "YIdx") })
            {
                if (TryGetMember(pos, xn, out var xv) && TryGetMember(pos, yn, out var yv) && IsNumber(xv) && IsNumber(yv))
                {
                    x = Convert.ToInt32(xv, CultureInfo.InvariantCulture);
                    y = Convert.ToInt32(yv, CultureInfo.InvariantCulture);
                    return true;
                }
            }
            return false;
        }

        public static bool IsNumber(object v) =>
            v is sbyte || v is byte || v is short || v is ushort || v is int || v is uint || v is long || v is ulong || v is float || v is double || v is decimal;
    }
}
