using UnityEngine;

namespace PEV.Scripting
{
    // Previous fixed sizing policy, used only for regression comparisons.
    internal static class LegacyBakePlanReference
    {
        private const int MaxSide=1024, MinCells=8, MaxTexelsPerSet=4200000, MinPxWidth=192;
        private static int ResolutionBucket(float px) => NoiseTextureFactory.ResolutionBucket(px);
        internal static CpuPackageBake.Plan Prepare(NoiseTextureFactory.Request req)
        {
            int frames = Mathf.Clamp(req.FrameCount, 2, 128);
            float sampleRate = Mathf.Clamp(req.SampleRate, 0.1f, 8f);
            int block = Mathf.Clamp(Mathf.RoundToInt(LegacyFieldTextureSet.ProfileData(req.Provider)["grainPixels"][0] / sampleRate), 1, 16);

            // The quad is the geometry box grown by the margin on every side. Geometry
            // size stays the reference for the coastline band and feature size, so padding
            // the quad does not change the shape — only how much of it survives clipping.
            float margin = Mathf.Max(0f, req.Margin);
            float totalW = req.WidthWorld + margin * 2f;
            float totalH = req.HeightWorld + margin * 2f;
            if (totalW < 1e-3f) totalW = 1e-3f;
            if (totalH < 1e-3f) totalH = 1e-3f;

            int pxW = ResolutionBucket(req.TargetPixelWidth);
            float aspect = totalH / totalW;
            int pxH = Mathf.Max(16, Mathf.RoundToInt(pxW * aspect));

            int maxCells = Mathf.Max(MinCells, MaxSide / block);
            int cellsX = Mathf.Clamp(Mathf.RoundToInt(pxW / (float)block), MinCells, maxCells);
            int cellsY = Mathf.Clamp(Mathf.RoundToInt(pxH / (float)block), MinCells, maxCells);

            // Over budget, trade resolution before frame count.
            //
            // Temporal resolution is what the eye reads as flow: a short cycle played at the
            // script's frame rate either strobes or loops visibly, and neither reads as the
            // reference's continuous drift. A slightly coarser texture costs nothing visible —
            // the reference's own mask is 1/8 of the screen — while dropping frames costs the
            // motion. So the width shrinks first, and only a still-too-large set loses frames.
            while (cellsX * block * cellsY * block * frames > MaxTexelsPerSet && pxW > MinPxWidth)
            {
                pxW = Mathf.Max(MinPxWidth, Mathf.RoundToInt(pxW * 0.8f));
                pxH = Mathf.Max(16, Mathf.RoundToInt(pxW * aspect));
                cellsX = Mathf.Clamp(Mathf.RoundToInt(pxW / (float)block), MinCells, maxCells);
                cellsY = Mathf.Clamp(Mathf.RoundToInt(pxH / (float)block), MinCells, maxCells);
            }
            while (cellsX * block * cellsY * block * frames > MaxTexelsPerSet && frames > 2)
                frames--;

            return new CpuPackageBake.Plan { Width = cellsX * block, Height = cellsY * block, Frames = frames };
        }
    }
}
