using System.Text;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Owns the objects scripts spawn.
    ///
    /// Deliberately no dedicated camera and no dedicated layer. An extra camera with its
    /// own culling mask looked tidy, but it has to track the gameplay camera exactly; any
    /// frame it fell behind, every effect became invisible with no error anywhere. The
    /// game's gameplay camera already renders last in its stack, so layer 0 plus a high
    /// sorting order is both simpler and more reliable.
    /// </summary>
    public static class EffectCanvas
    {
        private const string RootName = "PEV.Effects";

        private static Transform _root;

        public static Transform Root
        {
            get
            {
                Ensure();
                return _root;
            }
        }

        private static void Ensure()
        {
            if (_root != null) return;

            var existing = GameObject.Find(RootName);
            if (existing != null)
            {
                _root = existing.transform;
                return;
            }

            var go = new GameObject(RootName);
            Object.DontDestroyOnLoad(go);
            _root = go.transform;
        }

        /// <summary>Creates an effect object under the root, on the default layer.</summary>
        public static GameObject CreateObject(string name)
        {
            var go = new GameObject(name);
            go.layer = 0;
            go.transform.SetParent(Root, false);
            return go;
        }

        public static void DestroyAll()
        {
            if (_root == null) return;
            for (int i = _root.childCount - 1; i >= 0; i--)
                Object.Destroy(_root.GetChild(i).gameObject);
        }

        /// <summary>Diagnostic: which cameras render, and are they enabled.</summary>
        public static string Describe()
        {
            var sb = new StringBuilder();
            var all = Camera.allCameras;
            sb.Append($"cameras={all?.Length ?? 0}");
            if (all != null)
            {
                foreach (var c in all)
                    sb.Append($" [{c.name} d={c.depth} m={c.cullingMask} en={c.enabled} " +
                              $"pos={c.transform.position}]");
            }
            return sb.ToString();
        }
    }
}
