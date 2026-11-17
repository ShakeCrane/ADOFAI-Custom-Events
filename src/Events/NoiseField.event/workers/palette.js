// Region color ownership, derived from Phira-Pro c8fb11c6 block_color.rs / block_mask.rs (GPL-3.0-or-later).
// The host only provides bounded, generic raster and vector operations.
var PALETTE_PASSES=[{reverse:false,offsets:[[-1,0],[-1,-1],[0,-1],[1,-1]]}, {reverse:true,offsets:[[1,0],[-1,1],[0,1],[1,1]]}];
var PALETTE_RANGE=[['in',0],['c',23],['ge'],['in',0],['c',30],['le'],['mul']];
var PALETTE_ACTIVE=PALETTE_RANGE.concat([['c',255],['mul']]);
var PALETTE_DISABLED=PALETTE_RANGE.concat([['set',0],['in',1],['c',255],['div'],['set',1],['get',1],['c',0.2],['sub'],['c',-10],['mul'],['clamp'],['set',2],['get',0],['get',2],['get',2],['mul'],['c',3],['c',2],['get',2],['mul'],['sub'],['mul'],['add'],['set',3],['get',3],['unorm'],['c',255],['div'],['get',1],['get',3],['mul'],['c',10],['mul'],['unorm'],['c',255],['div'],['mul'],['unorm']]);
function computeVector(input) {
  if (input.request) {
    input.defaultRgb=input.request.Options;
    input.zones=input.request.Items.map(function(z) { return {
      centerX:z.CenterX,centerY:z.CenterY,halfX:z.HalfX,halfY:z.HalfY,angle:z.Angle,
      yScale:z.YScale,opacity:z.Opacity,red:z.Red,green:z.Green,blue:z.Blue,
      active:z.Active,inverted:z.Inverted
    }; });
  }
  var zones=input.zones, colors=zones.map(function(z){return [z.red,z.green,z.blue]});
  function run(x){return Number(__compute(JSON.stringify(x)));}
  function shapes(active,inverted,green){
    return zones.map(function(z,i){
      if(z.active!==active || z.inverted!==inverted || !(z.opacity>0))return null;
      return {x:z.centerX,y:z.centerY,hx:z.halfX,hy:z.halfY,scale:z.yScale,angle:z.angle,
              value:inverted?(green?Math.fround(Math.fround(.1)*Math.fround(z.opacity)):0.1):z.opacity,owner:i};
    }).filter(function(z){return z!==null;});
  }
  function layer(active){
    var normal=run({op:'raster',blend:'add',shapes:shapes(active,false,false)});
    var inverse=run({op:'raster',blend:'add',shapes:shapes(active,true,false)});
    var green=run({op:'raster',blend:'add',shapes:active?[]:shapes(false,true,true)});
    var code=active?PALETTE_ACTIVE:PALETTE_DISABLED;
    var value=run({op:'evaluate',inputs:[inverse,green],program:code});
    var seeds=run({op:'differenceSeeds',a:normal,b:inverse,value:value});
    seeds=run({op:'propagate',seeds:seeds,passes:PALETTE_PASSES});
    return run({op:'map',seeds:seeds,colors:colors,fallback:input.defaultRgb});
  }
  return [layer(true),layer(false)];
}
