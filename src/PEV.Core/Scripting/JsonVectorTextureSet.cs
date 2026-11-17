using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PEV.Scripting
{
    // A vector program receives only an immutable package-provided JSON value.
    internal sealed class JsonVectorTextureSet : IDisposable
    {
        private readonly VectorWorker<string> _worker;
        private readonly VectorTextureStorage _storage;
        private JToken _request;
        private string _json;
        internal JsonVectorTextureSet(string source, VectorTextureDefinition definition)
        {
            _storage = new VectorTextureStorage(definition);
            _worker = new VectorWorker<string>(source, value => value,
                (a, b) => string.Equals(a, b, StringComparison.Ordinal),
                (width, height, aspect, value) => JsonConvert.SerializeObject(new
                { width, height, aspect, request = JToken.Parse(value) }));
        }
        internal bool Update(int width, int height, float aspect, JToken request,
            Action<string, string, Texture, string, RenderTexture> transform)
        {
            if (request == null || request.Type == JTokenType.Null) throw new ArgumentException("Missing vector request");
            if (!ReferenceEquals(_request, request))
            {
                string json = request.ToString(Formatting.None);
                if (json.Length > 130000) throw new ArgumentException("Vector request exceeds the limit");
                _request = request; _json = json;
            }
            bool updated = _worker.Update(width, height, aspect, _json);
            if (_worker.LastError != null) throw new InvalidOperationException("Vector texture worker failed", _worker.LastError);
            return _storage.Update(width, height, aspect, updated, _worker.Latest, _worker.LatestWidth,
                _worker.LatestHeight, _worker.LatestAspect, transform);
        }
        internal Texture Get(string name) => _storage.Get(name);
        public void Dispose() { _worker.Dispose(); _storage.Dispose(); _request = null; _json = null; }
    }
}
