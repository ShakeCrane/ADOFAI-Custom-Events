// Draft, not connected to the game renderer.
// Port of Phira-Pro c8fb11c6 block_color.rs and block_mask.rs, GPL-3.0-or-later.
using System;

namespace PEV.Scripting
{
    /// <summary>
    /// CPU-only palette seed and chamfer sweeps. Invoke on a worker with an immutable
    /// snapshot, never on Unity's main thread. The active palette still needs the same
    /// per-frame source-pixel warp as the coverage mask before it is consumed.
    /// </summary>
    internal static class NoiseFieldColorOwnership
    {
        internal struct Zone
        {
            internal float CenterX { get; set; }
            internal float CenterY { get; set; }
            internal float HalfX { get; set; }
            internal float HalfY { get; set; }
            internal float Angle { get; set; }
            internal float YScale { get; set; }
            internal float Opacity { get; set; }
            internal float Red { get; set; }
            internal float Green { get; set; }
            internal float Blue { get; set; }
            internal bool Active { get; set; }
            internal bool Inverted { get; set; }
        }

        internal sealed class Result
        {
            internal byte[] Active, Disabled;
        }

        // Owned by one worker. Scratch arrays and two alternating output pairs are
        // reused so a moving field does not allocate several MiB every frame.
        internal sealed class Workspace
        {
            internal byte[] Normal, Inverse, Green;
            internal int[] NormalOwner, InverseOwner, Seeds, Owners;
            internal Result[] Results;
            internal int Next;

            internal void Ensure(int count)
            {
                if (Normal != null && Normal.Length == count) return;
                int bytes = checked(count * 4);
                // Publish a new workspace only after every allocation succeeds.
                var normal = new byte[count]; var inverse = new byte[count]; var green = new byte[count];
                var normalOwner = new int[count]; var inverseOwner = new int[count];
                var seeds = new int[count]; var owners = new int[count];
                var results = new[] { new Result { Active = new byte[bytes], Disabled = new byte[bytes] },
                                      new Result { Active = new byte[bytes], Disabled = new byte[bytes] } };
                Normal = normal; Inverse = inverse; Green = green;
                NormalOwner = normalOwner; InverseOwner = inverseOwner; Seeds = seeds; Owners = owners; Results = results;
                Next = 0;
            }
        }

        internal static Result Build(int width, int height, float aspect, Zone[] zones) =>
            Build(width, height, aspect, zones, new Workspace());

        internal static Result Build(int width, int height, float aspect, Zone[] zones, Workspace workspace)
        {
            if (width <= 0 || height <= 0 || !(aspect > 0) || float.IsInfinity(aspect) || zones == null || workspace == null)
                throw new ArgumentException("Invalid palette geometry");
            int count = checked(width * height);
            workspace.Ensure(count);
            var result = workspace.Results[workspace.Next];
            BuildLayer(width, height, count, aspect, zones, true, workspace, result.Active);
            BuildLayer(width, height, count, aspect, zones, false, workspace, result.Disabled);
            workspace.Next ^= 1;
            return result;
        }

        private static void BuildLayer(int width, int height, int count, float aspect, Zone[] zones, bool active,
            Workspace workspace, byte[] rgba)
        {
            var normal = workspace.Normal; var inverse = workspace.Inverse; var green = workspace.Green;
            var normalOwner = workspace.NormalOwner; var inverseOwner = workspace.InverseOwner;
            Array.Clear(normal, 0, count); Array.Clear(inverse, 0, count); Array.Clear(green, 0, count);
            for (int i = 0; i < count; i++) { normalOwner[i] = -1; inverseOwner[i] = -1; }
            for (int id = 0; id < zones.Length; id++)
            {
                var z = zones[id];
                if (z.Active != active || !(z.Opacity > 0) || !(z.HalfX > 0) || !(z.HalfY > 0) || !(z.YScale > 0)) continue;
                float s = (float)Math.Sin(z.Angle), c = (float)Math.Cos(z.Angle);
                float yHalf = (Math.Abs(s) * z.HalfX + Math.Abs(c) * z.HalfY) * z.YScale;
                int start = ClampIndex((int)Math.Ceiling(((z.CenterY - yHalf) * aspect + 1f) * height * 0.5f - 0.5f), height);
                int end = ClampIndex((int)Math.Floor(((z.CenterY + yHalf) * aspect + 1f) * height * 0.5f - 0.5f) + 1, height);
                for (int y = start; y < end; y++)
                {
                    float dy = (((y + 0.5f) * 2f / height - 1f) / aspect - z.CenterY) / z.YScale;
                    float lo = -1f, hi = 1f;
                    Clip(c, s * dy, z.HalfX, z.CenterX, ref lo, ref hi);
                    Clip(-s, c * dy, z.HalfY, z.CenterX, ref lo, ref hi);
                    if (lo > hi) continue;
                    int first = ClampIndex((int)Math.Ceiling((lo + 1f) * width * 0.5f - 0.5f), width);
                    int last = ClampIndex((int)Math.Floor((hi + 1f) * width * 0.5f - 0.5f) + 1, width);
                    for (int i = y * width + first; i < y * width + last; i++)
                    {
                        if (z.Inverted)
                        {
                            inverse[i] = Unorm(inverse[i] / 255f + 0.1f);
                            if (!active) green[i] = Unorm(green[i] / 255f + 0.1f * z.Opacity);
                            inverseOwner[i] = id;
                        }
                        else
                        {
                            normal[i] = Unorm(normal[i] / 255f + z.Opacity);
                            normalOwner[i] = id;
                        }
                    }
                }
            }

            var seeds = workspace.Seeds; var owners = workspace.Owners;
            for (int i = 0; i < count; i++) { seeds[i] = -1; owners[i] = -1; }
            for (int i = 0; i < count; i++)
            {
                int sub = inverse[i] >= 23 && inverse[i] <= 30 ? 255 : 0;
                if (!active)
                {
                    float g = green[i] / 255f;
                    float t = Math.Max(0f, Math.Min(1f, (g - 0.2f) * -10f));
                    float r = (sub == 0 ? 0f : 1f) + t * t * (3f - 2f * t);
                    sub = Unorm(Unorm(r) / 255f * (Unorm(g * r * 10f) / 255f));
                }
                if (normal[i] == sub) continue;
                int owner = normal[i] < sub ? inverseOwner[i] : normalOwner[i];
                if (owner >= 0) { seeds[i] = i; owners[i] = owner; }
            }
            for (int reverse = 0; reverse < 2; reverse++)
                for (int n = 0; n < count; n++)
                {
                    int i = reverse == 0 ? n : count - 1 - n;
                    if (seeds[i] == i) continue;
                    int x = i % width, y = i / width, best = seeds[i];
                    long distance = Distance(best, x, y, width);
                    int dy = reverse == 0 ? -1 : 1;
                    Consider(x + dy, y, x, y, width, height, seeds, ref best, ref distance);
                    Consider(x - 1, y + dy, x, y, width, height, seeds, ref best, ref distance);
                    Consider(x, y + dy, x, y, width, height, seeds, ref best, ref distance);
                    Consider(x + 1, y + dy, x, y, width, height, seeds, ref best, ref distance);
                    seeds[i] = best;
                }
            for (int i = 0; i < count; i++)
            {
                int owner = seeds[i] < 0 ? -1 : owners[seeds[i]];
                rgba[i * 4] = owner < 0 ? (byte)255 : Unorm(zones[owner].Red);
                rgba[i * 4 + 1] = owner < 0 ? (byte)84 : Unorm(zones[owner].Green);
                rgba[i * 4 + 2] = owner < 0 ? (byte)84 : Unorm(zones[owner].Blue);
                rgba[i * 4 + 3] = 255;
            }
        }

        private static void Clip(float a, float b, float half, float center, ref float lo, ref float hi)
        {
            if (Math.Abs(a) < 1e-7f) { if (Math.Abs(b) > half) hi = lo - 1f; return; }
            float a0 = (-half - b) / a + center, a1 = (half - b) / a + center;
            lo = Math.Max(lo, Math.Min(a0, a1)); hi = Math.Min(hi, Math.Max(a0, a1));
        }

        private static void Consider(int nx, int ny, int x, int y, int width, int height,
            int[] seeds, ref int best, ref long distance)
        {
            if (nx < 0 || ny < 0 || nx >= width || ny >= height) return;
            int seed = seeds[ny * width + nx];
            long d = Distance(seed, x, y, width);
            if (d < distance) { best = seed; distance = d; }
        }

        private static long Distance(int seed, int x, int y, int width)
        {
            if (seed < 0) return long.MaxValue;
            long dx = x - seed % width, dy = y - seed / width;
            return dx * dx + dy * dy;
        }
        private static int ClampIndex(int value, int max) => Math.Max(0, Math.Min(value, max));
        private static byte Unorm(float value)
        {
            float scaled = Math.Max(0f, Math.Min(1f, value)) * 255f;
            int whole = (int)scaled;
            return (byte)(whole + (scaled - whole >= 0.5f ? 1 : 0));
        }
    }
}
