using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace PEV.Scripting
{
    // Package-defined, tag-addressed scalar records. Neither the record names nor their
    // meanings are known to this parser; consumers decide what inputs they require.
    internal sealed class TaggedValueTable
    {
        internal sealed class Record
        {
            private readonly Dictionary<string, JValue> _items = new Dictionary<string, JValue>(StringComparer.Ordinal);
            internal static Record Parse(JToken token)
            {
                if (!(token is JObject obj) || obj.Count > 32) throw new ArgumentException("Invalid value record");
                var result = new Record();
                foreach (var property in obj.Properties())
                {
                    if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 128 ||
                        !(property.Value is JValue value) ||
                        (value.Type != JTokenType.Boolean && value.Type != JTokenType.Integer && value.Type != JTokenType.String) ||
                        (value.Type == JTokenType.String && (value.Value<string>()?.Length ?? 0) > 256))
                        throw new ArgumentException("Invalid value record entry");
                    result._items.Add(property.Name, value);
                }
                return result;
            }
            internal bool Boolean(string key)
            {
                if (!_items.TryGetValue(key, out var value) || value.Type != JTokenType.Boolean)
                    throw new ArgumentException("Invalid record boolean");
                return value.Value<bool>();
            }
            internal string Text(string key)
            {
                if (!_items.TryGetValue(key, out var value) || value.Type != JTokenType.String)
                    throw new ArgumentException("Invalid record text");
                return value.Value<string>();
            }
            internal uint Unsigned(string key, uint maximum)
            {
                if (!_items.TryGetValue(key, out var value) || value.Type != JTokenType.Integer)
                    throw new ArgumentException("Invalid record integer");
                long number = value.Value<long>();
                if (number < 0 || number > maximum) throw new ArgumentOutOfRangeException(key);
                return (uint)number;
            }
        }
        private readonly Dictionary<string, Record> _values = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
        internal string TagKey { get; private set; }
        internal string Default { get; private set; }
        internal Record Metadata { get; private set; }
        internal Record Resolve(string value) => value != null && _values.TryGetValue(value, out var found) ? found : _values[Default];

        internal static TaggedValueTable Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 131072) throw new ArgumentException("Invalid value table resource");
            var root = JObject.Parse(new UTF8Encoding(false, true).GetString(bytes));
            if (!(root["tagValues"] is JObject table) || !(table["values"] is JObject values) || values.Count < 1 || values.Count > 64)
                throw new ArgumentException("Invalid value table");
            var result = new TaggedValueTable { TagKey = Text(table, "tagKey"), Default = Text(table, "default"), Metadata = Record.Parse(table["metadata"]) };
            foreach (var property in values.Properties())
            {
                if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 128) throw new ArgumentException("Invalid value name");
                result._values.Add(property.Name, Record.Parse(property.Value));
            }
            if (!result._values.ContainsKey(result.Default)) throw new ArgumentException("Unknown default value");
            return result;
        }
        private static string Text(JObject obj, string name)
        {
            if (obj[name]?.Type != JTokenType.String) throw new ArgumentException("Invalid value table text");
            string value = obj.Value<string>(name);
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128) throw new ArgumentException("Invalid value table text");
            return value;
        }
    }
}
