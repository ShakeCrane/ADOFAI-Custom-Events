// Phira region state, inversion and same-depth grouping live in this package.
// The host supplies projected decoration snapshots and validates the returned indices.
function updateController(input) {
  var groups = [], indices = Object.create(null), items = input.Items;
  for (var i = 0; i < items.length; i++) {
    var item = items[i], value = (item.State || 'Active').toLowerCase();
    var params = item.Parameters ? JSON.parse(item.Parameters) : item;
    var inverted = params.invert === true || (typeof params.invert === 'string' && params.invert.toLowerCase() === 'true') || item.Inverted === true;
    var active = value !== 'disabled' && value !== 'ready';
    var warning = value === 'ready', disabled = value === 'disabled';
    var flags = (active ? 1 : warning ? 4 : 0) | (inverted ? 2 : 0);
    var key = item.SortingLayer + ':' + item.SortingOrder + ':' + item.UnityLayer;
    var index = indices[key];
    if (index === undefined) {
      index = groups.length; indices[key] = index;
      groups.push({sorting:[item.SortingLayer,item.SortingOrder,item.UnityLayer],
        primary:false,secondary:false,colored:false,members:[],data:[]});
    }
    var group = groups[index];
    group.primary = group.primary || active || warning;
    group.secondary = group.secondary || disabled || warning;
    group.colored = group.colored || !item.HasDefault || item.Red !== item.DefaultRed ||
      item.Green !== item.DefaultGreen || item.Blue !== item.DefaultBlue;
    group.members.push(i);
    group.data.push([item.X,item.Y,item.HalfX,item.HalfY,item.Angle,item.Opacity,flags,
      item.Red,item.Green,item.Blue,active,inverted,active,inverted]);
  }
  return {groups:groups};
}
