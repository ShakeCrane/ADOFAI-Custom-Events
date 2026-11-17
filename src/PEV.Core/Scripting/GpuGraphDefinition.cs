using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace PEV.Scripting
{
    /// <summary>Validated package-owned compute graph. It contains no executable CLR code.</summary>
    internal sealed class GpuGraphDefinition
    {
        internal sealed class Resource
        {
            internal string Name, CountInput;
            internal bool Buffer;
            internal int Stride;
            internal GraphicsFormat Format;
            internal FilterMode Filter;
            internal int BytesPerPixel;
            internal readonly Dictionary<string, (int Word, string Type)> Fields = new Dictionary<string, (int, string)>(StringComparer.Ordinal);
            internal int Field(string name, string type)
            {
                if (!Fields.TryGetValue(name, out var field) || field.Type != type)
                    throw new ArgumentException("Missing or incompatible compute buffer field");
                return field.Word;
            }
        }
        internal sealed class Uniform
        {
            internal string Name, Type, Input;
            internal int IntValue;
            internal float FloatValue;
        }
        internal sealed class ControllerLimits { internal int Capacity, MaxInputs; }
        internal sealed class Pass
        {
            internal string Kernel, When;
            internal readonly Dictionary<string, string> Textures = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> Buffers = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly List<Uniform> Uniforms = new List<Uniform>();
        }
        internal readonly Dictionary<string, Resource> Resources = new Dictionary<string, Resource>(StringComparer.Ordinal);
        internal readonly Dictionary<string, List<Pass>> Entries = new Dictionary<string, List<Pass>>(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> Outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, ControllerLimits> Controllers = new Dictionary<string, ControllerLimits>(StringComparer.Ordinal);
        internal readonly List<Uniform> Uniforms = new List<Uniform>();
        internal int Divisor, Multiplier;
        internal string ShaderRole;
        private static readonly Regex Identifier = new Regex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);
        private static bool ValidName(string value) => !string.IsNullOrEmpty(value) && Identifier.IsMatch(value) &&
            value != "__proto__" && value != "constructor" && value != "prototype";
        private static int Int(JToken value, int min, int max)
        {
            if (value == null || value.Type != JTokenType.Integer) throw new ArgumentException("invalid graph integer");
            long number = value.Value<long>();
            if (number < min || number > max) throw new ArgumentOutOfRangeException("graph integer");
            return (int)number;
        }
        private static string Name(JToken value)
        {
            string name = value?.Type == JTokenType.String ? value.Value<string>() : null;
            if (!ValidName(name)) throw new ArgumentException("invalid graph identifier");
            return name;
        }
        internal static GpuGraphDefinition Parse(byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > 131072) throw new ArgumentException("invalid compute graph size");
            JObject root;
            using (var reader = new JsonTextReader(new StringReader(new UTF8Encoding(false, true).GetString(data))) { MaxDepth = 32, DateParseHandling = DateParseHandling.None })
            {
                root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new ArgumentException("trailing compute graph data");
            }
            if (Int(root["version"], 1, 1) != 1 || !(root["resolution"] is JObject resolution))
                throw new ArgumentException("invalid compute graph version or resolution");
            var graph = new GpuGraphDefinition { Divisor = Int(resolution["divisor"], 1, 64), Multiplier = Int(resolution["multiplier"], 1, 8),
                ShaderRole = Name(root["shader"]) };
            if (!(root["resources"] is JArray resources) || resources.Count < 1 || resources.Count > 16)
                throw new ArgumentException("invalid compute graph resources");
            foreach (var token in resources)
            {
                if (!(token is JObject item)) throw new ArgumentException("invalid compute resource");
                var resource = new Resource { Name = Name(item["name"]) };
                string kind = item.Value<string>("kind");
                if (kind == "buffer")
                {
                    resource.Buffer = true; resource.Stride = Int(item["stride"], 4, 256);
                    if (item["countInput"] != null) resource.CountInput = Name(item["countInput"]);
                    if (resource.Stride % 4 != 0) throw new ArgumentException("compute buffer stride must be aligned");
                    if (item["fields"] != null)
                    {
                        if (!(item["fields"] is JArray fields) || fields.Count != resource.Stride / 4)
                            throw new ArgumentException("compute buffer fields must match its stride");
                        for (int i = 0; i < fields.Count; i++)
                        {
                            if (!(fields[i] is JObject field)) throw new ArgumentException("invalid compute buffer field");
                            string name = Name(field["name"]), type = field.Value<string>("type");
                            if (type != "float" && type != "uint") throw new ArgumentException("unsupported compute buffer field type");
                            resource.Fields.Add(name, (i, type));
                        }
                    }
                }
                else if (kind == "texture")
                {
                    switch (item.Value<string>("format"))
                    {
                        case "rgba32uint": resource.Format = GraphicsFormat.R32G32B32A32_UInt; resource.BytesPerPixel = 16; break;
                        case "r32uint": resource.Format = GraphicsFormat.R32_UInt; resource.BytesPerPixel = 4; break;
                        case "rgba8unorm": resource.Format = GraphicsFormat.R8G8B8A8_UNorm; resource.BytesPerPixel = 4; break;
                        default: throw new ArgumentException("unsupported compute texture format");
                    }
                    switch (item.Value<string>("filter"))
                    {
                        case "point": resource.Filter = FilterMode.Point; break;
                        case "linear": resource.Filter = FilterMode.Bilinear; break;
                        default: throw new ArgumentException("invalid compute texture filter");
                    }
                }
                else throw new ArgumentException("invalid compute resource kind");
                if (graph.Resources.ContainsKey(resource.Name)) throw new ArgumentException("duplicate compute resource");
                graph.Resources.Add(resource.Name, resource);
            }
            if (root["controllers"] != null)
            {
                if (!(root["controllers"] is JObject controllers) || controllers.Count > 16)
                    throw new ArgumentException("invalid compute graph controllers");
                foreach (var controller in controllers.Properties())
                {
                    if (!ValidName(controller.Name) || !(controller.Value is JObject spec)) throw new ArgumentException("invalid compute graph controller");
                    graph.Controllers.Add(controller.Name, new ControllerLimits { Capacity = Int(spec["capacity"], 1, 64),
                        MaxInputs = Int(spec["maxInputs"], 1, 4096) });
                }
            }
            ReadUniforms(root["uniforms"], graph.Uniforms);
            if (!(root["entries"] is JObject entries) || entries.Count < 1 || entries.Count > 8)
                throw new ArgumentException("invalid compute graph entries");
            int totalPasses = 0;
            foreach (var entry in entries.Properties())
            {
                if (!ValidName(entry.Name) || !(entry.Value is JArray nodes) || nodes.Count < 1 || nodes.Count > 32)
                    throw new ArgumentException("invalid compute graph entry");
                totalPasses += nodes.Count;
                if (totalPasses > 64) throw new ArgumentException("too many compute graph passes");
                var parsed = new List<Pass>(nodes.Count);
                foreach (var token in nodes)
                {
                    if (!(token is JObject node)) throw new ArgumentException("invalid compute pass");
                    var pass = new Pass { Kernel = Name(node["kernel"]) };
                    if (node["when"] != null) pass.When = Name(node["when"]);
                    ReadBindings(node["textures"], pass.Textures, graph.Resources, false);
                    ReadBindings(node["buffers"], pass.Buffers, graph.Resources, true);
                    ReadUniforms(node["uniforms"], pass.Uniforms);
                    parsed.Add(pass);
                }
                graph.Entries.Add(entry.Name, parsed);
            }
            if (!(root["outputs"] is JObject outputs) || outputs.Count < 1 || outputs.Count > 16)
                throw new ArgumentException("invalid compute graph outputs");
            foreach (var output in outputs.Properties())
            {
                if (!ValidName(output.Name)) throw new ArgumentException("invalid compute output");
                string target = Name(output.Value);
                if (!graph.Resources.TryGetValue(target, out var resource) || resource.Buffer)
                    throw new ArgumentException("compute output must name a texture");
                graph.Outputs.Add(output.Name, target);
            }
            return graph;
        }
        private static void ReadBindings(JToken token, Dictionary<string, string> result,
            Dictionary<string, Resource> resources, bool buffer)
        {
            if (token == null) return;
            if (!(token is JObject bindings) || bindings.Count > 16) throw new ArgumentException("invalid compute bindings");
            foreach (var binding in bindings.Properties())
            {
                if (!ValidName(binding.Name) || binding.Value.Type != JTokenType.String) throw new ArgumentException("invalid compute binding");
                string target = binding.Value.Value<string>();
                if (target.StartsWith("$", StringComparison.Ordinal))
                {
                    if (buffer || !ValidName(target.Substring(1))) throw new ArgumentException("invalid external compute texture");
                }
                else if (!resources.TryGetValue(target, out var resource) || resource.Buffer != buffer)
                    throw new ArgumentException("compute binding names an incompatible resource");
                result.Add(binding.Name, target);
            }
        }
        private static void ReadUniforms(JToken token, List<Uniform> result)
        {
            if (token == null) return;
            if (!(token is JObject uniforms) || uniforms.Count > 32) throw new ArgumentException("invalid compute uniforms");
            foreach (var property in uniforms.Properties())
            {
                if (!ValidName(property.Name) || !(property.Value is JObject spec)) throw new ArgumentException("invalid compute uniform");
                string type = spec.Value<string>("type");
                if (type != "int" && type != "float" && type != "int2") throw new ArgumentException("unsupported compute uniform type");
                bool fromInput = spec["input"] != null, constant = spec["value"] != null;
                if (fromInput == constant || (constant && type == "int2")) throw new ArgumentException("invalid compute uniform source");
                var uniform = new Uniform { Name = property.Name, Type = type };
                if (fromInput) uniform.Input = Name(spec["input"]);
                else if (type == "int") uniform.IntValue = Int(spec["value"], -1000000, 1000000);
                else
                {
                    if (spec["value"].Type != JTokenType.Float && spec["value"].Type != JTokenType.Integer) throw new ArgumentException("invalid compute float");
                    uniform.FloatValue = spec.Value<float>("value");
                    if (float.IsNaN(uniform.FloatValue) || float.IsInfinity(uniform.FloatValue) || Math.Abs(uniform.FloatValue) > 1000000f)
                        throw new ArgumentException("invalid compute float");
                }
                result.Add(uniform);
            }
        }
        internal void Size(int viewportWidth, int viewportHeight, out int width, out int height)
        {
            if (viewportWidth < 1 || viewportHeight < 1 || viewportWidth > 16384 || viewportHeight > 16384)
                throw new ArgumentOutOfRangeException("invalid compute viewport");
            width = Math.Max(1, viewportWidth / Divisor) * Multiplier;
            height = Math.Max(1, viewportHeight / Divisor) * Multiplier;
            if (width > 4096 || height > 4096 || (long)width * height > 1048576)
                throw new ArgumentOutOfRangeException("compute texture dimensions exceed the limit");
            long bytes = 0;
            foreach (var resource in Resources.Values) if (!resource.Buffer) bytes += (long)width * height * resource.BytesPerPixel;
            if (bytes > 128L * 1024 * 1024) throw new InvalidOperationException("compute texture budget exceeded");
        }
    }
}
