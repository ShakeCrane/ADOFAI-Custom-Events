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

            /// <summary>
            /// The sorting the carrier actually renders at.
            ///
            /// This is the whole of "obey the native depth": scrVisualDecoration.SetDepth
            /// does not store a number the engine later interprets — it writes the sorting
            /// layer name and sortingOrder onto the renderer, mapping depth ≥ 0 to "Bg" at
            /// order -depth and depth &lt; 0 to "Default" at order -depth. A field that
            /// draws at 900 + depth×10 is on neither layer, so it sits in front of the track
            /// regardless of its depth. Reading these two values is what keeps it behind
            /// exactly what it should be behind.
            /// </summary>
            public int SortingLayerID;
            public int SortingOrder;

            /// <summary>
            /// The GameObject layer the carrier is on. SetDepth assigns 9 for depth ≥ 0 and 7
            /// below, and the camera culls on this — a field drawn on layer 0 can appear in
            /// front of a background the engine would have hidden.
            /// </summary>
            public int UnityLayer;

            /// <summary>
            /// False for a decoration the author scaled to nothing.
            ///
            /// The engine does not draw a zero-sized sprite, so the field must not either.
            /// It is kept in the table rather than dropped: a MoveDecorations tween that
            /// scales it back up must bring it straight back, and a zero scale read on one
            /// frame is not the same thing as the row being deleted.
            /// </summary>
            public bool Renderable;
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
                // A zero component means the author collapsed the region; it is not replaced
                // with 1. The engine draws nothing at that size, and a field that stayed
                // visible through a zero scale would be a decoration that ignores its own
                // transform — the one thing the whole decoration mapping exists to prevent.
                Vector2 scale = dec.scaleVec;
                bool collapsed = Mathf.Abs(scale.x) < 1e-4f || Mathf.Abs(scale.y) < 1e-4f;

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
                live.Renderable = !collapsed;

                // The carrier's own renderer answers both, so syncFloorDepth is honoured for
                // free: SetDepth took that branch and wrote the floor's layer and order here.
                // Only the visual subclass carries a SpriteRenderer; a base scrDecoration
                // would have no sprite to read, and the depth mapping stands in for it.
                var sr = (dec as scrVisualDecoration)?.spriteRenderer;
                if (sr != null)
                {
                    live.SortingLayerID = sr.sortingLayerID;
                    live.SortingOrder = sr.sortingOrder;
                    // SetDepth writes the renderer child's GameObject, not the parent
                    // scrDecoration object (which commonly stays on layer 0).
                    live.UnityLayer = sr.gameObject.layer;
                }
                else
                {
                    NativeSorting(live.Depth, out live.SortingLayerID, out live.SortingOrder, out live.UnityLayer);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// scrVisualDecoration.SetDepth's own mapping, for the moments no component exists.
        ///
        /// Copied deliberately rather than inferred: depth ≥ 0 lands on the "Bg" layer at
        /// sortingOrder -depth, and depth &lt; 0 on "Default" at the same -depth. Note the
        /// sign — a LARGER depth sorts lower, so depth 5 draws behind depth 2. A field that
        /// sorts at 900 + depth×10 inverts that and draws in front of the track.
        ///
        /// syncFloorDepth is not reproduced here: it depends on the parent floor, which the
        /// row alone does not identify. When a component exists, its renderer is read directly
        /// and needs none of this.
        /// </summary>
        private static void NativeSorting(int depth, out int sortingLayerID, out int sortingOrder)
        {
            NativeSorting(depth, out sortingLayerID, out sortingOrder, out _);
        }

        /// <summary>
        /// The same mapping including the GameObject layer, which the camera culls on.
        ///
        /// Depth ≥ 0 puts the carrier on layer 9 and depth &lt; 0 on layer 7 — "Bg" and
        /// "Default" in the game's own vocabulary. A field drawn on layer 0 would survive a
        /// camera whose culling mask excludes the background, which is a different picture from
        /// the one the decoration itself would make.
        /// </summary>
        public static void NativeSorting(int depth, out int sortingLayerID, out int sortingOrder,
            out int unityLayer)
        {
            bool background = depth >= 0;
            sortingLayerID = SortingLayer.NameToID(background ? "Bg" : "Default");
            sortingOrder = -depth;
            unityLayer = background ? 9 : 7;
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

                // Decoration scale is a percentage, so the neutral value is 100, not 1 — but
                // zero is zero. Replacing a collapsed axis with 100 here is what made a field
                // invisible for one frame reappear at full size while the component was being
                // rebuilt; the row said 0 and the fallback overrode it.
                Vector2 scale = ev.Get("scale", new Vector2(100f, 100f));
                bool collapsed = Mathf.Abs(scale.x) < 1e-2f || Mathf.Abs(scale.y) < 1e-2f;

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
                live.Renderable = !collapsed;
                NativeSorting(live.Depth, out live.SortingLayerID, out live.SortingOrder, out live.UnityLayer);
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
