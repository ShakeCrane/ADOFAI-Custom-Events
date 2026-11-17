using System;
using System.Collections.Generic;
using UnityEngine;

namespace PEV.Scripting
{
    // Collects intersecting bounding rectangles, including bridges between existing
    // groups. Callers decide how to render or otherwise consume each group.
    internal static class RectangleClusters
    {
        internal static List<List<T>> Group<T>(IEnumerable<T> items, Func<T, Rect> bounds)
        {
            if (items == null || bounds == null) throw new ArgumentNullException();
            var groups = new List<List<T>>();
            var boxes = new List<Rect>();
            foreach (var item in items)
            {
                Rect box = bounds(item);
                int target = -1;
                for (int i = 0; i < boxes.Count; i++)
                    if (boxes[i].Overlaps(box)) { target = i; break; }
                if (target < 0)
                {
                    groups.Add(new List<T> { item }); boxes.Add(box);
                    continue;
                }
                groups[target].Add(item); boxes[target] = Union(boxes[target], box);
                for (int i = 0; i < boxes.Count; i++)
                {
                    if (i == target || !boxes[target].Overlaps(boxes[i])) continue;
                    groups[target].AddRange(groups[i]); boxes[target] = Union(boxes[target], boxes[i]);
                    groups.RemoveAt(i); boxes.RemoveAt(i);
                    if (i < target) target--;
                    i = -1;
                }
            }
            return groups;
        }
        private static Rect Union(Rect a, Rect b) => Rect.MinMaxRect(
            Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin),
            Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));
    }
}
