using System;
using UnityEngine;

namespace PEV.Scripting
{
    // Reusable integer quantization with a caller-supplied hysteresis band.
    internal static class StickyQuantization
    {
        internal static int Update(float value, int previous, float step, int minimum, int maximum, float hysteresis)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || step < 1 || step != Mathf.Round(step) ||
                float.IsNaN(step) || float.IsInfinity(step) || hysteresis < 0 || float.IsNaN(hysteresis) ||
                float.IsInfinity(hysteresis) || minimum < 1 || maximum < minimum || maximum * (double)step > 16384)
                throw new ArgumentException("Invalid sticky quantization parameters");
            int wanted = Mathf.Clamp(Mathf.RoundToInt(value / step), minimum, maximum) * (int)step;
            return previous <= 0 || Mathf.Abs(value - previous) >= hysteresis ? wanted : previous;
        }
    }
}
