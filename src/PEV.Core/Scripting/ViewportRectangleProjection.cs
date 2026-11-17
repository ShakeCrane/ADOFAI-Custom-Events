using UnityEngine;

namespace PEV.Scripting
{
    // Project a world rectangle into normalized viewport coordinates, with independent
    // coordinate divisors for consumers that use non-square coordinate systems.
    internal static class ViewportRectangleProjection
    {
        internal struct Rectangle
        {
            internal Vector2 Center, HalfSize;
            internal float AngleRadians;
        }
        internal static bool TryProject(Camera camera, Placement.Frame frame, Vector2 divisors, out Rectangle rectangle)
        {
            rectangle = default(Rectangle);
            if (camera == null || !camera.orthographic || divisors.x <= 0f || divisors.y <= 0f ||
                !Finite(divisors.x) || !Finite(divisors.y) || frame.Size.x == 0f || frame.Size.y == 0f) return false;
            float radians = frame.Rotation * Mathf.Deg2Rad;
            var axis = new Vector3(Mathf.Cos(radians), Mathf.Sin(radians), 0f);
            var perpendicular = new Vector3(-axis.y, axis.x, 0f);
            Vector3 center = camera.WorldToViewportPoint(frame.Center);
            Vector3 x = camera.WorldToViewportPoint(frame.Center + axis * (Mathf.Abs(frame.Size.x) * 0.5f));
            Vector3 y = camera.WorldToViewportPoint(frame.Center + perpendicular * (Mathf.Abs(frame.Size.y) * 0.5f));
            // The editor's orthographic camera can place a rectangle on the camera plane.
            if (!Finite(center.z) || !Finite(x.z) || !Finite(y.z)) return false;
            Vector2 projectedCenter = new Vector2((center.x * 2f - 1f) / divisors.x, (center.y * 2f - 1f) / divisors.y);
            Vector2 xAxis = new Vector2((x.x - center.x) * 2f / divisors.x, (x.y - center.y) * 2f / divisors.y);
            Vector2 yAxis = new Vector2((y.x - center.x) * 2f / divisors.x, (y.y - center.y) * 2f / divisors.y);
            float halfX = xAxis.magnitude, halfY = yAxis.magnitude;
            if (!Finite(projectedCenter.x) || !Finite(projectedCenter.y) || !Finite(halfX) || !Finite(halfY) ||
                halfX <= 0f || halfY <= 0f) return false;
            // A center, size and rotation cannot represent a sheared rectangle.
            if (Mathf.Abs(Vector2.Dot(xAxis, yAxis)) > halfX * halfY * 0.0001f) return false;
            rectangle.Center = projectedCenter;
            rectangle.HalfSize = new Vector2(halfX, halfY);
            rectangle.AngleRadians = Mathf.Atan2(xAxis.y, xAxis.x);
            return true;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
