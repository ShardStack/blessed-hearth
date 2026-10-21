// Reading and writing JSON.
//
// Everything the app saves (config.json, the state files) and everything CurseForge sends back is
// JSON. .NET Framework doesn't come with a JSON reader that's nice to use, so this is a small one.
// Under the hood JSON is just nested lists and dictionaries, so that's exactly what this turns it
// into: objects become Dictionary<string, object>, arrays become List<object>, numbers become
// long (or double if they have a decimal point), and the rest are string, bool or null.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BlessedHearth
{
    public class JsonException : Exception
    {
        public JsonException(string msg) : base(msg) { }
    }

    public static class Json
    {
        public static object Parse(string text)
        {
            var p = new Parser(text ?? "");
            p.SkipSpace();
            object v = p.Value();
            p.SkipSpace();
            if (!p.AtEnd) throw new JsonException("Extra text after the JSON");
            return v;
        }

        public static string Write(object value, bool indent = false)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, indent ? 0 : -1);
            return sb.ToString();
        }

        // A deep copy, handy for defaults that shouldn't get changed by accident
        public static object Clone(object value) => Parse(Write(value));

        class Parser
        {
            readonly string s;
            int i;
            public Parser(string text) { s = text; }
            public bool AtEnd => i >= s.Length;

            public void SkipSpace()
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r' || s[i] == '\uFEFF')) i++;
            }

            public object Value()
            {
                if (AtEnd) throw new JsonException("Unexpected end of JSON");
                char c = s[i];
                if (c == '{') return Obj();
                if (c == '[') return Arr();
                if (c == '"') return Str();
                if (c == 't' && Take("true")) return true;
                if (c == 'f' && Take("false")) return false;
                if (c == 'n' && Take("null")) return null;
                // Python's json writes these for odd floats, so read them too
                if (c == 'N' && Take("NaN")) return double.NaN;
                if (c == 'I' && Take("Infinity")) return double.PositiveInfinity;
                if (c == '-' || (c >= '0' && c <= '9')) return Num();
                throw new JsonException("Unexpected character in JSON at " + i);
            }

            bool Take(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
                i += word.Length;
                return true;
            }

            Dictionary<string, object> Obj()
            {
                var d = new Dictionary<string, object>();
                i++;
                SkipSpace();
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    SkipSpace();
                    if (AtEnd || s[i] != '"') throw new JsonException("Expected a name in JSON at " + i);
                    string k = Str();
                    SkipSpace();
                    if (AtEnd || s[i] != ':') throw new JsonException("Expected : in JSON at " + i);
                    i++;
                    SkipSpace();
                    d[k] = Value();
                    SkipSpace();
                    if (AtEnd) throw new JsonException("Unexpected end of JSON");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new JsonException("Expected , or } in JSON at " + i);
                }
            }

            List<object> Arr()
            {
                var list = new List<object>();
                i++;
                SkipSpace();
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    SkipSpace();
                    list.Add(Value());
                    SkipSpace();
                    if (AtEnd) throw new JsonException("Unexpected end of JSON");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return list; }
                    throw new JsonException("Expected , or ] in JSON at " + i);
                }
            }

            string Str()
            {
                var sb = new StringBuilder();
                i++;   // the opening quote
                while (true)
                {
                    if (AtEnd) throw new JsonException("Unfinished text in JSON");
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw new JsonException("Unfinished text in JSON");
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new JsonException("Bad \\u escape in JSON");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: throw new JsonException("Bad escape in JSON");
                    }
                }
            }

            object Num()
            {
                int start = i;
                if (s[i] == '-') i++;
                bool whole = true;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c >= '0' && c <= '9') { i++; continue; }
                    if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { whole = false; i++; continue; }
                    break;
                }
                string t = s.Substring(start, i - start);
                long l;
                if (whole && long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
                double d;
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                throw new JsonException("Bad number in JSON: " + t);
            }
        }

        static void WriteValue(StringBuilder sb, object v, int depth)
        {
            if (v == null) { sb.Append("null"); return; }
            var str = v as string;
            if (str != null) { WriteString(sb, str); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is double || v is float)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15) sb.Append(((long)d).ToString(CultureInfo.InvariantCulture)).Append(".0");
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is int || v is long || v is short || v is byte || v is uint || v is ulong || v is decimal)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            var dict = v as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, depth + 1);
                    WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                    sb.Append(depth >= 0 ? ": " : ":");
                    WriteValue(sb, kv.Value, depth >= 0 ? depth + 1 : -1);
                }
                if (!first) NewLine(sb, depth);
                sb.Append('}');
                return;
            }
            var list = v as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, depth + 1);
                    WriteValue(sb, item, depth >= 0 ? depth + 1 : -1);
                }
                if (!first) NewLine(sb, depth);
                sb.Append(']');
                return;
            }
            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        static void NewLine(StringBuilder sb, int depth)
        {
            if (depth < 0) return;
            sb.Append('\n').Append(' ', depth * 2);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
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
                        // anything unusual goes out escaped, which also keeps < > & from ever forming
                        // live HTML if a value ends up inside the page
                        if (c < 0x20 || c > 0x7E || c == '<' || c == '>' || c == '&')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    // Shortcuts for digging values out of parsed JSON without a cast on every line. They're
    // forgiving on purpose: a missing key or the wrong type just gives the fallback, the same way
    // Python's d.get("x") or {} works.
    public static class J
    {
        public static Dictionary<string, object> Obj(object o) => o as Dictionary<string, object>;
        public static Dictionary<string, object> ObjOrEmpty(object o) => (o as Dictionary<string, object>) ?? new Dictionary<string, object>();
        public static List<object> List(object o) => (o as List<object>) ?? new List<object>();

        public static object Get(this Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(this Dictionary<string, object> d, string key, string fallback = "")
        {
            object v = d.Get(key);
            if (v == null) return fallback;
            var s = v as string;
            return s ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static long? Long(this Dictionary<string, object> d, string key) => ToLong(d.Get(key));

        public static long? ToLong(object v)
        {
            if (v == null || v is bool) return null;
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            if (v is double) return (long)(double)v;
            var s = v as string;
            long l;
            if (s != null && long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
            return null;
        }

        public static bool Bool(this Dictionary<string, object> d, string key) => Truthy(d.Get(key));

        // Python's idea of true: not null, not false, not 0, not empty
        public static bool Truthy(object v)
        {
            if (v == null) return false;
            if (v is bool) return (bool)v;
            if (v is long) return (long)v != 0;
            if (v is int) return (int)v != 0;
            if (v is double) return (double)v != 0;
            var s = v as string;
            if (s != null) return s.Length > 0;
            var c = v as ICollection;
            if (c != null) return c.Count > 0;
            return true;
        }

        public static Dictionary<string, object> D(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
    }
}
