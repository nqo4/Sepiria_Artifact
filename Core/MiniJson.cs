using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SephiriaLive.Core
{
    /// <summary>JSON 객체. 없는 키를 읽으면 예외 대신 null을 돌려준다.</summary>
    public class JObj : Dictionary<string, object>
    {
        public new object this[string key]
        {
            get => TryGetValue(key, out var v) ? v : null;
            set => base[key] = value;
        }
    }

    /// <summary>
    /// 외부 라이브러리 없이 쓰는 최소 JSON 직렬화/파싱.
    /// Parse 결과: 객체=JObj, 배열=List&lt;object&gt;, 정수=long, 실수=double, 문자열, bool, null.
    /// </summary>
    public static class MiniJson
    {
        // ---------------- Write ----------------

        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        const int MaxDepth = 64;

        static void WriteValue(StringBuilder sb, object v, int depth)
        {
            if (depth > MaxDepth) { sb.Append("null"); return; }
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case char c: WriteString(sb, c.ToString()); return;
                case Enum e: WriteString(sb, e.ToString()); return;
                case float f: WriteDouble(sb, f); return;
                case double d: WriteDouble(sb, d); return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
                case IDictionary dict:
                    {
                        sb.Append('{');
                        bool first = true;
                        foreach (DictionaryEntry kv in dict)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                            sb.Append(':');
                            WriteValue(sb, kv.Value, depth + 1);
                        }
                        sb.Append('}');
                        return;
                    }
                case IEnumerable seq:
                    {
                        sb.Append('[');
                        bool first = true;
                        foreach (var item in seq)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            WriteValue(sb, item, depth + 1);
                        }
                        sb.Append(']');
                        return;
                    }
                default:
                    WriteString(sb, SafeToString(v)); return;
            }
        }

        static string SafeToString(object v)
        {
            try { return v.ToString(); } catch { return v.GetType().Name; }
        }

        static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        // HTML 안에 그대로 끼워넣어도 안전하도록 '<'도 이스케이프
                        if (c < 0x20 || c == '<' || c == (char)0x2028 || c == (char)0x2029)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------- Parse ----------------

        public static object Parse(string json)
        {
            if (json == null) throw new FormatException("JSON 입력이 null입니다.");
            var p = new Parser(json);
            p.SkipWs();
            var v = p.ReadValue(0);
            p.SkipWs();
            if (!p.End) throw new FormatException($"JSON 끝 뒤에 남은 문자가 있습니다 (위치 {p.Pos}).");
            return v;
        }

        sealed class Parser
        {
            readonly string _s;
            int _i;
            public Parser(string s) { _s = s; }
            public int Pos => _i;
            public bool End => _i >= _s.Length;

            public void SkipWs()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            FormatException Err(string msg) => new FormatException($"{msg} (위치 {_i})");

            char Peek() => _i < _s.Length ? _s[_i] : throw Err("JSON이 예상보다 일찍 끝났습니다");

            public object ReadValue(int depth)
            {
                if (depth > MaxDepth) throw Err("JSON 중첩이 너무 깊습니다");
                SkipWs();
                char c = Peek();
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw Err($"예상하지 못한 문자 '{c}'");
                }
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw Err($"'{word}' 가 와야 합니다");
                _i += word.Length;
            }

            JObj ReadObject(int depth)
            {
                var obj = new JObj();
                _i++; // {
                SkipWs();
                if (Peek() == '}') { _i++; return obj; }
                while (true)
                {
                    SkipWs();
                    if (Peek() != '"') throw Err("객체 키는 문자열이어야 합니다");
                    var key = ReadString();
                    SkipWs();
                    if (Peek() != ':') throw Err("':' 가 와야 합니다");
                    _i++;
                    obj[key] = ReadValue(depth + 1);
                    SkipWs();
                    char c = Peek();
                    _i++;
                    if (c == ',') continue;
                    if (c == '}') return obj;
                    throw Err("',' 또는 '}' 가 와야 합니다");
                }
            }

            List<object> ReadArray(int depth)
            {
                var list = new List<object>();
                _i++; // [
                SkipWs();
                if (Peek() == ']') { _i++; return list; }
                while (true)
                {
                    list.Add(ReadValue(depth + 1));
                    SkipWs();
                    char c = Peek();
                    _i++;
                    if (c == ',') continue;
                    if (c == ']') return list;
                    throw Err("',' 또는 ']' 가 와야 합니다");
                }
            }

            string ReadString()
            {
                _i++; // "
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw Err("문자열이 닫히지 않았습니다");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (_i >= _s.Length) throw Err("잘못된 이스케이프");
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw Err("잘못된 \\u 이스케이프");
                            if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                throw Err("잘못된 \\u 이스케이프");
                            sb.Append((char)code);
                            _i += 4;
                            break;
                        default: throw Err($"알 수 없는 이스케이프 '\\{e}'");
                    }
                }
            }

            object ReadNumber()
            {
                int start = _i;
                if (_s[_i] == '-') _i++;
                bool isFloat = false;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c >= '0' && c <= '9') { _i++; continue; }
                    if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { isFloat = true; _i++; continue; }
                    break;
                }
                var text = _s.Substring(start, _i - start);
                if (!isFloat && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                throw Err($"잘못된 숫자 '{text}'");
            }
        }

        // ---------------- 편의 변환 ----------------

        public static int AsInt(object v, int fallback = 0)
        {
            switch (v)
            {
                case null: return fallback;
                case int i: return i;
                case long l: return (int)l;
                case double d: return (int)Math.Round(d);
                case bool b: return b ? 1 : 0;
                case string s: return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : fallback;
                default:
                    try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return fallback; }
            }
        }

        public static int? AsNullableInt(object v) => v == null ? (int?)null : AsInt(v);

        /// <summary>배열이면 그대로, 아니면 빈 리스트.</summary>
        public static List<object> AsList(object v) => v as List<object> ?? new List<object>();

        /// <summary>객체면 그대로, 아니면 null.</summary>
        public static JObj AsObj(object v) => v as JObj;
    }
}
