using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PEV.Model
{
    /// <summary>
    /// Declaration for a package-owned full-screen post-processing stage.
    ///
    /// Core supplies the render hook and the uniform transport; the shader and every value
    /// that goes into it belong to the package. Keeping the declaration this small is
    /// deliberate: the moment Core names a grading parameter it has to be recompiled to add
    /// one, and the effect stops being an event package.
    /// </summary>
    public sealed class ScreenEffectSpec
    {
        private static readonly Regex Identifier = new Regex("^[A-Za-z_][A-Za-z0-9_]{0,63}$",
            RegexOptions.CultureInvariant);

        /// <summary>Key into the package's <c>shaderAssets</c> map.</summary>
        public string ShaderRole;

        /// <summary>Which pass of the shader to use. Almost always zero.</summary>
        public int Pass;

        public static ScreenEffectSpec Parse(JToken token, out string error)
        {
            error = null;
            if (!(token is JObject spec))
            {
                error = "screenEffect must be an object";
                return null;
            }
            foreach (var property in spec.Properties())
                if (property.Name != "shader" && property.Name != "pass")
                {
                    error = "unknown screenEffect field '" + property.Name + "'";
                    return null;
                }

            var result = new ScreenEffectSpec();
            var shader = spec["shader"];
            if (shader == null || shader.Type != JTokenType.String)
            {
                error = "screenEffect requires a shader role name";
                return null;
            }
            result.ShaderRole = shader.Value<string>();
            if (string.IsNullOrEmpty(result.ShaderRole) || !Identifier.IsMatch(result.ShaderRole) ||
                result.ShaderRole == "__proto__" || result.ShaderRole == "constructor" ||
                result.ShaderRole == "prototype")
            {
                error = "invalid screenEffect shader role";
                return null;
            }

            if (spec["pass"] != null)
            {
                if (spec["pass"].Type != JTokenType.Integer)
                {
                    error = "screenEffect pass must be an integer";
                    return null;
                }
                result.Pass = spec.Value<int>("pass");
                if (result.Pass < 0 || result.Pass > 8)
                {
                    error = "screenEffect pass must be between 0 and 8";
                    return null;
                }
            }
            return result;
        }
    }
}
