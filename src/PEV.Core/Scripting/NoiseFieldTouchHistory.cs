using System;
using System.Collections.Generic;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Touch sprite lifetimes from Phira-Pro block_touch.rs at commit c8fb11c6.
    /// Positions are bottom-left viewport UV and IDs must remain stable across frames.
    /// </summary>
    internal sealed class NoiseFieldTouchHistory
    {
        internal const int Capacity = 10;
        private const float Duration = 0.1f;

        internal struct Contact
        {
            public long Id;
            public Vector2 Position;
        }

        internal struct Slot
        {
            public bool Held;
            public long Id;
            public Vector2 Position;
            public float Scale;
            internal bool Seen;
            internal float StartTime, StartScale, EndScale;
        }

        private readonly Slot[] _slots = new Slot[Capacity];
        private float? _lastTime;
        internal Slot GetSlot(int index) => _slots[index];
        internal bool Visible
        {
            get
            {
                for (int i = 0; i < _slots.Length; i++)
                    if (_slots[i].Held || _slots[i].Scale > 0f) return true;
                return false;
            }
        }

        internal void Reset()
        {
            Array.Clear(_slots, 0, _slots.Length);
            _lastTime = null;
        }

        internal void Update(IReadOnlyList<Contact> contacts, float time)
        {
            if (float.IsNaN(time) || float.IsInfinity(time)) return;
            if (_lastTime.HasValue && time < _lastTime.Value) Reset();
            _lastTime = time;
            for (int i = 0; i < _slots.Length; i++)
            {
                Slot s = _slots[i];
                float t = Mathf.Clamp01((time - s.StartTime) / Duration);
                s.Scale = Mathf.Lerp(s.StartScale, s.EndScale, t);
                s.Seen = false;
                _slots[i] = s;
            }

            int count = Math.Min(contacts?.Count ?? 0, Capacity);
            for (int i = 0; i < count; i++)
            {
                Contact contact = contacts[i];
                if (float.IsNaN(contact.Position.x) || float.IsInfinity(contact.Position.x) ||
                    float.IsNaN(contact.Position.y) || float.IsInfinity(contact.Position.y)) continue;
                int slot = -1;
                for (int j = 0; j < Capacity; j++)
                    if (_slots[j].Held && _slots[j].Id == contact.Id) { slot = j; break; }
                bool existing = slot >= 0;
                if (!existing)
                    for (int j = 0; j < Capacity; j++)
                        if (!_slots[j].Held) { slot = j; break; }
                if (slot < 0) continue;
                Slot s = _slots[slot];
                if (!existing)
                {
                    s.Held = true;
                    s.Id = contact.Id;
                    s.StartTime = time;
                    s.StartScale = s.Scale = 0f;
                    s.EndScale = 1f;
                }
                s.Position = contact.Position;
                s.Seen = true;
                _slots[slot] = s;
            }
            for (int i = 0; i < Capacity; i++)
            {
                Slot s = _slots[i];
                if (s.Held && !s.Seen)
                {
                    s.Held = false;
                    s.StartTime = time;
                    s.StartScale = s.Scale;
                    s.EndScale = 0f;
                    _slots[i] = s;
                }
            }
        }
    }
}
