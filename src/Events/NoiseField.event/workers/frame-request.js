// Builds the CPU fallback's request and cache identity from ordinary decoration data.
function updateController(input) {
  var f=Math.fround, members=input.members, lead=members[0], p=lead.parameters || {};
  if (!members.length) throw Error('empty render group');
  function number(value, fallback) {
    if (value === null || value === undefined || value === '') return fallback;
    var n=Number(value); return isFinite(n)?f(n):fallback;
  }
  function clamp(x,a,b){return Math.max(a,Math.min(b,x));}
  function color(value, fallback) {
    if (Array.isArray(value) && value.length >= 3) return {r:f(value[0]),g:f(value[1]),b:f(value[2])};
    if (typeof value !== 'string') return fallback;
    var s=value.charAt(0)==='#'?value.slice(1):value;
    if (!/^[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$/.test(s)) return fallback;
    return {r:f(parseInt(s.slice(0,2),16)/255),g:f(parseInt(s.slice(2,4),16)/255),b:f(parseInt(s.slice(4,6),16)/255)};
  }
  function roundEven(x){var lo=Math.floor(x),d=x-lo;return d<.5?lo:d>.5?lo+1:(lo%2===0?lo:lo+1);}
  var displace=clamp(number(p.displace,0),0,1), marginScale=clamp(number(p.edgeMargin,0),0,2);
  var margin=f(f(displace*marginScale)*Math.min(input.width,input.height));
  var requested=f(f(input.pixelWidth)*f(f(input.width)/Math.max(f(.0001),f(lead.width))));
  var wanted=clamp(roundEven(f(requested/96)),1,12)*96;
  var bucket=input.previousBucket<=0 || Math.abs(f(requested-input.previousBucket))>=96 ? wanted : input.previousBucket;
  var pieces=members.map(function(m){
    var scale=m.scale>0?m.scale:1;
    return {Offset:{x:f(f(m.x+m.offsetX)-input.centerX),y:f(f(m.y+m.offsetY)-input.centerY)},
      Half:{x:f(f(f(m.width*.5)*scale)),y:f(f(f(m.height*.5)*scale))},
      Rotation:f(m.rotation+m.offsetRotation),Circle:String((m.parameters||{}).shape||'Rect').toLowerCase()==='circle'};
  });
  var alpha=0;
  members.forEach(function(m){alpha=Math.max(alpha,f(number(m.opacity,1)*clamp(number(m.alpha,1),0,1)));});
  var base={r:f(lead.color[0]),g:f(lead.color[1]),b:f(lead.color[2])};
  var opacity=clamp(number(p.fillOpacity,1),0,1);
  var request={WidthWorld:input.width,HeightWorld:input.height,Margin:margin,Pieces:pieces,
    EdgeColor:color(p.edgeColor,base),SceneColor:{r:f(input.scene[0]),g:f(input.scene[1]),b:f(input.scene[2])},
    FillOpacity:opacity,FillStrength:opacity,Exposure:clamp(number(p.exposure,0),0,2),
    EvolveSpeed:clamp(number(p.evolveSpeed,1),1,60),EdgeOpacity:clamp(number(p.edgeOpacity,1),0,1),
    GlowOpacity:clamp(number(p.glowOpacity,0),0,1),SparkOpacity:clamp(number(p.sparkOpacity,0),0,1),
    Glitch:clamp(number(p.glitch,0),0,1),SampleRate:Math.max(.1,number(p.sampleRate,1)),
    FrameCount:clamp(roundEven(number(p.frameCount,2)),2,128),Seed:input.seed,
    TargetPixelWidth:bucket,ScreenPixelWidth:bucket};
  function stable(value) {
    if (typeof value==='number') return Number.isInteger(value)?value:Number(value.toFixed(3));
    if (Array.isArray(value)) return value.map(stable);
    if (value && typeof value==='object') { var o={}; Object.keys(value).forEach(function(k){o[k]=stable(value[k]);}); return o; }
    return value;
  }
  return {request:request,key:JSON.stringify(stable(request)),margin:margin,bucket:bucket,alpha:alpha};
}
