using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PEV.Storage
{
    /// <summary>
    /// The persisted shape of one placed custom event. Everything here is authoritative:
    /// raw holds every key the file contained, including ones the current manifest no
    /// longer declares. That is what makes "only grow" migration work.
    /// </summary>
    public class EventInstanceData
    {
        public const string Prefix = "@PEV1";

        public string Id;
        public string Version = "1.0.0";
        public string Uid;
        public Dictionary<string, JToken> Raw = new Dictionary<string, JToken>(StringComparer.Ordinal);

        /// <summary>
        /// Set when this instance stands in for a payload we could not resolve (missing
        /// package, newer major version, unreadable body). A placeholder is shown on the
        /// track so the author does not think their events vanished, but it is never
        /// executed and never rewritten: ToComment returns the original bytes verbatim.
        /// </summary>
        public bool IsPlaceholder;

        /// <summary>Why this payload could not be resolved. Shown in the editor.</summary>
        public string PlaceholderReason;

        /// <summary>Verbatim comment this placeholder stands for. Written back untouched.</summary>
        public string OriginalComment;

        /// <summary>Values as they should be shown in the editor (defaults filled in).</summary>
        public Dictionary<string, object> Effective { get; private set; } = new Dictionary<string, object>();

        public static bool LooksLikeOurs(string comment)
        {
            return comment != null && comment.StartsWith(Prefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Parses the comment body. Returns null when the payload is unreadable; callers
        /// must then leave the original comment untouched.
        /// </summary>
        public static EventInstanceData Parse(string comment, out string error)
        {
            error = null;
            if (!LooksLikeOurs(comment))
            {
                error = "not a PEV comment";
                return null;
            }
            string body = comment.Substring(Prefix.Length).Trim();
            if (body.Length == 0)
            {
                error = "empty payload";
                return null;
            }

            JObject root;
            try { root = JObject.Parse(body); }
            catch (Exception e) { error = "payload is not valid JSON: " + e.Message; return null; }

            var data = new EventInstanceData
            {
                Id = root.Value<string>("id"),
                Version = root.Value<string>("v") ?? "1.0.0",
                Uid = root.Value<string>("uid")
            };
            if (string.IsNullOrWhiteSpace(data.Id))
            {
                error = "payload has no id";
                return null;
            }
            if (root["p"] is JObject p)
                foreach (var prop in p.Properties())
                    data.Raw[prop.Name] = prop.Value;

            return data;
        }

        /// <summary>
        /// Fills in defaults for params the instance does not carry, without dropping
        /// anything the instance does carry.
        /// </summary>
        public void Materialize(IEnumerable<Model.ParamSpec> specs)
        {
            Effective = new Dictionary<string, object>(StringComparer.Ordinal);

            foreach (var spec in specs ?? Enumerable.Empty<Model.ParamSpec>())
            {
                if (Raw.TryGetValue(spec.Name, out var token))
                    Effective[spec.Name] = ToPlain(token);
                else if (spec.Default != null)
                    Effective[spec.Name] = spec.Default;
            }

            // Unknown-to-the-manifest keys are preserved verbatim but still readable.
            foreach (var kv in Raw)
                if (!Effective.ContainsKey(kv.Key))
                    Effective[kv.Key] = ToPlain(kv.Value);
        }

        /// <summary>
        /// Serializes back to a comment body. Every key in Raw is written, so an event
        /// authored against a newer manifest keeps its data when opened with an older one.
        /// </summary>
        public string ToComment()
        {
            // A placeholder stands for a payload we could not interpret. Rebuilding it from
            // Raw would silently normalise data we do not understand, so hand back the
            // original bytes untouched.
            if (IsPlaceholder && !string.IsNullOrEmpty(OriginalComment))
                return OriginalComment;

            var p = new JObject();
            foreach (var kv in Raw)
                p[kv.Key] = Canonicalize(kv.Value);

            var root = new JObject
            {
                ["id"] = Id,
                ["v"] = Version
            };
            if (!string.IsNullOrEmpty(Uid)) root["uid"] = Uid;
            root["p"] = p;

            return Prefix + " " + root.ToString(Formatting.None);
        }

        /// <summary>
        /// Deep copy with a fresh uid. A copied editor event must NOT share the original's
        /// uid: both would write the same comment on save and one would silently win.
        /// </summary>
        public EventInstanceData Clone(string newUid)
        {
            var copy = new EventInstanceData
            {
                Id = Id,
                Version = Version,
                Uid = newUid,
                IsPlaceholder = IsPlaceholder,
                PlaceholderReason = PlaceholderReason,
                OriginalComment = OriginalComment
            };
            foreach (var kv in Raw)
                copy.Raw[kv.Key] = kv.Value.DeepClone();
            copy.Effective = new Dictionary<string, object>(Effective, StringComparer.Ordinal);
            return copy;
        }

        /// <summary>
        /// Undo the editor's in-memory representation so the file carries the same shape
        /// the author wrote: enums as names, tiles/pairs/vectors as plain arrays.
        /// </summary>
        private static JToken Canonicalize(JToken token)
        {
            if (token == null) return JValue.CreateNull();
            if (token.Type != JTokenType.Object) return token;

            var obj = token as JObject;
            // Tuple<int, TileRelativeTo> and Tuple<float, float> both arrive as Item1/Item2.
            if (obj.Property("Item1") != null && obj.Property("Item2") != null)
            {
                return new JArray(Canonicalize(obj["Item1"]), Canonicalize(obj["Item2"]));
            }
            // UnityEngine.Vector2 serializes as x/y.
            if (obj.Property("x") != null && obj.Property("y") != null && obj.Count == 2)
            {
                return new JArray(Canonicalize(obj["x"]), Canonicalize(obj["y"]));
            }
            return token;
        }

        /// <summary>Updates a raw value, keeping it in both the raw and effective views.</summary>
        public void Set(string name, object value)
        {
            Raw[name] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
            Effective[name] = value;
        }

        public bool TryGet(string name, out object value) => Effective.TryGetValue(name, out value);

        public float GetFloat(string name, float fallback = 0f)
        {
            if (!Effective.TryGetValue(name, out var v) || v == null) return fallback;
            try { return Convert.ToSingle(v); } catch { return fallback; }
        }

        public string GetString(string name) =>
            Effective.TryGetValue(name, out var v) ? v?.ToString() : null;

        private static object ToPlain(JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.Float: return t.Value<float>();
                case JTokenType.Integer: return t.Value<int>();
                case JTokenType.Boolean: return t.Value<bool>();
                case JTokenType.String: return t.ToString();
                case JTokenType.Null: return null;
                default: return t.ToObject<object>();
            }
        }
    }
}
