// Phira-Pro block touch geometry and response. The host provides only JSON snapshots.
var slots = [];
var lastTime = null;
function updateContacts(input) {
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
    var normal = null, inverted = null, subtract = 0;
    for (j = 0; j < input.Areas.length; j++) {
      var area = input.Areas[j];
      if (!area.Active || !area.Visible || area.HalfSize.x <= 0 || area.HalfSize.y <= 0) continue;
      var dx = px - area.Center.x, dy = py - area.Center.y;
      var c = Math.cos(-area.Angle), sin = Math.sin(-area.Angle);
      var inside = Math.abs(dx * c - dy * sin) <= area.HalfSize.x && Math.abs(dx * sin + dy * c) <= area.HalfSize.y;
      if (!inside) continue;
      if (area.Inverted) { subtract++; if (!inverted) inverted = area; }
      else if (!normal) normal = area;
    }
    if (!!normal === ((subtract & 1) !== 0)) continue;
    var style = normal || inverted;
    var size = style.TouchSize === undefined ? 1 : style.TouchSize;
    var opacity = style.TouchOpacity === undefined ? 1 : style.TouchOpacity;
    if (size > 0 && opacity > 0) contacts.push({id:point.Id,x:point.Position.x,y:point.Position.y,size:size,opacity:opacity});
    else for (j = 0; j < slots.length; j++) {
      // An explicit zero turns off any trail this planet was already drawing.
      if (slots[j].id === point.Id) { slots[j].held = false; slots[j].scale = 0; slots[j].startScale = 0; slots[j].endScale = 0; }
    }
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
    slot.x = contact.x; slot.y = contact.y; slot.size = contact.size; slot.opacity = contact.opacity; slot.seen = true;
  }
  var visible = false, drawn = [];
  for (i = 0; i < slots.length; i++) {
    var item = slots[i];
    if (item.held && !item.seen) {
      item.held = false; item.startTime = time; item.startScale = item.scale; item.endScale = 0;
    }
    if (item.held || item.scale > 0) visible = true;
    if (item.scale > 0) drawn.push({x:item.x, y:item.y, scale:item.scale,size:item.size,opacity:item.opacity});
  }
  return {visible:visible, contacts:contacts.slice(0, capacity), slots:drawn};
}
function updateController(input) { return updateContacts(input); }
