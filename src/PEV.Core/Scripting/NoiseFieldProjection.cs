using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>Converts a world-space rectangle to the reference chart coordinate system.</summary>
    internal static class NoiseFieldProjection
    {
        internal static bool TryProject(Camera camera, Placement.Frame frame, float aspect,
            out NoiseFieldGpuMasks.Area area)
        {
            area = default(NoiseFieldGpuMasks.Area);
            if (camera == null || !camera.orthographic || aspect <= 0f ||
                float.IsNaN(aspect) || float.IsInfinity(aspect) ||
                frame.Size.x == 0f || frame.Size.y == 0f) return false;
            float radians = frame.Rotation * Mathf.Deg2Rad;
            var axis = new Vector3(Mathf.Cos(radians), Mathf.Sin(radians), 0f);
            var perpendicular = new Vector3(-axis.y, axis.x, 0f);
            Vector3 center = camera.WorldToViewportPoint(frame.Center);
            Vector3 x = camera.WorldToViewportPoint(frame.Center + axis * (Mathf.Abs(frame.Size.x) * 0.5f));
            Vector3 y = camera.WorldToViewportPoint(frame.Center + perpendicular * (Mathf.Abs(frame.Size.y) * 0.5f));
            // Orthographic XY projection is valid on the camera plane as well. ADOFAI's
            // editor uses that configuration, so rejecting z == 0 dropped every field.
            if (!Finite(center.z) || !Finite(x.z) || !Finite(y.z)) return false;
            Vector2 centerChart = new Vector2(center.x * 2f - 1f, (center.y * 2f - 1f) / aspect);
            Vector2 xAxis = new Vector2((x.x - center.x) * 2f, (x.y - center.y) * 2f / aspect);
            Vector2 yAxis = new Vector2((y.x - center.x) * 2f, (y.y - center.y) * 2f / aspect);
            float halfX = xAxis.magnitude, halfY = yAxis.magnitude;
            if (!Finite(centerChart.x) || !Finite(centerChart.y) || !Finite(halfX) || !Finite(halfY) ||
                halfX <= 0f || halfY <= 0f) return false;
            // This representation cannot describe a sheared quad. Reject it rather than
            // quietly producing a wrong mask; non-orthographic cameras need a polygon path.
            if (Mathf.Abs(Vector2.Dot(xAxis, yAxis)) > halfX * halfY * 0.0001f) return false;
            area.Center = centerChart;
            area.HalfSize = new Vector2(halfX, halfY);
            area.AngleRadians = Mathf.Atan2(xAxis.y, xAxis.x);
            area.YScale = 1f;
            return true;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
