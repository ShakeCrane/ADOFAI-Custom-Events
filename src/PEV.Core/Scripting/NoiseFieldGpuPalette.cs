using System;
using PEV.Loading;
using PEV.Model;
using UnityEngine;

namespace PEV.Scripting
{
    // Transitional adapter between field color records and generic vector textures.
    internal sealed class NoiseFieldGpuPalette : IDisposable
    {
        private readonly EventManifest _provider;
        private readonly VectorTextureDefinition _definition;
        private readonly string _primary, _secondary;
        private readonly int _defaultRgb;
        private VectorTextureSet<NoiseFieldColorOwnership.Zone, int> _textures;

        internal Texture Active => _textures?.Get(_primary);
        internal Texture Disabled => _textures?.Get(_secondary);

        internal NoiseFieldGpuPalette(EventManifest provider, VectorTextureDefinition definition)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _primary = definition.Role("primary"); _secondary = definition.Role("secondary");
            _defaultRgb = int.TryParse(provider.DecorationDefaultColor, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int rgb) ? rgb : 0xffffff;
        }
        internal bool Update(NoiseFieldGpuMasks masks, float aspect, NoiseFieldColorOwnership.Zone[] zones)
        {
            if (_textures == null)
            {
                if (!_provider.TryRuntimeResource("colorProgram", _provider.VectorPrograms, out var path))
                    throw new InvalidOperationException("Package color program is not declared");
                byte[] bytes = EventLoader.ReadResource(_provider.SourcePath, path, 262144);
                if (bytes == null) throw new InvalidOperationException("Package color program is missing or too large");
                _textures = new VectorTextureSet<NoiseFieldColorOwnership.Zone, int>(new System.Text.UTF8Encoding(false, true).GetString(bytes), _definition);
            }
            return _textures.Update(masks.Width, masks.Height, aspect, zones, _defaultRgb, masks.RunExternal);
        }
        public void Dispose() { _textures?.Dispose(); _textures = null; }
    }
}
