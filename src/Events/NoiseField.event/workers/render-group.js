// Package-owned per-depth render decisions. Group data is sent again when it changes.
var cachedGroup = null, cachedGeometry = [], cachedColors = [], cachedRegions = [];
function bounded(value, fallback, min, max) {
  return typeof value === 'number' && isFinite(value) ? Math.max(min, Math.min(max, value)) : fallback;
}
function updateController(input) {
  if (input.Group) {
    cachedGroup=input.Group; cachedGeometry=[]; cachedColors=[]; cachedRegions=[];
    for (var i = 0; i < cachedGroup.data.length; i++) {
      var a = cachedGroup.data[i];
      cachedGeometry.push([a[0],a[1],a[2],a[3],a[4],1,a[5],a[6]]);
      if (cachedGroup.colored) cachedColors.push({CenterX:a[0],CenterY:a[1],HalfX:a[2],HalfY:a[3],Angle:a[4],YScale:1,
        Opacity:a[5],Red:a[7],Green:a[8],Blue:a[9],Active:a[10],Inverted:a[11]});
      cachedRegions.push({Center:{x:a[0],y:a[1]},HalfSize:{x:a[2],y:a[3]},Angle:a[4],
        Active:a[12],Inverted:a[13],Visible:true,
        TouchOpacity:bounded(a[14], 1, 0, 1),TouchSize:bounded(a[15], 1, 0, 3)});
    }
  }
  var group=cachedGroup, geometry=cachedGeometry, colors=cachedColors, regions=cachedRegions;
  if (!group) throw Error('group data is missing');
  var style = group.style || [];
  var coastWidth = bounded(style[0], 0.5, 0, 4);
  var outlineWidth = bounded(style[1], 0.045, 0.005, 0.5);
  var animationSpeed = bounded(style[2], 1, 0, 3);
  var worldHeight = input.WorldHeight || 10;
  var touchScale = 10 / worldHeight;
  // The reference warp produces UV offsets in [-0.070703125, 0.070703125].
  var coastScale = coastWidth / (0.070703125 * worldHeight);
  var baseMaskHeight = Math.max(1, Math.floor(input.ViewHeight / 8));
  var response = updateContacts({Time:input.Time,Aspect:input.Aspect,Capacity:input.Capacity,
    Areas:regions,Points:input.Points});
  var slots = response.slots.map(function(s){return [s.x,s.y,s.scale * s.size * touchScale,1 + s.opacity];});
  var points = response.contacts.map(function(p){return [Math.fround(Math.fround(p.x)*Math.fround(input.ViewWidth/input.ViewHeight)),p.y,p.size * touchScale,1 + p.opacity];});
  var defaultRgb = parseInt(input.DefaultColor || '',16);
  if (!isFinite(defaultRgb)) defaultRgb = 0xffffff;
  return {
    animationSpeed:animationSpeed,
    continuous:response.visible,
    buffers:{geometry:geometry,contacts:slots},
    compute:{geometryCount:geometry.length,contactCount:slots.length,aspect:input.Aspect,time:input.Time,
      coastScaleX:1 + Math.min(16, coastScale / input.Aspect),
      coastScaleY:1 + Math.min(16, coastScale),
      ringStep:Math.min(16, outlineWidth * baseMaskHeight * 2 / worldHeight),
      screenAspect:input.ViewWidth/input.ViewHeight,hasContacts:slots.length>0},
    conditions:{showPrimary:group.primary||response.visible,showSecondary:group.secondary,
      colored:group.colored,hover:response.visible,hasContacts:points.length>0},
    values:{contactCount:points.length,contacts:points},
    vector:group.colored?{Items:colors,Options:defaultRgb}:null
  };
}
