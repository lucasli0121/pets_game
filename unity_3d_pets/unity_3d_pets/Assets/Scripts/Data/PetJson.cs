using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PetV1.Data
{
    /// <summary>
    /// 极小的 JSON 解析器。**为什么不用 JsonUtility**：
    ///
    /// ① 解不了嵌套层级。后台统一返回 <c>{"code":0,"msg":"ok","data":{...}}</c>，
    ///    真正的字段在 data 里面，再往下一层的 <c>config.food</c> 更是两层。
    ///    实测 <c>JsonUtility.FromJson</c> 遇到 <c>{"a":{"b":1}}</c> 会把内层整个丢掉，
    ///    内层类是 public 还是 private 都一样。
    /// ② 解不了 map。<c>assets.inventory</c> 是 <c>{"food_beef":1}</c> 这种动态键名的对象，
    ///    而 Unity 的序列化后端根本不支持 Dictionary。
    /// ③ 解不了 <c>null</c> 与字段缺失的区别、也处理不了数字精度。
    ///
    /// 工程已有先例：<see cref="PetSceneConfigCache"/> 为了剥一层 config 手写了括号配平，
    /// 说明这条路是被接受的。但那里只够拿 scene 数组，拿不到 map 与多层嵌套。
    ///
    /// 不引第三方（Newtonsoft 之类）：软著口径要求「无第三方代码依赖」，
    /// 而且小游戏首包 4MB 很紧，一个 JSON 库就是几百 KB。
    ///
    /// 只做「读」，不做序列化 —— 请求体是我们自己拼的（字段少且固定），
    /// 写一个完整的序列化器不值得。
    /// </summary>
    public enum PetJsonType
    {
        Null = 0,
        Bool = 1,
        Number = 2,
        String = 3,
        Array = 4,
        Object = 5,
    }

    /// <summary>
    /// 解析出来的一个 JSON 节点。所有访问都不抛异常：类型不对或键不存在时返回调用方给的默认值。
    /// 这一点是刻意的 —— 后台加字段/改类型时，客户端应该「拿到默认值继续跑」，
    /// 而不是在 UI 线程上抛异常把整页打死。
    /// </summary>
    public class PetJsonValue
    {
        public PetJsonType Type { get; private set; }

        private bool _bool;
        private double _num;
        private long _lng;
        private bool _isInteger;
        private string _str;
        private List<PetJsonValue> _items;
        private Dictionary<string, PetJsonValue> _fields;

        /// <summary>数组长度 / 对象字段数。其他类型返回 0。</summary>
        public int Count
        {
            get
            {
                if (Type == PetJsonType.Array) return _items != null ? _items.Count : 0;
                if (Type == PetJsonType.Object) return _fields != null ? _fields.Count : 0;
                return 0;
            }
        }

        public bool IsNull => Type == PetJsonType.Null;
        public bool IsArray => Type == PetJsonType.Array;
        public bool IsObject => Type == PetJsonType.Object;

        /// <summary>数组下标。越界或不是数组返回 null（不会抛）。</summary>
        public PetJsonValue this[int index]
        {
            get
            {
                if (Type != PetJsonType.Array || _items == null) return null;
                if (index < 0 || index >= _items.Count) return null;
                return _items[index];
            }
        }

        /// <summary>对象取字段。缺失或不是对象返回 null（不会抛）。</summary>
        public PetJsonValue this[string key]
        {
            get
            {
                if (Type != PetJsonType.Object || _fields == null || string.IsNullOrEmpty(key)) return null;
                return _fields.TryGetValue(key, out var v) ? v : null;
            }
        }

        public bool Has(string key)
        {
            return Type == PetJsonType.Object && _fields != null && _fields.ContainsKey(key);
        }

        /// <summary>数组元素枚举。不是数组时返回空序列，调用方可以直接 foreach。</summary>
        public IEnumerable<PetJsonValue> Items
        {
            get
            {
                if (Type != PetJsonType.Array || _items == null) yield break;
                for (int i = 0; i < _items.Count; i++) yield return _items[i];
            }
        }

        /// <summary>对象字段名枚举。不是对象时返回空序列。</summary>
        public IEnumerable<string> Keys
        {
            get
            {
                if (Type != PetJsonType.Object || _fields == null) yield break;
                foreach (var k in _fields.Keys) yield return k;
            }
        }

        public string AsString(string def = "")
        {
            if (Type == PetJsonType.String) return _str ?? def;
            if (Type == PetJsonType.Number) return _isInteger ? _lng.ToString() : _num.ToString(CultureInfo.InvariantCulture);
            if (Type == PetJsonType.Bool) return _bool ? "true" : "false";
            return def;
        }

        public bool AsBool(bool def = false)
        {
            switch (Type)
            {
                case PetJsonType.Bool: return _bool;
                // 后台偶尔用 0/1 表示布尔（flags 之类），容错一下
                case PetJsonType.Number: return _isInteger ? _lng != 0 : Math.Abs(_num) > double.Epsilon;
                case PetJsonType.String: return _str == "true" || _str == "1";
                default: return def;
            }
        }

        public int AsInt(int def = 0)
        {
            long v = AsLong(long.MinValue);
            if (v == long.MinValue) return def;
            if (v > int.MaxValue) return int.MaxValue;
            if (v < int.MinValue) return int.MinValue;
            return (int)v;
        }

        public long AsLong(long def = 0)
        {
            if (Type == PetJsonType.Number)
                return _isInteger ? _lng : (long)Math.Round(_num);
            if (Type == PetJsonType.String)
            {
                long parsed;
                if (long.TryParse(_str, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
            }
            if (Type == PetJsonType.Bool) return _bool ? 1 : 0;
            return def;
        }

        public float AsFloat(float def = 0f)
        {
            if (Type == PetJsonType.Number) return _isInteger ? (float)_lng : (float)_num;
            if (Type == PetJsonType.String)
            {
                float parsed;
                if (float.TryParse(_str, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
            }
            return def;
        }

        // ---- 构造（仅供解析器使用） ----

        internal static PetJsonValue NewNull() { return new PetJsonValue { Type = PetJsonType.Null }; }

        internal static PetJsonValue NewBool(bool v)
        {
            return new PetJsonValue { Type = PetJsonType.Bool, _bool = v };
        }

        internal static PetJsonValue NewString(string v)
        {
            return new PetJsonValue { Type = PetJsonType.String, _str = v ?? "" };
        }

        internal static PetJsonValue NewInteger(long v)
        {
            return new PetJsonValue { Type = PetJsonType.Number, _isInteger = true, _lng = v, _num = v };
        }

        internal static PetJsonValue NewFloat(double v)
        {
            return new PetJsonValue { Type = PetJsonType.Number, _isInteger = false, _num = v };
        }

        internal static PetJsonValue NewArray(List<PetJsonValue> items)
        {
            return new PetJsonValue { Type = PetJsonType.Array, _items = items ?? new List<PetJsonValue>() };
        }

        internal static PetJsonValue NewObject(Dictionary<string, PetJsonValue> fields)
        {
            return new PetJsonValue
            {
                Type = PetJsonType.Object,
                _fields = fields ?? new Dictionary<string, PetJsonValue>(),
            };
        }
    }

    /// <summary>递归下降解析。输入不合法时返回 null，不抛异常。</summary>
    public static class PetJson
    {
        public static PetJsonValue Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                int i = 0;
                var v = ParseValue(text, ref i);
                return v;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[PetV1] JSON 解析失败：" + e.Message);
                return null;
            }
        }

        private static PetJsonValue ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;

            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return PetJsonValue.NewString(ParseString(s, ref i));
                case 't':
                    SkipLiteral(s, ref i, "true");
                    return PetJsonValue.NewBool(true);
                case 'f':
                    SkipLiteral(s, ref i, "false");
                    return PetJsonValue.NewBool(false);
                case 'n':
                    SkipLiteral(s, ref i, "null");
                    return PetJsonValue.NewNull();
                default: return ParseNumber(s, ref i);
            }
        }

        private static PetJsonValue ParseObject(string s, ref int i)
        {
            var fields = new Dictionary<string, PetJsonValue>();
            i++; // 跳过 {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return PetJsonValue.NewObject(fields);
            }

            while (i < s.Length)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') break;
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') break;
                i++; // 跳过 :
                var val = ParseValue(s, ref i);
                fields[key] = val ?? PetJsonValue.NewNull();
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') i++;
                break;
            }
            return PetJsonValue.NewObject(fields);
        }

        private static PetJsonValue ParseArray(string s, ref int i)
        {
            var items = new List<PetJsonValue>();
            i++; // 跳过 [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return PetJsonValue.NewArray(items);
            }

            while (i < s.Length)
            {
                var val = ParseValue(s, ref i);
                items.Add(val ?? PetJsonValue.NewNull());
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') i++;
                break;
            }
            return PetJsonValue.NewArray(items);
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // 跳过起始引号
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) break;
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
                        // 后台的中文字段（name / msg）会走 \uXXXX 转义
                        if (i + 4 <= s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber,
                                             CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        private static PetJsonValue ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            bool hasDot = false, hasExp = false;
            while (i < s.Length)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') { i++; continue; }
                if (c == '.' && !hasDot && !hasExp) { hasDot = true; i++; continue; }
                if ((c == 'e' || c == 'E') && !hasExp)
                {
                    hasExp = true;
                    i++;
                    if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
                    continue;
                }
                break;
            }
            if (i == start) { i++; return PetJsonValue.NewNull(); }

            string raw = s.Substring(start, i - start);
            // 整数走 long，避免 exp 这类 int64 字段被 double 丢精度
            if (!hasDot && !hasExp)
            {
                long lv;
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out lv))
                    return PetJsonValue.NewInteger(lv);
            }
            double dv;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out dv))
                return PetJsonValue.NewFloat(dv);
            return PetJsonValue.NewNull();
        }

        private static void SkipLiteral(string s, ref int i, string literal)
        {
            if (i + literal.Length <= s.Length) i += literal.Length;
            else i = s.Length;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { i++; continue; }
                break;
            }
        }
    }
}
