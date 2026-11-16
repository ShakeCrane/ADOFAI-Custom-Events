using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PEV.Scripting
{
    // Transitional field adapter. The package controls hit tests and contact animation.
    internal sealed class FieldContactController
    {
        internal const int Capacity = 10;
        internal struct Contact { internal long Id; internal Vector2 Position; }
        internal struct Area
        {
            internal Vector2 Center, HalfSize;
            internal float Angle;
            internal bool Active, Inverted, Visible;
        }
        internal struct Slot { internal Vector2 Position; internal float Scale; }
        private readonly JsonController _controller;
        internal readonly List<Contact> Contacts = new List<Contact>();
        internal readonly List<Slot> Slots = new List<Slot>();
        internal bool Visible { get; private set; }
        internal FieldContactController(string source) { _controller = new JsonController(source); }

        internal void Update(IReadOnlyList<Area> areas, IReadOnlyList<Contact> points, float aspect, float time)
        {
            if (areas.Count > 1024 || points.Count > 1024 || !Finite(aspect) || aspect <= 0 || !Finite(time))
                throw new ArgumentException("Invalid contact controller input");
            var input = new
            {
                time, aspect,
                areas = areas.Select(a => new { x = a.Center.x, y = a.Center.y, halfX = a.HalfSize.x,
                    halfY = a.HalfSize.y, angle = a.Angle, active = a.Active, inverted = a.Inverted, visible = a.Visible }).ToArray(),
                points = points.Select(p => new { id = p.Id, x = p.Position.x, y = p.Position.y }).ToArray()
            };
            var result = _controller.Update(JsonConvert.SerializeObject(input));
            if (result["visible"]?.Type != JTokenType.Boolean || !(result["contacts"] is JArray contacts) ||
                !(result["slots"] is JArray slots) || contacts.Count > Capacity || slots.Count > Capacity)
                throw new ArgumentException("Invalid contact controller output");
            var nextContacts = new List<Contact>(contacts.Count);
            var nextSlots = new List<Slot>(slots.Count);
            foreach (var token in contacts)
            {
                if (!(token is JObject item) || item["id"]?.Type != JTokenType.Integer) throw new ArgumentException("Invalid contact");
                nextContacts.Add(new Contact { Id = item.Value<long>("id"), Position = new Vector2(Number(item, "x"), Number(item, "y")) });
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
