using ADOFAI;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Resolves the camera the game actually renders through.
    ///
    /// Camera.main returns null here: ADOFAI does not tag its gameplay camera with the
    /// MainCamera tag, it keeps it on scrCamera. Every effect that used Camera.main was
    /// silently placing its objects at the world origin — the numbers were right, the
    /// polygons were somewhere off screen, and nothing said so.
    /// </summary>
    public static class GameCamera
    {
        private static Camera _cached;

        /// <summary>The gameplay camera, or null if none exists yet.</summary>
        public static Camera Current
        {
            get
            {
                // The gameplay camera is owned by scrCamera. Querying the live instance
                // rather than caching at load time keeps this correct across scene changes.
                try
                {
                    var scrCameraInstance = scrCamera.instance;
                    if (scrCameraInstance != null && scrCameraInstance.camobj != null)
                    {
                        _cached = scrCameraInstance.camobj;
                        return _cached;
                    }
                }
                catch { }

                if (_cached != null && _cached.isActiveAndEnabled) return _cached;

                // Fall back for contexts where scrCamera is not present (menus, tests).
                if (Camera.main != null)
                {
                    _cached = Camera.main;
                    return _cached;
                }

                var all = Camera.allCameras;
                if (all != null && all.Length > 0)
                {
                    _cached = all[0];
                    return _cached;
                }

                return null;
            }
        }

        public static void Invalidate() => _cached = null;

        /// <summary>Half-height and half-width of the view, in world units.</summary>
        public static bool TryGetHalfExtents(out float halfHeight, out float halfWidth)
        {
            halfHeight = halfWidth = 0f;
            var cam = Current;
            if (cam == null) return false;

            if (cam.orthographic)
            {
                halfHeight = cam.orthographicSize;
            }
            else
            {
                halfHeight = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            }
            halfWidth = halfHeight * cam.aspect;
            return true;
        }

        /// <summary>
        /// Screen-relative position in front of the camera.
        /// x/y are normalised: 1.0 is half the viewport. depth is the distance along forward.
        /// </summary>
        public static Vector3 ScreenOffset(float x, float y, float depth = 0.5f)
        {
            var cam = Current;
            if (cam == null) return new Vector3(x, y, 0f);

            if (!TryGetHalfExtents(out float halfHeight, out float halfWidth))
                return cam.transform.position + cam.transform.forward * depth;

            return cam.transform.position
                   + cam.transform.right * (x * halfWidth)
                   + cam.transform.up * (y * halfHeight)
                   + cam.transform.forward * depth;
        }
    }
}
