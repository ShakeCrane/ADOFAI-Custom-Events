using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PEV.Scripting
{
    // Declarative material selection and input binding; shader code remains package-owned.
    internal sealed class CompositorDefinition
    {
        internal sealed class Binding
        {
            internal string Name, Type, Input;
            internal readonly Dictionary<string, bool> When = new Dictionary<string, bool>(StringComparer.Ordinal);
        }
        internal sealed class Variant
        {
            internal string Shader;
            internal readonly Dictionary<string, bool> When = new Dictionary<string, bool>(StringComparer.Ordinal);
        }
        internal sealed class Layer
        {
            internal string Name, Visible;
            internal int Order;
            internal readonly List<Variant> Variants = new List<Variant>();
            internal readonly List<Binding> Bindings = new List<Binding>();
        }
        internal readonly List<Binding> Bindings = new List<Binding>();
        internal readonly List<Layer> Layers = new List<Layer>();
        private static readonly Regex Identifier = new Regex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);
        private static string Name(JToken token)
        {
            string value = token?.Type == JTokenType.String ? token.Value<string>() : null;
            if (string.IsNullOrEmpty(value) || !Identifier.IsMatch(value) || value == "__proto__" || value == "constructor" || value == "prototype")
                throw new ArgumentException("Invalid compositor identifier");
            return value;
        }
        private static void Conditions(JToken token, Dictionary<string, bool> destination)
        {
            if (token == null) return;
            if (!(token is JObject conditions) || conditions.Count > 8) throw new ArgumentException("Invalid compositor conditions");
            foreach (var property in conditions.Properties())
            {
                if (!Identifier.IsMatch(property.Name) || property.Value.Type != JTokenType.Boolean)
                    throw new ArgumentException("Invalid compositor condition");
                destination.Add(property.Name, property.Value.Value<bool>());
            }
        }
        private static void BindingsFrom(JToken token, List<Binding> destination)
        {
            if (token == null) return;
            if (!(token is JArray bindings) || bindings.Count > 32) throw new ArgumentException("Invalid compositor bindings");
            foreach (var item in bindings)
            {
                if (!(item is JObject spec)) throw new ArgumentException("Invalid compositor binding");
                var binding = new Binding { Name = Name(spec["name"]), Input = Name(spec["input"]), Type = Name(spec["type"]) };
                if (binding.Type != "texture" && binding.Type != "vector" && binding.Type != "color" &&
                    binding.Type != "int" && binding.Type != "float" && binding.Type != "vectorArray")
                    throw new ArgumentException("Unsupported compositor binding type");
                Conditions(spec["when"], binding.When); destination.Add(binding);
            }
        }
        internal static CompositorDefinition Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 131072) throw new ArgumentException("Invalid compositor resource");
            var root = JObject.Parse(new UTF8Encoding(false, true).GetString(bytes));
            if (!(root["compositor"] is JObject definition) || !(definition["layers"] is JArray layers) || layers.Count < 1 || layers.Count > 4)
                throw new ArgumentException("Invalid compositor layers");
            var result = new CompositorDefinition();
            BindingsFrom(definition["bindings"], result.Bindings);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in layers)
            {
                if (!(item is JObject spec) || spec["order"]?.Type != JTokenType.Integer || !(spec["variants"] is JArray variants) ||
                    variants.Count < 1 || variants.Count > 16) throw new ArgumentException("Invalid compositor layer");
                var layer = new Layer { Name = Name(spec["name"]), Visible = Name(spec["visible"]), Order = spec.Value<int>("order") };
                if (!names.Add(layer.Name) || layer.Order < -128 || layer.Order > 128) throw new ArgumentException("Invalid compositor layer order");
                foreach (var variantSpec in variants)
                {
                    if (!(variantSpec is JObject variantObject)) throw new ArgumentException("Invalid compositor variant");
                    var variant = new Variant { Shader = Name(variantObject["shader"]) };
                    Conditions(variantObject["when"], variant.When); layer.Variants.Add(variant);
                }
                BindingsFrom(spec["bindings"], layer.Bindings); result.Layers.Add(layer);
            }
            return result;
        }
    }
}
