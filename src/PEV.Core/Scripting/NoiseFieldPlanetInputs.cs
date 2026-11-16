using System;
using System.Collections.Generic;
using UnityEngine;

namespace PEV.Scripting
{
    internal static class NoiseFieldPlanetInputs
    {
        private static readonly Comparison<NoiseFieldTouchHistory.Contact> CompareIds =
            (left, right) => left.Id.CompareTo(right.Id);
        /// <summary>
        /// Collects actual playable planets, not pooled unused planets or visual copies.
        /// Call on the main thread; stable Unity instance IDs survive list reordering.
        /// The caller owns the list and filters positions against composed field geometry.
        /// </summary>
        internal static void Collect(Camera camera, List<NoiseFieldTouchHistory.Contact> contacts)
        {
            contacts.Clear();
            if (camera == null || ADOBase.playerManager == null || ADOBase.playerManager.players == null) return;
            foreach (var player in ADOBase.playerManager.players)
            {
                if (player == null || !player.alive || player.planetarySystem == null ||
                    player.planetarySystem.planetList == null) continue;
                foreach (var planet in player.planetarySystem.planetList)
                {
                    if (planet == null || !planet.isActiveAndEnabled || planet.dummyPlanets || planet.toDelete) continue;
                    Vector3 uv = camera.WorldToViewportPoint(planet.transform.position);
                    // In ADOFAI's orthographic editor camera the planets can sit on
                    // the camera's depth plane (z == 0) while their screen XY is valid.
                    // Perspective projection still cannot use points behind the camera.
                    if ((!camera.orthographic && uv.z <= 0) || float.IsNaN(uv.x) || float.IsInfinity(uv.x) ||
                        float.IsNaN(uv.y) || float.IsInfinity(uv.y)) continue;
                    contacts.Add(new NoiseFieldTouchHistory.Contact
                    {
                        Id = planet.GetInstanceID(),
                        Position = new Vector2(uv.x, uv.y)
                    });
                }
            }
            contacts.Sort(CompareIds);
        }
    }
}
