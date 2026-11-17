using System;
using UnityEngine;
using Request = PEV.Scripting.NoiseTextureFactory.Request;
using Piece = PEV.Scripting.NoiseTextureFactory.Piece;

namespace PEV.Scripting
{
    // Previous production CPU implementation retained only for diagnostic parity.
    internal static class LegacyCpuBakeReference
    {
        private static readonly int BakeThreads = Math.Max(1, Environment.ProcessorCount / 2);
        internal static void RenderPixels(Request req, Color32[][] pixelSets, int texW, int texH, Func<bool> cancelled)
        {
            int frames = pixelSets.Length;
            int block = Mathf.Clamp(Mathf.RoundToInt(req.Samples.Profile.GrainPixels / Mathf.Clamp(req.SampleRate, 0.1f, 8f)), 1, 16);
            uint seed = (uint)req.Seed;
            var pieces = req.Pieces ?? Array.Empty<Piece>();

            float margin = Mathf.Max(0f, req.Margin);
            float totalW = Mathf.Max(1e-3f, req.WidthWorld + margin * 2f);
            float totalH = Mathf.Max(1e-3f, req.HeightWorld + margin * 2f);
            int cellsX = Mathf.Max(1, texW / block);
            int cellsY = Mathf.Max(1, texH / block);

            // One flipbook cycle is exactly `frames / evolveSpeed` seconds of playback, and the
            // drift must close over that span or the loop point jumps. The script walks
            // frame = floor(t * evolveSpeed) % frames, so this is the only span where a step
            // at the wrap is the same size as every other step.
            req.LoopPeriod = frames / Mathf.Max(1f, req.EvolveSpeed);

            var framesBuilt = new NoiseComposer.Frame[frames];
            var options = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = BakeThreads };
            System.Threading.Tasks.Parallel.For(0, frames, options, k =>
            {
                if (cancelled()) return;
                // The seed only reaches the glitch pass. The composer's grain comes from the
                // noise maps, sampled at a drift driven by the frame's own shader time, so
                // consecutive frames move the same grains rather than re-rolling them — which
                // is what makes the field flow instead of shimmer. Keeping one seed here is
                // what stops the coastline from being re-rolled per frame.
                uint kseed = (uint)(k * 7919) + seed * 2654435761u;

                // The whole pipeline — coverage, edge, five glow passes, composite — comes from
                // the ported reference passes. Animation is driven by the frame index rather
                // than a wall clock so the flipbook loops exactly with the song.
                // One frame step of SHADER time. The playback advances the frame index at
                // EvolveSpeed per second, so each frame must represent 1/EvolveSpeed seconds —
                // not LoopPeriod/frames, which made the field run 90x fast and read as random
                // regeneration rather than flow.
                float frameTime = k / Mathf.Max(1f, req.EvolveSpeed);
                framesBuilt[k] = NoiseComposer.Frame.Build(pieces, req, texW, texH, frameTime, kseed, frames);
                pixelSets[k] = new Color32[texW * texH];
            });

            // Rows are chunked coarsely: each band costs a fresh pass over the 6-pixel cell
            // column, and too many bands would spend more time on that than on the pixels.
            //
            // The band count is per frame. It used to be `frames * max(1, texH / 24)` and then
            // multiplied by `frames` again at the loop bound, which dispatched 46,080 tasks for
            // a 132-row texture — 475 of every 480 bands per frame were empty.
            int bandsPerFrame = Mathf.Clamp(texH / 24, 1, 8);
            System.Threading.Tasks.Parallel.For(0, frames * bandsPerFrame, options, task =>
            {
                if (cancelled()) return;
                int k = task / bandsPerFrame;
                int band = task % bandsPerFrame;
                int y0 = (int)((long)texH * band / bandsPerFrame);
                int y1 = (int)((long)texH * (band + 1) / bandsPerFrame);
                if (y1 > y0) framesBuilt[k].RenderRows(pixelSets[k], y0, y1);
            });

            // Per-row horizontal tear, the reference's GlitchMap. After the passes, because it
            // moves finished rows rather than changing what is drawn, and off the render so the
            // bands never write the same row twice.
            float glitch = Mathf.Clamp01(req.Glitch);
            if (glitch > 0f && texH > 0)
            {
                System.Threading.Tasks.Parallel.For(0, frames, options, k =>
                {
                    if (cancelled()) return;
                    var pixels = pixelSets[k];
                    if (pixels == null) return;
                    uint kseed = (uint)(k * 7919) + seed * 2654435761u;
                    var rowShift = new int[texH];
                    for (int y = 0; y < texH; y++)
                    {
                        uint gh = Hash(0x5BD1u, (uint)y, kseed);
                        if ((gh & 0xFFFF) / 65535f >= glitch * 0.35f) continue;
                        int rowSpan = Mathf.Max(1, Mathf.RoundToInt(glitch * texW * 0.18f));
                        rowShift[y] = Mathf.RoundToInt(((gh >> 16) & 0xFFFF) / 65535f * 2f - 1f) * rowSpan;
                    }
                    var shifted = new Color32[pixels.Length];
                    for (int y = 0; y < texH; y++)
                    {
                        int row = y * texW;
                        int shift = rowShift[y];
                        for (int x = 0; x < texW; x++)
                        {
                            int sx = x + shift;
                            if (shift != 0)
                            {
                                sx %= texW;
                                if (sx < 0) sx += texW;
                            }
                            shifted[row + x] = pixels[row + sx];
                        }
                    }
                    pixelSets[k] = shifted;
                });
            }
        }



        private static uint Hash(uint a, uint b, uint c)
        {
            uint h = a * 374761393u + b * 668265263u + c * 2147483647u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
