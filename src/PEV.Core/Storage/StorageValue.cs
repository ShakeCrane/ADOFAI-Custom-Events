using System;
using System.Collections.Generic;
using ADOFAI;
using Newtonsoft.Json.Linq;

namespace PEV.Storage
{
    /// <summary>
    /// Converts a live editor value back into the *file* representation.
    ///
    /// The editor holds enums as boxed enum values and tiles as tuples, which Json.NET
    /// would happily write as bare integers. That is unreadable for authors and, worse,
    /// silently drops the enum member name. Everything the manifest declares gets written
    /// back in the shape a human would have typed: names for enums, arrays for compound
    /// values.
    /// </summary>
    public static class StorageValue
    {
        public static JToken Canonical(PropertyInfo info, object live)
        {
            if (live == null) return JValue.CreateNull();
            if (info == null) return SafeFromObject(live);

            try
            {
                switch (info.type)
                {
                    case PropertyType.Enum:
                        return new JValue(live.ToString());

                    case PropertyType.Tile:
                        if (live is Tuple<int, TileRelativeTo> tile)
                            return new JArray(tile.Item1, tile.Item2.ToString());
                        return SafeFromObject(live);

                    case PropertyType.Vector2:
                        if (live is UnityEngine.Vector2 v)
                            return new JArray(v.x, v.y);
                        return SafeFromObject(live);

                    case PropertyType.FloatPair:
                        if (live is Tuple<float, float> pair)
                            return new JArray(pair.Item1, pair.Item2);
                        return SafeFromObject(live);

                    case PropertyType.Float:
                    case PropertyType.Rating:
                        return new JValue(Convert.ToSingle(live));

                    case PropertyType.Int:
                        return new JValue(Convert.ToInt32(live));

                    case PropertyType.Bool:
                        return new JValue(Convert.ToBoolean(live));

                    case PropertyType.String:
                    case PropertyType.LongString:
                    case PropertyType.Color:
                    case PropertyType.File:
                        return new JValue(live.ToString());

                    default:
                        return SafeFromObject(live);
                }
            }
            catch
            {
                return SafeFromObject(live);
            }
        }

        private static JToken SafeFromObject(object live)
        {
            try { return JToken.FromObject(live); }
            catch { return new JValue(live.ToString()); }
        }
    }
}
