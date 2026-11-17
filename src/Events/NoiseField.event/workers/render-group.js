// Package-owned per-depth render decisions. Group data is sent again when it changes.
var cachedGroup = null, cachedGeometry = [], cachedColors = [], cachedRegions = [];
function updateController(input) {
  if (input.Group) {
    cachedGroup=input.Group; cachedGeometry=[]; cachedColors=[]; cachedRegions=[];
    for (var i = 0; i < cachedGroup.data.length; i++) {
      var a = cachedGroup.data[i];
      cachedGeometry.push([a[0],a[1],a[2],a[3],a[4],1,a[5],a[6]]);
      if (cachedGroup.colored) cachedColors.push({CenterX:a[0],CenterY:a[1],HalfX:a[2],HalfY:a[3],Angle:a[4],YScale:1,
        Opacity:a[5],Red:a[7],Green:a[8],Blue:a[9],Active:a[10],Inverted:a[11]});
      cachedRegions.push({Center:{x:a[0],y:a[1]},HalfSize:{x:a[2],y:a[3]},Angle:a[4],
        Active:a[12],Inverted:a[13],Visible:true});
    }
  }
  var group=cachedGroup, geometry=cachedGeometry, colors=cachedColors, regions=cachedRegions;
  if (!group) throw Error('group data is missing');
  var response = updateContacts({Time:input.Time,Aspect:input.Aspect,Capacity:input.Capacity,
    Areas:regions,Points:input.Points});
  var slots = response.slots.map(function(s){return [s.x,s.y,s.scale,0];});
  var points = response.contacts.map(function(p){return [Math.fround(Math.fround(p.x)*Math.fround(input.ViewWidth/input.ViewHeight)),p.y,0,0];});
  var defaultRgb = parseInt(input.DefaultColor || '',16);
  if (!isFinite(defaultRgb)) defaultRgb = 0xffffff;
  return {
    continuous:response.visible,
    buffers:{geometry:geometry,contacts:slots},
    compute:{geometryCount:geometry.length,contactCount:slots.length,aspect:input.Aspect,time:input.Time,
      screenAspect:input.ViewWidth/input.ViewHeight,hasContacts:slots.length>0},
    conditions:{showPrimary:group.primary||response.visible,showSecondary:group.secondary,
      colored:group.colored,hover:response.visible,hasContacts:points.length>0},
    values:{contactCount:points.length,contacts:points},
    vector:group.colored?{Items:colors,Options:defaultRgb}:null
  };
}
