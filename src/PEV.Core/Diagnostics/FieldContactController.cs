using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PEV.Scripting
{
    // Transitional field adapter. The package controls hit tests and contact animation.
    internal sealed class FieldContactController
    {
        internal struct Area
        {
            public Vector2 Center, HalfSize;
            public float Angle;
            public bool Active, Inverted, Visible;
        }
        private struct Input { public float Time, Aspect; public int Capacity; public Area[] Areas; public ViewportPoint[] Points; }
        internal struct Slot { internal Vector2 Position; internal float Scale; }
        private readonly JsonController _controller;
        private readonly int _capacity, _maxInputs;
        internal readonly List<ViewportPoint> Contacts = new List<ViewportPoint>();
        internal readonly List<Slot> Slots = new List<Slot>();
        internal bool Visible { get; private set; }
        internal FieldContactController(string source, int capacity, int maxInputs)
        {
            if (capacity < 1 || capacity > 64 || maxInputs < 1 || maxInputs > 4096) throw new ArgumentOutOfRangeException("Controller limits");
            _capacity = capacity; _maxInputs = maxInputs; _controller = new JsonController(source);
        }

        internal void Update(IReadOnlyList<Area> areas, IReadOnlyList<ViewportPoint> points, float aspect, float time)
        {
            if (areas.Count > _maxInputs || points.Count > _maxInputs || !Finite(aspect) || aspect <= 0 || !Finite(time))
                throw new ArgumentException("Invalid contact controller input");
            var input = new Input { Time = time, Aspect = aspect, Capacity = _capacity, Areas = areas.ToArray(), Points = points.ToArray() };
            var result = _controller.Update(PublicFieldsSnapshot.Serialize(input));
            if (result["visible"]?.Type != JTokenType.Boolean || !(result["contacts"] is JArray contacts) ||
                !(result["slots"] is JArray slots) || contacts.Count > _capacity || slots.Count > _capacity)
                throw new ArgumentException("Invalid contact controller output");
            var nextContacts = new List<ViewportPoint>(contacts.Count);
            var nextSlots = new List<Slot>(slots.Count);
            foreach (var token in contacts)
            {
                if (!(token is JObject item) || item["id"]?.Type != JTokenType.Integer) throw new ArgumentException("Invalid contact");
                nextContacts.Add(new ViewportPoint { Id = item.Value<long>("id"), Position = new Vector2(Number(item, "x"), Number(item, "y")) });
            }
            foreach (var token in slots)
            {
                if (!(token is JObject item)) throw new ArgumentException("Invalid contact slot");
                float scale = Number(item, "scale");
                if (scale < 0 || scale > 1) throw new ArgumentException("Invalid contact scale");
                nextSlots.Add(new Slot { Position = new Vector2(Number(item, "x"), Number(item, "y")), Scale = scale });
            }
            Contacts.Clear(); Contacts.AddRange(nextContacts);
            Slots.Clear(); Slots.AddRange(nextSlots); Visible = result.Value<bool>("visible");
        }
        private static bool Finite(float n) => !float.IsNaN(n) && !float.IsInfinity(n);
        private static float Number(JObject item, string name)
        {
            var token = item[name];
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
                throw new ArgumentException("Invalid contact coordinate");
            float value = token.Value<float>();
            if (!Finite(value) || Math.Abs(value) > 1000000f) throw new ArgumentException("Invalid contact coordinate");
            return value;
        }
    }
}
