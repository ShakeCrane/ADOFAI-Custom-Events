using System;
using System.Collections.Generic;
using UnityEngine;

namespace PEV.Scripting
{
    internal struct ViewportPoint { internal long Id; internal Vector2 Position; }

    // Generic game input: callers decide how a live planet position is used.
    internal static class LivePlanetPositions
    {
        private static readonly Comparison<ViewportPoint> CompareIds = (left, right) => left.Id.CompareTo(right.Id);
        internal static void Collect(Camera camera, List<ViewportPoint> points)
        {
            points.Clear();
            if (camera == null || ADOBase.playerManager == null || ADOBase.playerManager.players == null) return;
            foreach (var player in ADOBase.playerManager.players)
            {
                if (player == null || !player.alive || player.planetarySystem == null ||
                    player.planetarySystem.planetList == null) continue;
                foreach (var planet in player.planetarySystem.planetList)
                {
                    if (planet == null || !planet.isActiveAndEnabled || planet.dummyPlanets || planet.toDelete) continue;
                    Vector3 uv = camera.WorldToViewportPoint(planet.transform.position);
                    // Orthographic editor cameras may put planets on the camera's depth plane.
                    if ((!camera.orthographic && uv.z <= 0) || float.IsNaN(uv.x) || float.IsInfinity(uv.x) ||
                        float.IsNaN(uv.y) || float.IsInfinity(uv.y)) continue;
                    points.Add(new ViewportPoint { Id = planet.GetInstanceID(), Position = new Vector2(uv.x, uv.y) });
                }
            }
            points.Sort(CompareIds);
        }
    }
}
