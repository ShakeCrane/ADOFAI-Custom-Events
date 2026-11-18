using System;
using Newtonsoft.Json.Linq;

namespace PEV.Storage
{
    /// <summary>
    /// Marks a native AddDecoration row as a PEV event carrier, and carries its payload.
    ///
    /// The row has to stay a genuine AddDecoration: vanilla decodes decorations by looking
    /// the eventType up in its own table, and an unknown type leaves `info` null, which
    /// throws on the very next line of LevelData.Decode. Keeping the type also buys the
    /// behaviour we want — scrDecorationManager indexes the row and builds a scrVisualDecoration
    /// for it, so MoveDecorations can find it by tag and the editor gizmo can drag it, with
    /// no code of ours in that path.
    ///
    /// Two fields carry the identity:
    ///   decorationImage = "pev/field"   the marker. Points at a file that does not exist,
    ///                                  so vanilla loads null, draws nothing, does not throw.
    ///   maskingTarget   = the payload    the only AddDecoration property that is both
    ///                                  hidden (control: Hidden) and encoded. Nothing reads
    ///                                  it while masking is off, and LevelEvent round-trips it.
    /// </summary>
    public static class DecorationCarrier
    {
        /// <summary>Marker image path. Not a real file, by design.</summary>
        public const string ImageMarker = "pev/field";

        /// <summary>Default for older packages without an explicit tag declaration.</summary>
        public const string DefaultTag = "pev";

        /// <summary>Prefix that tells our payload apart from a real masking target.</summary>
        public const string PayloadPrefix = "pev1:";

        /// <summary>
        /// The payload is the event's whole instance body, so a chart reopened anywhere keeps
        /// every parameter. Compact JSON: this string is written once per field, and charts
        /// can hold dozens.
        /// </summary>
        public static string Pack(EventInstanceData data)
        {
            if (data == null) return PayloadPrefix + "{}";

            var p = new JObject();
            foreach (var kv in data.Raw) p[kv.Key] = kv.Value;

            var root = new JObject
            {
                ["id"] = data.Id,
                ["v"] = data.Version,
                ["uid"] = data.Uid
            };
            if (p.Count > 0) root["p"] = p;
            return PayloadPrefix + root.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static bool TryUnpack(string maskedTarget, out EventInstanceData data)
        {
            data = null;
            if (string.IsNullOrEmpty(maskedTarget)) return false;
            if (!maskedTarget.StartsWith(PayloadPrefix, StringComparison.Ordinal)) return false;

            string body = maskedTarget.Substring(PayloadPrefix.Length).Trim();
            if (body.Length == 0) return false;

            try
            {
                var root = JObject.Parse(body);
                string id = root.Value<string>("id");
                if (string.IsNullOrWhiteSpace(id)) return false;

                data = new EventInstanceData
                {
                    Id = id,
                    Version = root.Value<string>("v") ?? "1.0.0",
                    Uid = root.Value<string>("uid")
                };
                if (root["p"] is JObject p)
                    foreach (var prop in p.Properties())
                        data.Raw[prop.Name] = prop.Value;
                return true;
            }
            catch
            {
                // A truncated or corrupted payload is not fatal: the row still exists as a
                // plain decoration, and Restore falls back to the inline parameters.
                return false;
            }
        }

        public static bool IsOurs(string decorationImage)
        {
            return string.Equals(decorationImage, ImageMarker, StringComparison.OrdinalIgnoreCase);
        }
    }
}
