using System;
using ADOFAI;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Reads geometry off a live native decoration.
    ///
    /// This is what makes the event behave like a decoration rather than merely look like
    /// one. A noise field is stored as a real AddDecoration row, so the engine's own tweens
    /// write into scrDecoration's fields — pivotPosVec, scaleVec, rotAngle, color, opacity.
    /// Reading them every frame means native MoveDecorations, editor dragging and the
    /// transform gizmo all drive the effect with no code of ours in the path.
    ///
    /// Reading the LevelEvent's dictionary instead would show the value from the last save:
    /// the tween lives on the component, not in the event.
    /// </summary>
    public static class DecorationBridge
    {
        /// <summary>
        /// Tiles a field covers at 100% decoration scale. ScrDecoration sizes itself from its
        /// sprite, and ours has none — the marker image deliberately does not exist — so the
        /// game falls back to a unit-sized box. This constant is therefore the scale-to-tiles
        /// mapping, and the default scale (100) reads as 4x3 tiles: one screen-ish region,
        /// which is what the reference effect covers.
        /// </summary>
        public static readonly Vector2 BaseSizeTiles = new Vector2(4f, 3f);

        /// <summary>The live component for this event, or null if it has not been built.</summary>
        public static scrDecoration Find(LevelEvent ev)
        {
            if (ev == null) return null;
            try { return scrDecorationManager.GetDecoration(ev); }
            catch { return null; }
        }

        /// <summary>
        /// The decoration's current frame in world space. Values come from the component,
        /// so they already include whatever a MoveDecorations tween or an editor drag did.
        /// </summary>
        public struct Live
        {
            public Placement.Frame Frame;
            public Color Color;
            public float Opacity;
            public bool Visible;
            public int Depth;
            public string Tag;
        }

        public static bool TryRead(LevelEvent ev, out Live live)
        {
            live = default(Live);
            var dec = Find(ev);
            if (dec == null) return false;

            try
            {
                float tile = ADOBase.controller != null ? ADOBase.controller.tileSize : 1f;

                // scaleVec is already a multiplier — 1.0 means 100%. The percentage in the
                // event data is converted by MoveDecorations before it ever reaches SetScale,
                // so dividing again here would shrink every field by two orders of magnitude.
                // A zero component would collapse the region to a line; treat it as 1.
                Vector2 scale = dec.scaleVec;
                if (Mathf.Abs(scale.x) < 1e-6f) scale.x = 1f;
                if (Mathf.Abs(scale.y) < 1e-6f) scale.y = 1f;

                // pivotPosVec is the pivot in world space and is exactly the value the
                // engine's tweens write, so it is the honest source for the centre.
                Vector2 pivot = dec.pivotPosVec;

                live.Frame = new Placement.Frame
                {
                    Center = new Vector3(pivot.x, pivot.y, 0f),
                    Size = new Vector2(
                        Mathf.Abs(scale.x) * BaseSizeTiles.x * tile,
                        Mathf.Abs(scale.y) * BaseSizeTiles.y * tile),
                    Rotation = dec.rotAngle,
                    Base = new Vector3(pivot.x, pivot.y, 0f)
                };

                live.Color = dec.color;
                live.Opacity = Mathf.Clamp01(dec.opacity);
                live.Visible = dec.GetVisible();
                live.Depth = ev.Get("depth", -1);
                live.Tag = ev.Get("tag", "") ?? "";
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Hex without '#', the form our own parameters use.</summary>
        public static string ToHex(Color c)
        {
            int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
            int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
            int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            return r.ToString("x2") + g.ToString("x2") + b.ToString("x2");
        }

        /// <summary>
        /// Geometry straight from the row's own data, for the moments when no component
        /// exists yet.
        ///
        /// scrDecorationManager builds its objects after the level decodes and rebuilds them
        /// on undo, on play and on reload; between those moments GetDecoration returns null.
        /// A field that vanished for a frame every time the author pressed undo would look
        /// broken, so the same numbers are read from the event instead. The fallback is
        /// deliberately the same computation the engine performs on those fields, so the
        /// two paths agree and a switch between them is invisible.
        /// </summary>
        public static bool TryReadEvent(LevelEvent ev, out Live live)
        {
            live = default(Live);
            if (ev == null) return false;

            try
            {
                float tile = ADOBase.controller != null ? ADOBase.controller.tileSize : 1f;

                // Decoration scale is a percentage, so the neutral value is 100, not 1.
                Vector2 scale = ev.Get("scale", new Vector2(100f, 100f));
                if (Mathf.Abs(scale.x) < 1e-6f) scale.x = 100f;
                if (Mathf.Abs(scale.y) < 1e-6f) scale.y = 100f;

                Vector2 position = ev.Get("position", Vector2.zero);
                bool onTile = IsTileRelative(ev);
                int floor = ev.floor;

                Vector3 centre = new Vector3(position.x * tile, position.y * tile, 0f);
                if (onTile && floor >= 0)
                {
                    var floors = ADOBase.lm != null ? ADOBase.lm.listFloors : null;
                    if (floors != null && floor < floors.Count)
                    {
                        var t = floors[floor].transform;
                        // Tile-relative decorations rotate their offset with the tile, which
                        // is why a rotated path still puts squares where the author aimed.
                        centre = t.position + t.rotation * new Vector3(position.x * tile, position.y * tile, 0f);
                    }
                }

                live.Frame = new Placement.Frame
                {
                    Center = centre,
                    Size = new Vector2(
                        Mathf.Abs(scale.x) / 100f * BaseSizeTiles.x * tile,
                        Mathf.Abs(scale.y) / 100f * BaseSizeTiles.y * tile),
                    Rotation = ev.Get("rotation", 0f),
                    Base = centre
                };

                float r = ev.Get("opacity", 100f);
                live.Color = ev.Get("color", Color.white);
                live.Opacity = Mathf.Clamp01(r / 100f);
                live.Visible = ev.Get("visible", true);
                live.Depth = ev.Get("depth", -1);
                live.Tag = ev.Get("tag", "") ?? "";
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Is this row positioned relative to a tile, or to the camera?
        ///
        /// The property is an enum in the game's metadata but may arrive as either a name or
        /// a number depending on which path decoded it, so both are accepted. "Camera" and
        /// "LastPosition" are the non-tile cases.
        /// </summary>
        private static bool IsTileRelative(LevelEvent ev)
        {
            object raw;
            try { raw = ev.Get<object>("relativeTo"); }
            catch { return true; }
            if (raw == null) return true;

            string name = raw.ToString();
            return name.IndexOf("Camera", StringComparison.OrdinalIgnoreCase) < 0
                   && name.IndexOf("LastPosition", StringComparison.OrdinalIgnoreCase) < 0;
        }
    }
}
