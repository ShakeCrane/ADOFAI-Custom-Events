using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PEV.Scripting
{
    // A bounded package-supplied selection of public value fields. It only reads the
    // host-provided data type; paths cannot invoke properties, methods or reference objects.
    internal sealed class DeclarativeCacheKey<T> where T : struct
    {
        internal struct Bucket
        {
            internal float Step, Hysteresis;
            internal int Minimum, Maximum;
        }
        private sealed class Value
        {
            internal FieldInfo[] Path;
            internal string Name;
            internal int Digits;
            internal float Step;
            internal float Hysteresis;
            internal int Minimum, Maximum;
            internal object Read(object root)
            {
                foreach (var field in Path) root = field.GetValue(root);
                return root;
            }
            internal void Append(StringBuilder output, object root)
            {
                object value = Read(root);
                string text;
                if (Step > 0f)
                {
                    int bucket = Mathf.Clamp(Mathf.RoundToInt(Convert.ToSingle(value, CultureInfo.InvariantCulture) / Step), Minimum, Maximum);
                    text = bucket.ToString(CultureInfo.InvariantCulture);
                }
                else if (value is float f) text = f.ToString("F" + Digits, CultureInfo.InvariantCulture);
                else if (value is double d) text = d.ToString("F" + Digits, CultureInfo.InvariantCulture);
                else if (value is bool boolean) text = boolean ? "1" : "0";
                else if (value is IFormattable formattable) text = formattable.ToString(null, CultureInfo.InvariantCulture);
                else text = value as string ?? "";
                if (text.Length > 4096) throw new ArgumentException("Cache key value is too long");
                output.Append(text.Length).Append(':').Append(text).Append('|');
            }
        }
        private sealed class Sequence { internal FieldInfo[] Path; internal Value[] Values; }
        private readonly Value[] _values;
        private readonly Sequence[] _sequences;
        private DeclarativeCacheKey(Value[] values, Sequence[] sequences) { _values = values; _sequences = sequences; }

        internal static DeclarativeCacheKey<T> Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 32768) throw new ArgumentException("Invalid cache key resource");
            var root = JObject.Parse(new UTF8Encoding(false, true).GetString(bytes));
            if (root["version"]?.Type != JTokenType.Integer || root.Value<int>("version") != 1 ||
                !(root["values"] is JArray values) || values.Count < 1 || values.Count > 64)
                throw new ArgumentException("Invalid cache key specification");
            var result = new List<Value>();
            foreach (var value in values) result.Add(ParseValue(typeof(T), value));
            var sequences = new List<Sequence>();
            if (root["sequences"] != null)
            {
                if (!(root["sequences"] is JArray array) || array.Count > 8) throw new ArgumentException("Invalid cache key sequences");
                foreach (var token in array)
                {
                    if (!(token is JObject item) || !(item["values"] is JArray fields) || fields.Count < 1 || fields.Count > 32)
                        throw new ArgumentException("Invalid cache key sequence");
                    var path = Path(typeof(T), item.Value<string>("path"), out var type);
                    if (!type.IsArray || type.GetArrayRank() != 1 || !type.GetElementType().IsValueType)
                        throw new ArgumentException("Cache key sequence must contain plain values");
                    var selected = new List<Value>();
                    foreach (var field in fields) selected.Add(ParseValue(type.GetElementType(), field));
                    sequences.Add(new Sequence { Path = path, Values = selected.ToArray() });
                }
            }
            return new DeclarativeCacheKey<T>(result.ToArray(), sequences.ToArray());
        }
        private static Value ParseValue(Type type, JToken token)
        {
            if (!(token is JObject spec)) throw new ArgumentException("Invalid cache key value");
            var path = Path(type, spec.Value<string>("path"), out var leaf);
            if (leaf != typeof(float) && leaf != typeof(double) && leaf != typeof(int) && leaf != typeof(uint) &&
                leaf != typeof(long) && leaf != typeof(bool) && leaf != typeof(string))
                throw new ArgumentException("Unsupported cache key value type");
            int digits = spec.Value<int?>("digits") ?? 3;
            if (digits < 0 || digits > 6) throw new ArgumentException("Invalid cache key precision");
            var result = new Value { Path = path, Name = spec.Value<string>("path"), Digits = digits };
            if (spec["bucket"] != null)
            {
                if (!(spec["bucket"] is JObject bucket) || leaf != typeof(float) ||
                    (bucket["step"]?.Type != JTokenType.Float && bucket["step"]?.Type != JTokenType.Integer) ||
                    bucket["min"]?.Type != JTokenType.Integer || bucket["max"]?.Type != JTokenType.Integer)
                    throw new ArgumentException("Invalid cache key bucket");
                result.Step = bucket.Value<float>("step"); result.Minimum = bucket.Value<int>("min"); result.Maximum = bucket.Value<int>("max");
                if (bucket["hysteresis"] != null && bucket["hysteresis"].Type != JTokenType.Float && bucket["hysteresis"].Type != JTokenType.Integer)
                    throw new ArgumentException("Invalid cache key hysteresis");
                result.Hysteresis = bucket.Value<float?>("hysteresis") ?? result.Step;
                if (result.Step <= 0 || float.IsNaN(result.Step) || float.IsInfinity(result.Step) ||
                    result.Hysteresis < 0 || float.IsNaN(result.Hysteresis) || float.IsInfinity(result.Hysteresis) ||
                    result.Minimum < -1000000 || result.Maximum > 1000000 || result.Minimum > result.Maximum)
                    throw new ArgumentException("Invalid cache key bucket range");
            }
            return result;
        }
        internal bool TryBucket(string path, out Bucket bucket)
        {
            foreach (var value in _values)
                if (value.Name == path && value.Step > 0)
                {
                    bucket = new Bucket { Step = value.Step, Hysteresis = value.Hysteresis,
                        Minimum = value.Minimum, Maximum = value.Maximum };
                    return true;
                }
            bucket = default(Bucket); return false;
        }
        private static FieldInfo[] Path(Type type, string path, out Type leaf)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 128) throw new ArgumentException("Invalid cache key path");
            string[] names = path.Split('.');
            if (names.Length > 4) throw new ArgumentException("Cache key path is too deep");
            var fields = new FieldInfo[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                if (!type.IsValueType || string.IsNullOrEmpty(names[i])) throw new ArgumentException("Cache key path crosses a reference");
                fields[i] = type.GetField(names[i], BindingFlags.Public | BindingFlags.Instance);
                if (fields[i] == null || fields[i].IsNotSerialized) throw new ArgumentException("Unknown cache key field");
                type = fields[i].FieldType;
            }
            leaf = type; return fields;
        }
        internal string Build(T data)
        {
            object root = data;
            var output = new StringBuilder(256);
            foreach (var value in _values)
            {
                value.Append(output, root);
                if (output.Length > 131072) throw new ArgumentException("Cache key is too long");
            }
            foreach (var sequence in _sequences)
            {
                object raw = root;
                foreach (var field in sequence.Path) raw = field.GetValue(raw);
                var items = raw as Array;
                int count = items?.Length ?? 0;
                if (count > 1024) throw new ArgumentException("Cache key sequence is too long");
                output.Append('[').Append(count).Append('|');
                for (int i = 0; i < count; i++)
                {
                    object item = items.GetValue(i);
                    foreach (var value in sequence.Values) value.Append(output, item);
                    if (output.Length > 131072) throw new ArgumentException("Cache key is too long");
                }
                output.Append(']');
            }
            return output.ToString();
        }
    }
}
