using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace PEV.Scripting
{
    // Generic asynchronous frame ownership and Unity upload. Pixel content and planning
    // come from the caller; no event parameters or rendering rules live here.
    public sealed class BakedFrameCache
    {
        internal struct Plan { internal int Width, Height, Count; }
        public sealed class Frames
        {
            public Texture2D[] Textures;
            public Sprite[] Sprites;
            public Vector2 NaturalSize;
            public string Key;
            internal int Refs;
            internal BakedFrameCache Owner;
            internal Color32[][] Pixels;
            internal int TexW, TexH, Uploaded;
            internal volatile bool PixelsReady, Cancelled;
            internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            internal bool Ready;
            internal volatile string Error;
            internal float WorkerMs, UploadTotalMs, WorstUploadSliceMs;
            internal Thread Worker;
        }
        internal readonly Dictionary<string, Frames> Cache = new Dictionary<string, Frames>(StringComparer.Ordinal);
        private static readonly SemaphoreSlim BakeGate = new SemaphoreSlim(1);
        private readonly float _pixelsPerUnit;
        internal int Generations, Bakes;
        internal float LastBakeMs, LastUploadMs, WorstBakeMs;
        internal string LastBakeDetail = "";
        internal BakedFrameCache(float pixelsPerUnit)
        {
            if (pixelsPerUnit <= 0 || float.IsNaN(pixelsPerUnit) || float.IsInfinity(pixelsPerUnit))
                throw new ArgumentOutOfRangeException(nameof(pixelsPerUnit));
            _pixelsPerUnit = pixelsPerUnit;
        }
        internal Frames Begin(string key, Func<CancellationToken, Plan> prepare,
            Action<Color32[][], int, int, CancellationToken> render)
        {
            if (key == null || prepare == null || render == null) throw new ArgumentNullException();
            if (Cache.TryGetValue(key, out var hit) && HitIsAlive(hit)) { hit.Refs++; return hit; }
            var frames = new Frames { Key = key, Refs = 1, Owner = this };
            Generations++; Bakes++; Cache[key] = frames;
            var worker = new Thread(() => RunWorker(frames, prepare, render)) { IsBackground = true, Name = "PEV.FrameBake" };
            frames.Worker = worker; worker.Start(); return frames;
        }
        internal void Pump(float budgetMs)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (var kv in Cache)
            {
                var f = kv.Value;
                if (f == null || !f.PixelsReady || f.Ready || f.Error != null) continue;
                bool first = f.Uploaded == 0;
                while (f.Uploaded < f.Textures.Length)
                {
                    try { UploadOne(f, f.Uploaded); }
                    catch (Exception e) { f.Error = "frame upload failed: " + e.Message; DestroyFrames(f); break; }
                    f.Uploaded++;
                    if (first && f.Uploaded >= 2) break;
                    if (watch.Elapsed.TotalMilliseconds >= budgetMs) break;
                }
                if (f.Error != null) continue;
                float slice = (float)watch.Elapsed.TotalMilliseconds;
                if (slice > f.WorstUploadSliceMs) f.WorstUploadSliceMs = slice;
                if (f.Uploaded >= f.Textures.Length)
                {
                    f.Ready = true; LastUploadMs = slice;
                    LastBakeDetail = $"{f.TexW}x{f.TexH}x{f.Textures.Length} " +
                        $"payload={(long)f.TexW * f.TexH * f.Textures.Length / 1024}KB " +
                        $"worker={f.WorkerMs:F1}ms uploadTotal={f.UploadTotalMs:F1}ms " +
                        $"worstSlice={f.WorstUploadSliceMs:F1}ms";
                    LastBakeMs = f.WorstUploadSliceMs;
                    if (LastBakeMs > WorstBakeMs) WorstBakeMs = LastBakeMs;
                }
                if (watch.Elapsed.TotalMilliseconds >= budgetMs) return;
            }
        }
        private void UploadOne(Frames f, int k)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var pixels = f.Pixels[k];
            if (pixels == null) return;
            var tex = new Texture2D(f.TexW, f.TexH, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, name = "PEV.Frame." + k };
            f.Textures[k] = tex; tex.SetPixels32(pixels); tex.Apply(false);
            f.Sprites[k] = Sprite.Create(tex, new Rect(0f, 0f, f.TexW, f.TexH), new Vector2(.5f, .5f), _pixelsPerUnit);
            f.Sprites[k].name = "PEV.FrameSprite." + k;
            f.UploadTotalMs += (float)watch.Elapsed.TotalMilliseconds; f.Pixels[k] = null;
        }
        internal void Release(Frames frames)
        {
            if (frames == null) return;
            if (!ReferenceEquals(frames.Owner, this)) throw new ArgumentException("Frame set belongs to another cache");
            if (frames.Refs <= 0)
            {
                if (frames.Key != null && Cache.TryGetValue(frames.Key, out var dead) && ReferenceEquals(dead, frames)) Cache.Remove(frames.Key);
                return;
            }
            frames.Refs--;
            if (frames.Refs > 0) return;
            if (frames.Key != null && Cache.TryGetValue(frames.Key, out var cached) && ReferenceEquals(cached, frames)) Cache.Remove(frames.Key);
            DestroyFrames(frames);
        }
        internal void Clear()
        {
            foreach (var item in Cache) DestroyFrames(item.Value);
            Cache.Clear();
        }
        private static void DestroyFrames(Frames frames)
        {
            Sprite[] sprites; Texture2D[] textures;
            lock (frames)
            {
                frames.Cancelled = true; frames.PixelsReady = false; frames.Pixels = null; frames.Refs = 0;
                sprites = frames.Sprites; frames.Sprites = null; textures = frames.Textures; frames.Textures = null;
            }
            frames.Cancellation.Cancel();
            if (sprites != null) foreach (var sprite in sprites) if (sprite != null) UnityEngine.Object.Destroy(sprite);
            if (textures != null) foreach (var texture in textures) if (texture != null) UnityEngine.Object.Destroy(texture);
        }
        private static bool HitIsAlive(Frames f)
        {
            if (!f.Ready) return string.IsNullOrEmpty(f.Error);
            if (f.Textures == null || f.Sprites == null) return false;
            foreach (var texture in f.Textures) if (texture == null) return false;
            foreach (var sprite in f.Sprites) if (sprite == null) return false;
            return true;
        }
        private void RunWorker(Frames frames, Func<CancellationToken, Plan> prepare,
            Action<Color32[][], int, int, CancellationToken> render)
        {
            bool entered = false;
            try
            {
                BakeGate.Wait(frames.Cancellation.Token); entered = true;
                frames.Cancellation.Token.ThrowIfCancellationRequested();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var plan = prepare(frames.Cancellation.Token);
                if (plan.Width < 1 || plan.Height < 1 || plan.Width > 4096 || plan.Height > 4096 || plan.Count < 1 ||
                    plan.Count > 128 || (long)plan.Width * plan.Height > 1048576 ||
                    (long)plan.Width * plan.Height * plan.Count > 4200000)
                    throw new ArgumentException("Frame plan exceeds host limits");
                var pixels = new Color32[plan.Count][];
                lock (frames)
                {
                    if (frames.Cancelled) return;
                    frames.TexW = plan.Width; frames.TexH = plan.Height;
                    frames.Textures = new Texture2D[plan.Count]; frames.Sprites = new Sprite[plan.Count]; frames.Pixels = pixels;
                    frames.NaturalSize = new Vector2(plan.Width / _pixelsPerUnit, plan.Height / _pixelsPerUnit);
                }
                render(pixels, plan.Width, plan.Height, frames.Cancellation.Token);
                foreach (var frame in pixels)
                {
                    frames.Cancellation.Token.ThrowIfCancellationRequested();
                    if (frame == null || frame.Length != plan.Width * plan.Height)
                        throw new InvalidOperationException("Frame renderer returned an incomplete frame set");
                }
                lock (frames)
                {
                    frames.WorkerMs = (float)watch.Elapsed.TotalMilliseconds;
                    if (!frames.Cancelled) frames.PixelsReady = true;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (!frames.Cancelled) frames.Error = "frame bake failed: " + e.Message; }
            finally { if (entered) BakeGate.Release(); }
        }
    }
}
