// Phira-Pro block touch geometry and response. The host provides only JSON snapshots.
var slots = [];
var lastTime = null;
function updateController(input) {
  var capacity = input.Capacity;
  var time = Math.fround(input.Time), contacts = [], i, j;
  if (lastTime !== null && time < lastTime) slots = [];
  lastTime = time;
  for (i = 0; i < slots.length; i++) {
    var s = slots[i];
    var t = Math.max(0, Math.min(1, Math.fround(Math.fround(time - s.startTime) / Math.fround(0.1))));
    s.scale = Math.fround(s.startScale + Math.fround(Math.fround(s.endScale - s.startScale) * t));
    s.seen = false;
  }
  for (i = 0; i < input.Points.length; i++) {
    var point = input.Points[i], px = point.Position.x * 2 - 1, py = (point.Position.y * 2 - 1) / input.Aspect;
    var normal = false, subtract = 0;
    for (j = 0; j < input.Areas.length; j++) {
      var area = input.Areas[j];
      if (!area.Active || !area.Visible || area.HalfSize.x <= 0 || area.HalfSize.y <= 0) continue;
      var dx = px - area.Center.x, dy = py - area.Center.y;
      var c = Math.cos(-area.Angle), sin = Math.sin(-area.Angle);
      var inside = Math.abs(dx * c - dy * sin) <= area.HalfSize.x && Math.abs(dx * sin + dy * c) <= area.HalfSize.y;
      if (inside) { if (area.Inverted) subtract++; else normal = true; }
    }
    if (normal !== ((subtract & 1) !== 0)) contacts.push({id:point.Id,x:point.Position.x,y:point.Position.y});
  }
  var count = Math.min(contacts.length, capacity);
  for (i = 0; i < count; i++) {
    var contact = contacts[i], index = -1;
    for (j = 0; j < slots.length; j++) if (slots[j].held && slots[j].id === contact.id) { index = j; break; }
    var existing = index >= 0;
    if (!existing) for (j = 0; j < slots.length; j++) if (!slots[j].held) { index = j; break; }
    if (index < 0 && slots.length < capacity) { index = slots.length; slots.push({scale:0, held:false}); }
    if (index < 0) continue;
    var slot = slots[index];
    if (!existing) {
      slot.held = true; slot.id = contact.id; slot.startTime = time;
      slot.startScale = slot.scale = 0; slot.endScale = 1;
    }
    slot.x = contact.x; slot.y = contact.y; slot.seen = true;
  }
  var visible = false, drawn = [];
  for (i = 0; i < slots.length; i++) {
    var item = slots[i];
    if (item.held && !item.seen) {
      item.held = false; item.startTime = time; item.startScale = item.scale; item.endScale = 0;
    }
    if (item.held || item.scale > 0) visible = true;
    if (item.scale > 0) drawn.push({x:item.x, y:item.y, scale:item.scale});
  }
  return {visible:visible, contacts:contacts.slice(0, capacity), slots:drawn};
}
