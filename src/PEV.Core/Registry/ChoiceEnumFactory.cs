using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PEV.Model;

namespace PEV.Registry
{
    /// <summary>
    /// Native panels require CLR enums. Generate data-only enums from validated package
    /// choices; no package-supplied code runs and JS still has no CLR access.
    /// </summary>
    internal static class ChoiceEnumFactory
    {
        private static ModuleBuilder _module;
        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.Ordinal);

        internal static void Prepare(EventManifest manifest, ParamSpec spec)
        {
            if (spec == null || spec.ResolvedType != null) return;
            if (spec.Type != "Choice")
            {
                // A previously installed package may reference an enum that no longer
                // exists. Its own declared option labels can supply a generic, data-only
                // replacement without a hard-coded alias for any particular event.
                if (string.IsNullOrEmpty(spec.Type) || !spec.Type.StartsWith("Enum:", StringComparison.Ordinal) ||
                    !spec.Type.Contains(",") || spec.OptionLabels == null || spec.OptionLabels.Count == 0) return;
                try { if (Type.GetType(spec.Type.Substring(5).Trim(), false) != null) return; }
                catch { return; }
                var options = new List<string>();
                foreach (var option in spec.OptionLabels)
                {
                    if (string.IsNullOrEmpty(option.Key) || option.Key == "value__" ||
                        !Regex.IsMatch(option.Key, "^[A-Za-z_][A-Za-z0-9_]{0,63}$") || options.Contains(option.Key)) return;
                    options.Add(option.Key);
                }
                if (options.Count > 64 || !(spec.Default is string defaultValue) || !options.Contains(defaultValue)) return;
                spec.Options = options;
            }
            if (manifest == null || spec.Options == null || spec.Options.Count == 0)
                throw new ArgumentException("Choice was not validated");
            string identity = manifest.Id.Length + ":" + manifest.Id + spec.Name.Length + ":" + spec.Name +
                "\n" + string.Join("\n", spec.Options);
            if (!Types.TryGetValue(identity, out var type))
            {
                if (_module == null)
                {
                    var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
                        new AssemblyName("PEV.DynamicChoices"), AssemblyBuilderAccess.Run);
                    _module = assembly.DefineDynamicModule("PEV.DynamicChoices");
                }
                byte[] digest;
                using (var sha = SHA256.Create()) digest = sha.ComputeHash(Encoding.UTF8.GetBytes(identity));
                var name = new StringBuilder("PEV.DynamicChoices.C_");
                foreach (byte b in digest) name.Append(b.ToString("x2"));
                var builder = _module.DefineEnum(name.ToString(), TypeAttributes.Public, typeof(int));
                for (int i = 0; i < spec.Options.Count; i++) builder.DefineLiteral(spec.Options[i], i);
                type = builder.CreateType();
                Types.Add(identity, type);
            }
            spec.ResolvedType = "Enum:" + type.AssemblyQualifiedName;
        }
    }
}
