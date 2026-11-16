using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using PEV.Scripting;

class Program
{
    private sealed class Resource : IDisposable
    {
        internal int Disposals;
        public void Dispose() { Disposals++; }
    }

    private static NoiseFieldColorOwnership.Zone Area(float x, float y, float hx, float hy,
        bool inverse, bool active, float opacity, float angle, float r, float g, float b) =>
        new NoiseFieldColorOwnership.Zone { CenterX = x, CenterY = y, HalfX = hx, HalfY = hy,
            Inverted = inverse, Active = active, YScale = 1, Opacity = opacity, Angle = angle,
            Red = r, Green = g, Blue = b };

    static int Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 2 || (args.Length == 2 && args[1] != "--benchmark"))
            throw new ArgumentException("Pass the fixture directory and optionally --benchmark");
        var areas = new[] {
            Area(-.2f,.05f,.45f,.27f,false,true,1f,.23f,0,1,0),
            Area(.3f,-.1f,.35f,.18f,true,true,1f,0,0,0,1),
            Area(.05f,.1f,.28f,.22f,false,false,.35f,0,1,1,1),
            Area(-.3f,-.12f,.24f,.20f,true,false,.65f,0,.2f,.4f,.9f),
            Area(.05f,-.05f,.21f,.13f,false,false,1f,0,.9f,.2f,.7f),
            Area(.15f,.08f,.17f,.14f,true,false,.4f,0,.3f,.9f,.2f)
        };
        int failures = 0;
        foreach (var dims in new[] { new[] { 256, 144 }, new[] { 255, 143 } })
        {
            int width = Math.Max(1, dims[0] / 8) * 2, height = Math.Max(1, dims[1] / 8) * 2;
            var result = NoiseFieldColorOwnership.Build(width, height, 16f / 9f, areas);
            foreach (var pair in new[] { ("raw-active", result.Active), ("disabled", result.Disabled) })
            {
                var expected = File.ReadAllBytes(Path.Combine(args[0], $"{dims[0]}x{dims[1]}-{pair.Item1}.bin"));
                if (pair.Item2.Length != expected.Length) throw new Exception("Fixture length mismatch");
                int different = 0, first = -1;
                for (int i = 0; i < expected.Length; i++)
                    if (expected[i] != pair.Item2[i]) { if (first < 0) first = i; different++; }
                Console.WriteLine($"{dims[0]}x{dims[1]} {pair.Item1}: {different} differing bytes (first {first})");
                if (different != 0) failures++;
            }
        }
        var workspace = new NoiseFieldColorOwnership.Workspace();
        var firstResult = NoiseFieldColorOwnership.Build(64, 36, 16f / 9f, areas, workspace);
        var saved = (byte[])firstResult.Active.Clone();
        var changedAreas = (NoiseFieldColorOwnership.Zone[])areas.Clone();
        changedAreas[0].Red = 1f;
        var second = NoiseFieldColorOwnership.Build(64, 36, 16f / 9f, changedAreas, workspace);
        if (!Equal(firstResult.Active, saved) || ReferenceEquals(firstResult.Active, second.Active))
            throw new Exception("Alternating workspace buffers are not isolated");
        var third = NoiseFieldColorOwnership.Build(64, 36, 16f / 9f, areas, workspace);
        if (!ReferenceEquals(firstResult.Active, third.Active) || !Equal(third.Active, saved))
            throw new Exception("Workspace did not correctly reuse and reset buffers");
        Console.WriteLine("Palette workspace alternation and reuse: PASS");
        using (var worker = new NoiseFieldColorWorker())
        {
            var snapshot = (NoiseFieldColorOwnership.Zone[])areas.Clone();
            var expected = NoiseFieldColorOwnership.Build(64, 36, 16f / 9f, snapshot);
            worker.Update(64, 36, 16f / 9f, snapshot);
            snapshot[0].Red = 1f; // the worker must own the input it was given
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (worker.Latest == null && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5);
                worker.Update(64, 36, 16f / 9f, areas);
                if (worker.LastError != null) throw worker.LastError;
            }
            if (worker.Latest == null || !Equal(worker.Latest.Active, expected.Active))
                throw new Exception("Worker did not preserve its immutable input");
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(2);
                if (worker.Update(64, 36, 16f / 9f, areas)) throw new Exception("Unchanged palette rebuilt");
            }
            worker.Update(62, 34, 16f / 9f, areas);
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (worker.LatestWidth != 62 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5);
                worker.Update(62, 34, 16f / 9f, areas);
                if (worker.LastError != null) throw worker.LastError;
            }
            if (worker.LatestWidth != 62 || worker.LatestHeight != 34) throw new Exception("Resize did not complete");
            var moving = (NoiseFieldColorOwnership.Zone[])areas.Clone();
            for (int i = 0; i < 20; i++)
            {
                moving[0].CenterX = -0.2f + i * 0.01f;
                worker.Update(128, 72, 16f / 9f, moving);
            }
            var latest = NoiseFieldColorOwnership.Build(128, 72, 16f / 9f, moving);
            deadline = DateTime.UtcNow.AddSeconds(10);
            while ((worker.LatestWidth != 128 || !Equal(worker.Latest.Active, latest.Active)) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5);
                worker.Update(128, 72, 16f / 9f, moving);
                if (worker.LastError != null) throw worker.LastError;
            }
            if (worker.LatestWidth != 128 || !Equal(worker.Latest.Active, latest.Active))
                throw new Exception("Moving palette did not converge to the last requested snapshot");
            Console.WriteLine("Worker snapshot, idle reuse, resize and latest-request convergence: PASS");
        }
        var canceledWorker = new NoiseFieldColorWorker();
        canceledWorker.Update(400, 224, 16f / 9f, areas);
        canceledWorker.Dispose();
        canceledWorker.Dispose();
        try { canceledWorker.Update(1, 1, 1f, areas); throw new Exception("Disposed worker accepted work"); }
        catch (ObjectDisposedException) { }
        Console.WriteLine("Worker disposal and cancellation: PASS");
        var staging = new NoiseFieldGroupStaging<int, Resource>();
        var keep = new Dictionary<int, bool> { [1] = true };
        var originalResource = new Resource(); var replacement = new Resource();
        var superseded = new Resource(); var removed = new Resource();
        staging.SetPending(1, originalResource);
        if (staging.Committed || staging.Active.Count != 0) throw new Exception("Staged group became active too early");
        staging.Commit(keep);
        staging.SetPending(1, superseded);
        staging.SetPending(1, replacement);
        staging.SetPending(2, removed);
        staging.DiscardPendingExcept(keep);
        if (originalResource.Disposals != 0 || superseded.Disposals != 1 || removed.Disposals != 1 ||
            staging.Active[1] != originalResource || staging.Pending[1] != replacement)
            throw new Exception("Pending groups did not preserve or dispose resources correctly");
        staging.Commit(keep);
        if (originalResource.Disposals != 1 || staging.Active[1] != replacement || staging.Pending.Count != 0)
            throw new Exception("Group commit did not replace the previous resource");
        staging.Dispose(); staging.Dispose();
        if (replacement.Disposals != 1) throw new Exception("Group cleanup was not idempotent");
        Console.WriteLine("Group staging, replacement and cleanup: PASS");
        if (args.Length == 2)
        {
            var benchWorkspace = new NoiseFieldColorOwnership.Workspace();
            for (int i = 0; i < 3; i++) NoiseFieldColorOwnership.Build(400, 224, 16f / 9f, areas, benchWorkspace);
            var times = new double[20];
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < times.Length; i++)
            {
                long start = Stopwatch.GetTimestamp();
                NoiseFieldColorOwnership.Build(400, 224, 16f / 9f, areas, benchWorkspace);
                times[i] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Array.Sort(times);
            double sum = 0; foreach (double value in times) sum += value;
            Console.WriteLine($"CPU palette 400x224, 6 areas, 20 samples: mean={sum / times.Length:F3} ms p95={times[18]:F3} ms allocated={allocated / times.Length / 1024.0:F0} KiB/build");
        }
        return failures == 0 ? 0 : 1;
    }

    private static bool Equal(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
