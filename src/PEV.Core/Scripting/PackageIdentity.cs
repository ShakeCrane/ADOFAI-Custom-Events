using System;
using System.Runtime.CompilerServices;
using System.Threading;
using PEV.Model;

namespace PEV.Scripting
{
    // Each loaded package definition has a unique lifetime identity; a reload cannot
    // accidentally reuse cached data from an older object with the same path or version.
    internal static class PackageIdentity
    {
        private sealed class Entry { internal string Value; }
        private static readonly ConditionalWeakTable<EventManifest, Entry> Identities = new ConditionalWeakTable<EventManifest, Entry>();
        private static long _next;
        internal static string Of(EventManifest manifest) => manifest == null ? "" : Identities.GetValue(manifest,
            _ => new Entry { Value = Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture) }).Value;
    }
}
