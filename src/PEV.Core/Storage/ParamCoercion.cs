using System;
using System.Collections.Generic;
using System.Linq;
using ADOFAI;
using Newtonsoft.Json.Linq;

namespace PEV.Storage
{
    /// <summary>
    /// The editor stores parameter values as loosely-typed objects, but LevelEvent.Encode
    /// casts some of them to concrete types (Tile -> Tuple&lt;int, TileRelativeTo&gt;,
    /// Vector2 -> Vector2, ...). Feeding it a plain float or a List&lt;object&gt; throws
    /// InvalidCastException at save time, which is exactly the kind of failure that must
    /// never reach a chart author.
    ///
    /// Coerce everything to the type the declared PropertyType implies.
    /// </summary>
    public static class ParamCoercion
    {
        public static object Coerce(PropertyInfo info, object value)
        {
            if (info == null || value == null) return value;
            try
            {
                switch (info.type)
                {
                    case PropertyType.Float:
                    case PropertyType.Rating:
                        return Convert.ToSingle(value);
                    case PropertyType.Int:
                        return Convert.ToInt32(value);
                    case PropertyType.Bool:
                        return ToBool(value);
                    case PropertyType.String:
                    case PropertyType.LongString:
                    case PropertyType.Color:
                    case PropertyType.File:
                        return value.ToString();
                    case PropertyType.Enum:
                        return ToEnum(info.enumType, value);
                    case PropertyType.Tile:
                        return ToTile(value);
                    case PropertyType.Vector2:
                        return ToVector2(value);
                    case PropertyType.FloatPair:
                        return ToFloatPair(value);
                    default:
                        return value;
                }
            }
            catch
            {
                // A value that will not convert is worse than a default; fall back to the
                // declared default so encode never throws.
                return info.value_default;
            }
        }

        private static bool ToBool(object value)
        {
            if (value is bool b) return b;
            string s = value.ToString();
            if (bool.TryParse(s, out bool parsed)) return parsed;
            // The editor sometimes stores "Enabled" instead of a bool.
            return s.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        }

        private static object ToEnum(Type enumType, object value)
        {
            if (enumType == null) return value;
            if (value.GetType() == enumType) return value;
            if (value is int i) return Enum.ToObject(enumType, i);
            string s = value.ToString();
            if (!string.IsNullOrEmpty(s) && Enum.IsDefined(enumType, s))
                return Enum.Parse(enumType, s, true);
            if (value is IConvertible && Enum.IsDefined(enumType, Convert.ToInt32(value)))
                return Enum.ToObject(enumType, Convert.ToInt32(value));
            return Enum.ToObject(enumType, 0);
        }

        private static object ToTile(object value)
        {
            int floor = 0;
            string rel = "ThisTile";

            if (value is Tuple<int, TileRelativeTo> tup) return tup;

            if (value is IEnumerable<object> seq)
            {
                var parts = seq.ToList();
                if (parts.Count >= 1) floor = Convert.ToInt32(parts[0]);
                if (parts.Count >= 2) rel = parts[1]?.ToString();
            }
            else if (value is JArray arr)
            {
                if (arr.Count >= 1) floor = arr[0].Value<int>();
                if (arr.Count >= 2) rel = arr[1].ToString();
            }

            var relativeTo = Enum.TryParse(rel, true, out TileRelativeTo r) ? r : TileRelativeTo.ThisTile;
            return new Tuple<int, TileRelativeTo>(floor, relativeTo);
        }

        private static object ToVector2(object value)
        {
            if (value is UnityEngine.Vector2 v) return v;
            float x = 0, y = 0;
            if (value is IEnumerable<object> seq)
            {
                var parts = seq.ToList();
                if (parts.Count >= 1) x = Convert.ToSingle(parts[0]);
                if (parts.Count >= 2) y = Convert.ToSingle(parts[1]);
            }
            else if (value is JArray arr)
            {
                if (arr.Count >= 1) x = arr[0].Value<float>();
                if (arr.Count >= 2) y = arr[1].Value<float>();
            }
            return new UnityEngine.Vector2(x, y);
        }

        private static object ToFloatPair(object value)
        {
            if (value is Tuple<float, float> tup) return tup;
            float x = 0, y = 0;
            if (value is IEnumerable<object> seq)
            {
                var parts = seq.ToList();
                if (parts.Count >= 1) x = Convert.ToSingle(parts[0]);
                if (parts.Count >= 2) y = Convert.ToSingle(parts[1]);
            }
            else if (value is JArray arr)
            {
                if (arr.Count >= 1) x = arr[0].Value<float>();
                if (arr.Count >= 2) y = arr[1].Value<float>();
            }
            return new Tuple<float, float>(x, y);
        }
    }
}
