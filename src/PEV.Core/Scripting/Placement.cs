using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Turns author-facing geometry (tiles, anchors, camera ratios) into a world-space
    /// centre, extent and rotation for a quad.
    ///
    /// Pure functions: the caller resolves the camera, the tile transform and the planet
    /// transform, so every conversion here can be reasoned about and tested in isolation.
    /// </summary>
    public static class Placement
    {
        /// <summary>1.0 in ADOFAI terms; the real value is read from scrController at runtime.</summary>
        public const float FallbackTileSize = 1f;

        /// <summary>scrCamera.DefaultCameraOrthoSize; used to cancel zoom for lockSize.</summary>
        public const float DefaultOrthoSize = 5f;

        public struct Frame
        {
            /// <summary>Quad centre in world space.</summary>
            public Vector3 Center;
            /// <summary>World-space size after tileSize, ratio and lockSize are applied.</summary>
            public Vector2 Size;
            /// <summary>Rotation in degrees, base rotation already combined in.</summary>
            public float Rotation;
            /// <summary>Anchor point, kept for diagnostics and for Move-style events later.</summary>
            public Vector3 Base;
        }

        public struct Input
        {
            public NoiseFieldRelativeTo RelativeTo;
            public NoiseFieldAnchor Anchor;
            public NoiseFieldShape Shape;

            /// <summary>size in tiles, or viewport fraction when RelativeTo == CameraRatio.</summary>
            public Vector2 Size;
            public float Radius;
            /// <summary>Offset in tiles (viewport fractions when CameraRatio).</summary>
            public Vector2 PositionOffset;
            public float Rotation;
            public bool LockRotation;
            public bool LockSize;
            public Vector2 Parallax;

            public float TileSize;
            /// <summary>Camera position and axes. Axes are normally the camera's transform.</summary>
            public Vector3 CameraPosition;
            public Vector3 CameraRight;
            public Vector3 CameraUp;
            public float HalfWidth;
            public float HalfHeight;
            /// <summary>Rotation (degrees around Z) of the reference object, e.g. the tile.</summary>
            public float BaseRotation;
            /// <summary>Anchor position in world space.</summary>
            public Vector3 BasePosition;
        }

        /// <summary>
        /// Computes the quad frame. size is in tiles for every reference except CameraRatio,
        /// where 1.0 spans the full viewport in the corresponding axis.
        /// </summary>
        public static Frame Compute(Input i)
        {
            float tile = i.TileSize > 1e-4f ? i.TileSize : FallbackTileSize;

            Vector2 world;
            if (i.RelativeTo == NoiseFieldRelativeTo.CameraRatio)
            {
                world = new Vector2(i.Size.x * 2f * i.HalfWidth, i.Size.y * 2f * i.HalfHeight);
            }
            else
            {
                if (i.Shape == NoiseFieldShape.Circle)
                {
                    float d = i.Radius * 2f * tile;
                    world = new Vector2(d, d);
                }
                else
                {
                    world = new Vector2(i.Size.x * tile, i.Size.y * tile);
                }
            }

            // lockSize keeps the region at a fixed fraction of the viewport as the camera
            // zooms. Zoom is expressed as orthoSize against the game's default 5.
            if (i.LockSize)
            {
                float zoom = i.HalfHeight / DefaultOrthoSize;
                if (zoom > 1e-4f) world *= zoom;
            }

            float w = Mathf.Max(0f, Mathf.Abs(world.x));
            float h = Mathf.Max(0f, Mathf.Abs(world.y));

            // Anchor semantics: the anchor point of the rectangle sits on BasePosition.
            // Anchor at top-left => rectangle centre is right and below the base point.
            Vector2 sign = AnchorSign(i.Anchor);

            float rot = i.LockRotation ? i.Rotation : i.Rotation + i.BaseRotation;
            Quaternion q = Quaternion.Euler(0f, 0f, rot);

            Vector3 right = i.Parallax.x == 1f && i.Parallax.y == 1f
                ? i.CameraRight
                : i.CameraRight;
            Vector3 up = i.CameraUp;

            Vector3 offset = new Vector3(i.PositionOffset.x * tile, i.PositionOffset.y * tile, 0f);
            offset = i.Parallax == Vector2.one
                ? offset
                : new Vector3(offset.x * i.Parallax.x, offset.y * i.Parallax.y, 0f);

            Vector3 quarter = q * new Vector3(sign.x * w * 0.5f, sign.y * h * 0.5f, 0f);
            Vector3 basePos = i.BasePosition + offset;

            Vector3 centre = basePos
                             + right * quarter.x
                             + up * quarter.y;

            return new Frame
            {
                Center = centre,
                Size = new Vector2(w, h),
                Rotation = rot,
                Base = basePos
            };
        }

        /// <summary>Quad scale that makes a unit sprite (pivot centre, PPU 100) cover Size.</summary>
        public static Vector3 ScaleFor(Vector2 size)
        {
            return new Vector3(size.x > 0f ? size.x : 0.0001f, size.y > 0f ? size.y : 0.0001f, 1f);
        }

        private static Vector2 AnchorSign(NoiseFieldAnchor anchor)
        {
            switch (anchor)
            {
                case NoiseFieldAnchor.TopLeft: return new Vector2(1f, -1f);
                case NoiseFieldAnchor.Top: return new Vector2(0f, -1f);
                case NoiseFieldAnchor.TopRight: return new Vector2(-1f, -1f);
                case NoiseFieldAnchor.Left: return new Vector2(1f, 0f);
                case NoiseFieldAnchor.Right: return new Vector2(-1f, 0f);
                case NoiseFieldAnchor.BottomLeft: return new Vector2(1f, 1f);
                case NoiseFieldAnchor.Bottom: return new Vector2(0f, 1f);
                case NoiseFieldAnchor.BottomRight: return new Vector2(-1f, 1f);
                default: return Vector2.zero;
            }
        }
    }
}
