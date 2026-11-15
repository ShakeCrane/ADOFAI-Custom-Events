using System;

namespace PEV.Util
{
    public static class StableId
    {
        /// <summary>
        /// Custom event types start here. Vanilla LevelEventType tops out at ~65,
        /// so anything in this range is unambiguous.
        /// </summary>
        public const int Base = 100000;

        /// <summary>
        /// FNV-1a over the event id. Deterministic across runs, reinstallations and
        /// machines, so a chart saved today resolves to the same type tomorrow.
        /// </summary>
        public static int FromEventId(string eventId)
        {
            unchecked
            {
                uint hash = 2166136261u;
                foreach (char c in eventId)
                {
                    hash ^= c;
                    hash *= 16777619u;
                }
                // Clamp into a positive offset that cannot collide with vanilla values.
                return Base + (int)(hash % 2000000000u);
            }
        }

        public static string NewUid()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
