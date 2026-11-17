using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PEV.Scripting
{
    // Package-owned mapping from vector outputs to texture resources and optional passes.
    internal sealed class VectorTextureDefinition
    {
        internal sealed class Output
        {
            internal string Name, Entry, Input, Target;
            internal FilterMode Filter;
            internal TextureWrapMode Wrap;
            internal bool Linear;
        }
        internal readonly List<Output> Outputs = new List<Output>();
        internal readonly Dictionary<string, string> Roles = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Regex Identifier = new Regex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);
        private static string Name(JToken token)
        {
            string value = token?.Type == JTokenType.String ? token.Value<string>() : null;
            if (value == null || !Identifier.IsMatch(value) || value == "__proto__" || value == "constructor" || value == "prototype")
                throw new ArgumentException("Invalid vector texture identifier");
            return value;
        }
        internal static VectorTextureDefinition Parse(byte[] bytes, GpuGraphDefinition graph)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 131072 || graph == null)
                throw new ArgumentException("Invalid vector texture resource");
            var root = JObject.Parse(new UTF8Encoding(false, true).GetString(bytes));
            if (!(root["vectorTextures"] is JObject spec) || !(spec["outputs"] is JArray outputs) || outputs.Count < 1 || outputs.Count > 8 ||
                !(spec["roles"] is JObject roles) || roles.Count > 16)
                throw new ArgumentException("Invalid vector texture specification");
            var result = new VectorTextureDefinition();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in outputs)
            {
                if (!(token is JObject item) || item["linear"]?.Type != JTokenType.Boolean)
                    throw new ArgumentException("Invalid vector texture output");
                var output = new Output { Name = Name(item["name"]), Linear = item.Value<bool>("linear") };
                if (!names.Add(output.Name)) throw new ArgumentException("Duplicate vector texture output");
                switch (item.Value<string>("filter"))
                {
                    case "point": output.Filter = FilterMode.Point; break;
                    case "linear": output.Filter = FilterMode.Bilinear; break;
                    default: throw new ArgumentException("Invalid vector texture filter");
                }
                switch (item.Value<string>("wrap"))
                {
                    case "clamp": output.Wrap = TextureWrapMode.Clamp; break;
                    case "repeat": output.Wrap = TextureWrapMode.Repeat; break;
                    case "mirror": output.Wrap = TextureWrapMode.Mirror; break;
                    default: throw new ArgumentException("Invalid vector texture wrap");
                }
                if (item["transform"] != null)
                {
                    if (!(item["transform"] is JObject transform)) throw new ArgumentException("Invalid vector texture transform");
                    output.Entry = Name(transform["entry"]); output.Input = Name(transform["input"]); output.Target = Name(transform["output"]);
                    if (!output.Linear || !graph.Entries.ContainsKey(output.Entry) || output.Input == output.Target)
                        throw new ArgumentException("Invalid vector texture transform bindings");
                }
                result.Outputs.Add(output);
            }
            foreach (var role in roles.Properties())
            {
                string name = Name(new JValue(role.Name)), target = Name(role.Value);
                if (!names.Contains(target)) throw new ArgumentException("Unknown vector texture role target");
                result.Roles.Add(name, target);
            }
            return result;
        }
        internal string Role(string role) => Roles.TryGetValue(role, out var name) ? name : throw new ArgumentException("Missing vector texture role");
    }
}
