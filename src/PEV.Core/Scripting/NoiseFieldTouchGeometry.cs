using System;
using System.Collections.Generic;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>Continuous rectangle hit tests; never uses the animated noise boundary.</summary>
    internal static class NoiseFieldTouchGeometry
    {
        internal struct Area
        {
            public Vector2 Center, Size;
            public float RotationDegrees;
            public bool Inverted, Active, Visible;
        }

        internal static bool Contains(IReadOnlyList<Area> areas, Vector2 point, float inset = 0f)
        {
            if (areas == null || !Finite(point.x) || !Finite(point.y) || !Finite(inset) || inset < 0f) return false;
            bool normal = false, normalInset = false;
            int subtract = 0, subtractInset = 0;
            for (int i = 0; i < areas.Count; i++)
            {
                Area area = areas[i];
                if (!area.Active || !area.Visible || !Finite(area.Center.x) || !Finite(area.Center.y) ||
                    !Finite(area.Size.x) || !Finite(area.Size.y) || !Finite(area.RotationDegrees)) continue;
                float halfX = Mathf.Abs(area.Size.x) * 0.5f, halfY = Mathf.Abs(area.Size.y) * 0.5f;
                if (halfX <= 0f || halfY <= 0f) continue;
                float angle = -area.RotationDegrees * Mathf.Deg2Rad;
                float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
                Vector2 delta = point - area.Center;
                float x = Mathf.Abs(delta.x * cos - delta.y * sin);
                float y = Mathf.Abs(delta.x * sin + delta.y * cos);
                bool inside = x <= halfX && y <= halfY;
                float adjustment = area.Inverted ? inset : -inset;
                bool insideInset = x <= Mathf.Max(0f, halfX + adjustment) && y <= Mathf.Max(0f, halfY + adjustment);
                if (area.Inverted)
                {
                    if (inside) subtract++;
                    if (insideInset) subtractInset++;
                }
                else
                {
                    normal |= inside;
                    normalInset |= insideInset;
                }
            }
            // Reference input parity applies separately to the original and inset regions.
            return (normal != ((subtract & 1) != 0)) && (normalInset != ((subtractInset & 1) != 0));
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
