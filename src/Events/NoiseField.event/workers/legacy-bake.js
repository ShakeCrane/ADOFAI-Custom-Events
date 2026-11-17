// Package-owned defaults from the reference CPU fallback.
var bakeProfile = {"composeScale":0.125,"composeDisplaceSpeed":2.5899999141693115,"composeDisplaceStrength":0.10000000149011612,"composeDisplaceDirection":[0.5,0.5],"composeDisplaceMapScale":[2.13,1.02],"displaceBlendIntensity":0.41100001335144043,"activeDisplaceDirection":[1,1],"activeDisplaceMapScale":[0.8,0.3],"activeSparkMapScale":[3,1.2],"activeSparkDisplaceIntensity":2.390000104904175,"activeSparkHueShiftAmount":0.20000000298023224,"fillStrength":0.667,"activeDisplaceSpeed":1.5,"activeDisplaceStrength":0.15,"sparkMapOpacity":5.69,"fillValue":0.713207483291626,"glowSaturation":1.2253520800891937,"sparkSaturation":1.0676054460523543,"grainPixels":1.5,"glowWeights":[0.458567970132,0.282861223139,0.156589227109,0.07305908496,0.024947807942]};
// CPU fallback stages. This program owns the effect formula; Core only executes bounded pixel instructions.
function computeVector(input) {
  input.profile = bakeProfile;
  if (input.stage !== 'coverage' && input.stage !== 'effects' && input.stage !== 'frame') throw Error('unknown bake stage');
  var f = Math.fround, pi = f(Math.PI);
  // Interpret the legacy decoration request inside the event, not in the host's frame executor.
  if (input.request) {
    var request=input.request, margin=Math.max(0,f(request.Margin));
    var speed=Math.max(1,f(request.EvolveSpeed));
    input.screenPixelWidth=request.ScreenPixelWidth>0?request.ScreenPixelWidth:input.width;
    input.time=f(f(input.frameIndex)/speed); input.loopPeriod=f(f(input.frameCount)/speed);
    input.totalWidth=Math.max(f(.001),f(f(request.WidthWorld)+f(margin*f(2))));
    input.totalHeight=Math.max(f(.001),f(f(request.HeightWorld)+f(margin*f(2))));
    input.seed=request.Seed; input.pieces=(request.Pieces||[]).map(function(piece) {
      return {x:piece.Offset.x,y:piece.Offset.y,hx:piece.Half.x,hy:piece.Half.y,rotation:piece.Rotation,circle:piece.Circle};
    });
    input.options={edgeColor:[request.EdgeColor.r,request.EdgeColor.g,request.EdgeColor.b],
      sceneColor:[request.SceneColor.r,request.SceneColor.g,request.SceneColor.b],
      fillOpacity:request.FillOpacity,fillStrength:request.FillStrength,exposure:request.Exposure,
      edgeOpacity:request.EdgeOpacity,glowOpacity:request.GlowOpacity,sparkOpacity:request.SparkOpacity,glitch:request.Glitch};
  }
  function roundEven(value) {
    var floor=Math.floor(value), fraction=value-floor;
    return fraction<.5 ? floor : fraction>.5 ? floor+1 : (floor%2===0 ? floor : floor+1);
  }
  function run(command) { return Number(__compute(JSON.stringify(command))); }
  function fill(width,height,value) { return run({op:'pixelFill',width:width,height:height,value:value}); }
  var outputWidth=input.width, outputHeight=input.height;
  var autoCW=Math.max(1,Math.min(outputWidth,roundEven(f(f(input.screenPixelWidth || outputWidth)*f(input.profile.composeScale)))));
  var autoCH=Math.max(1,Math.min(outputHeight,roundEven(f(f(autoCW*f(outputHeight))/f(outputWidth)))));
  var cw = input.coverageWidth || (input.stage==='coverage' ? input.width : autoCW);
  var ch = input.coverageHeight || (input.stage==='coverage' ? input.height : autoCH);
  var block = run({op:'pixelResource',name:'block'});
  var pieces = input.pieces || [];
  var direction = input.profile.composeDisplaceDirection;
  var len = f(Math.sqrt(f(f(f(direction[0])*f(direction[0])) + f(f(direction[1])*f(direction[1])))));
  var dx = len > 0.00001 ? f(f(direction[0])/len) : f(0.70710678);
  var dy = len > 0.00001 ? f(f(direction[1])/len) : f(0.70710678);
  var span = f(Math.max(0.001, f(input.loopPeriod)));
  var step = f(f(f(input.profile.composeDisplaceSpeed) / f(20)) / f(60));
  var angle = f(f(f(2)*pi) * f(f(input.time)/span));
  var radius = f(f(step * f(input.frameCount)) / f(f(2)*pi));
  var ca = f(Math.cos(angle)), sa = f(Math.sin(angle));
  var driftX = f(ca*radius), driftY = f(sa*radius), orthoX = f(-sa*radius), orthoY = f(ca*radius);
  var previous = pieces.length ? null : fill(cw,ch,1);
  for (var first = 0; first < pieces.length; first += 4) {
    var program = [], uniforms = [];
    function emit(name, a, b) {
      if (b !== undefined) program.push([name,a,b]);
      else if (a !== undefined) program.push([name,a]);
      else program.push([name]);
    }
    function uniform(value) { var index = uniforms.length; uniforms.push(value); emit('uniform',index); }
    function g(i) { emit('get',i); }
    function pixel() { emit('u'); emit('v'); }
    function driftNoise(ox,oy,target) {
      g(0); uniform(input.profile.composeDisplaceMapScale[0]); emit('mul'); uniform(ox); emit('add');
      g(1); uniform(input.profile.composeDisplaceMapScale[1]); emit('mul'); uniform(oy); emit('add');
      emit('sample',0,0); emit('c',0.5); emit('sub'); emit('set',target);
    }
    pixel(); emit('set',1); emit('set',0);
    driftNoise(driftX,driftY,2); driftNoise(orthoX,orthoY,3);
    function coordinate(base, d, p, cells, total, result) {
      g(base); g(2); uniform(d); emit('mul'); g(3); uniform(p); emit('mul'); emit('add');
      uniform(input.profile.composeDisplaceStrength); emit('mul'); emit('add');
      uniform(cells); emit('mul'); emit('floor'); emit('c',0.5); emit('add'); uniform(cells); emit('div');
      uniform(total); emit('mul'); uniform(f(f(total)*f(0.5))); emit('sub'); emit('set',result);
    }
    coordinate(0,dx,-dy,cw,input.totalWidth,4);
    coordinate(1,dy,dx,ch,input.totalHeight,5);
    if (previous === null) emit('c',0); else { pixel(); emit('sample',1,0); }
    var sources = [{handle:block,filter:'point',wrap:'mirror'}];
    if (previous !== null) sources.push({handle:previous,filter:'point',wrap:'clamp'});
    for (var i=first; i<Math.min(first+4,pieces.length); i++) {
      var part=pieces[i], radians=f(f(part.rotation)*f(pi/f(180)));
      var c=f(Math.cos(radians)), s=f(Math.sin(radians));
      // localX and localY exactly match the old CPU shape transform.
      g(4); uniform(part.x); emit('sub'); uniform(c); emit('mul');
      g(5); uniform(part.y); emit('sub'); uniform(s); emit('mul'); emit('add'); emit('set',6);
      g(4); uniform(part.x); emit('sub'); uniform(-s); emit('mul');
      g(5); uniform(part.y); emit('sub'); uniform(c); emit('mul'); emit('add'); emit('set',7);
      if (part.circle) {
        g(6); uniform(Math.max(0.0001,part.hx)); emit('div'); emit('set',8);
        g(7); uniform(Math.max(0.0001,part.hy)); emit('div'); emit('set',9);
        g(8); g(8); emit('mul'); g(9); g(9); emit('mul'); emit('add'); emit('c',1); emit('le');
      } else {
        g(6); emit('abs'); uniform(part.hx); emit('le');
        g(7); emit('abs'); uniform(part.hy); emit('le'); emit('mul');
      }
      emit('max');
    }
    previous = run({op:'pixelRun',width:cw,height:ch,program:program,sources:sources,uniforms:uniforms});
  }
  if (input.stage === 'coverage') {
    var alpha=fill(input.width,input.height,1);
    return [run({op:'pixelPack',channels:[previous,previous,previous,alpha]})];
  }

  var ew=input.effectsWidth || (input.stage==='effects' ? input.width : Math.max(1,Math.min(outputWidth,Math.max(2,cw*2))));
  var eh=input.effectsHeight || (input.stage==='effects' ? input.height : Math.max(1,Math.min(outputHeight,Math.max(2,ch*2))));
  function src(handle) { return {handle:handle,filter:'point',wrap:'clamp'}; }
  function kernel(code, sources) { return run({op:'pixelRun',width:ew,height:eh,program:code,sources:sources,uniforms:[]}); }
  function sample(source, x, y) {
    return [['u'],['c',f(x/ew)],['add'],['v'],['c',f(y/eh)],['add'],['sample',source,0]];
  }
  function maxNine(handle) {
    var code=[], initial=true;
    for (var y=-1;y<=1;y++) for (var x=-1;x<=1;x++) {
      code=code.concat(sample(0,x,y));
      if (!initial) code.push(['max']);
      initial=false;
    }
    return kernel(code,[src(handle)]);
  }
  var maximum=maxNine(previous);
  var edgeCode=sample(1,0,0).concat(sample(0,0,0),[['sub'],['clamp']]);
  var edges=kernel(edgeCode,[src(previous),src(maximum)]);
  var priorMaximum=null, accumulated=null, weights=input.profile.glowWeights;
  for (var pass=0;pass<weights.length;pass++) {
    if (pass>0) maximum=maxNine(priorMaximum);
    // max - current, suppressed inside original coverage, then weighted and accumulated.
    var ring=sample(1,0,0).concat(sample(pass===0?0:2,0,0),[['sub'],['clamp'],['c',1]],
      sample(0,0,0),[['sub'],['mul'],['c',weights[pass]],['mul']]);
    var ringSources=[src(previous),src(maximum)];
    if (pass>0) { ringSources.push(src(priorMaximum)); ringSources.push(src(accumulated)); ring=ring.concat(sample(3,0,0),[['add']]); }
    ring.push(['clamp']);
    accumulated=kernel(ring,ringSources);
    priorMaximum=maximum;
  }
  if (input.stage === 'effects') {
    var coverageAtEffects=kernel(sample(0,0,0),[src(previous)]), opaque=fill(ew,eh,1);
    return [run({op:'pixelPack',channels:[edges,accumulated,coverageAtEffects,opaque]})];
  }

  var texW=input.width, texH=input.height, cellW=Math.max(1,Math.ceil(texW/6)), cellH=Math.max(1,Math.ceil(texH/6));
  function clamp01(x) { return Math.max(0,Math.min(1,x)); }
  function repeat(x,length) { return f(Math.max(0,Math.min(length,f(x-f(Math.floor(f(x/length))*length))))); }
  function rgbToHsv(c) {
    var hi=Math.max(c[0],c[1],c[2]), lo=Math.min(c[0],c[1],c[2]), d=f(hi-lo), h=0;
    if (d>1e-10) {
      if (hi===c[0]) h=repeat(f(f(c[1]-c[2])/d),6);
      else if (hi===c[1]) h=f(f(f(c[2]-c[0])/d)+2);
      else h=f(f(f(c[0]-c[1])/d)+4);
      h=repeat(f(h/6),1);
    }
    return [h,hi<=1e-10?0:f(d/hi),hi];
  }
  function hsvToRgb(hsv) {
    var offsets=[1,f(2/3),f(1/3)], result=[];
    for(var k=0;k<3;k++) {
      var p=clamp01(f(Math.abs(f(f(repeat(f(hsv[0]+offsets[k]),1)*6)-3))-1));
      result.push(f(hsv[2]*f(1+f(hsv[1]*f(p-1)))));
    }
    return result;
  }
  function variant(color,saturation,value) {
    var hsv=rgbToHsv(color);
    hsv[1]=clamp01(f(hsv[1]*f(saturation))); hsv[2]=clamp01(f(hsv[2]*f(value)));
    return hsvToRgb(hsv);
  }
  var prof=input.profile, opts=input.options, color=opts.edgeColor, scene=opts.sceneColor;
  var nfFill=variant(color,1,prof.fillValue), nfEdge=variant(color,1,1);
  var nfGlow=variant(color,prof.glowSaturation,1), nfSpark=variant(color,prof.sparkSaturation,1), sceneHsv=rgbToHsv(scene);
  var fillOpacity=clamp01(opts.fillOpacity), fillStrength=clamp01(opts.fillStrength<=0?prof.fillStrength:opts.fillStrength);
  var exposure=Math.max(0,Math.min(2,opts.exposure)), edgeOpacity=Math.max(0,opts.edgeOpacity), glowOpacity=Math.max(0,opts.glowOpacity);
  var sparkOpacity=f(f(prof.sparkMapOpacity)*f(Math.max(0,opts.sparkOpacity)));
  var ad=prof.activeDisplaceDirection;
  var adLen=f(Math.sqrt(f(f(f(ad[0])*f(ad[0]))+f(f(ad[1])*f(ad[1])))));
  var ax=adLen>0.00001?f(f(ad[0])/adLen):f(0.70710678), ay=adLen>0.00001?f(f(ad[1])/adLen):f(0.70710678);
  var activeStep=f(f(f(prof.activeDisplaceSpeed)/f(20))/f(60));
  var activeRadius=f(f(activeStep*f(input.frameCount))/f(f(2)*pi));
  var adriftX=f(ca*activeRadius), adriftY=f(sa*activeRadius), aorthoX=f(-sa*activeRadius), aorthoY=f(ca*activeRadius);
  function arbitraryKernel(w,h,code,sources,uniforms) { return run({op:'pixelRun',width:w,height:h,program:code,sources:sources,uniforms:uniforms || []}); }
  function cellSample(ox,oy) {
    var code=[['x'],['c',0.5],['add'],['c',6],['mul'],['c',texW],['div'],['c',prof.activeDisplaceMapScale[0]],['mul'],['uniform',0],['add'],
      ['y'],['c',0.5],['add'],['c',6],['mul'],['c',texH],['div'],['c',prof.activeDisplaceMapScale[1]],['mul'],['uniform',1],['add'],['sample',0,0]];
    return arbitraryKernel(cellW,cellH,code,[{handle:block,filter:'point',wrap:'mirror'}],[ox,oy]);
  }
  var cellA=cellSample(adriftX,adriftY), cellB=cellSample(aorthoX,aorthoY);
  function rawSample(source) { return [['u'],['v'],['sample',source,0]]; }
  function alphaCode() {
    return rawSample(0).concat([['set',0]],rawSample(1),[['set',1]],rawSample(2),[['set',2]],
      [['get',0],['c',fillOpacity],['mul'],['get',1],['c',edgeOpacity],['mul'],['add'],
       ['get',2],['c',1],['get',1],['get',0],['add'],['sub'],['mul'],['c',glowOpacity],['mul'],['add']]);
  }
  var baseSources=[src(previous),src(edges),{handle:accumulated,filter:'linear',wrap:'clamp'}];
  var alphaPlane=arbitraryKernel(texW,texH,alphaCode(),baseSources);
  var point=run({op:'pixelResource',name:'point'});
  var channels=[];
  for (var channel=0;channel<3;channel++) {
    var code=[], uniforms=[];
    function e(name,a,b) { if(b!==undefined)code.push([name,a,b]);else if(a!==undefined)code.push([name,a]);else code.push([name]); }
    function k(value) { e('c',value); }
    function get(index) { e('get',index); }
    function read(source) { e('u');e('v');e('sample',source,0); }
    function cellRead(source) {
      e('x');k(6);e('div');e('floor');k(.5);e('add');k(cellW);e('div');
      e('y');k(6);e('div');e('floor');k(.5);e('add');k(cellH);e('div');e('sample',source,0);
    }
    read(0);e('set',0); read(1);e('set',1); read(2);e('set',2);
    cellRead(3);e('set',3); cellRead(4);e('set',4);
    get(3);get(4);e('add');k(.5);e('mul');e('set',5); // displacement average
    get(3);k(.5);e('sub');k(ax);e('mul');get(4);k(.5);e('sub');k(-ay);e('mul');e('add');e('set',6);
    get(3);k(.5);e('sub');k(ay);e('mul');get(4);k(.5);e('sub');k(ax);e('mul');e('add');e('set',7);
    e('u');k(prof.activeSparkMapScale[0]);e('mul');get(6);k(prof.activeSparkDisplaceIntensity);e('mul');e('add');
    e('v');k(prof.activeSparkMapScale[1]);e('mul');get(7);k(prof.activeSparkDisplaceIntensity);e('mul');e('add');e('sample',5,0);
    get(5);e('mul');k(sparkOpacity);e('mul');e('set',8); // sparkle value
    k(nfFill[channel]);get(5);k(prof.displaceBlendIntensity);e('mul');e('sub');e('set',9); // fill base
    // Scene HSV plus the three spark channels, followed by the reference HSV to RGB and overlay.
    k(sceneHsv[0]);k(nfSpark[0]);get(8);e('mul');k(prof.activeSparkHueShiftAmount);e('mul');e('add');e('set',10);
    k(sceneHsv[1]);k(nfSpark[1]);get(8);e('mul');k(prof.activeSparkHueShiftAmount);e('mul');e('add');e('set',11);
    k(sceneHsv[2]);k(nfSpark[2]);get(8);e('mul');k(prof.activeSparkHueShiftAmount);e('mul');e('add');e('set',12);
    get(10);k([1,f(2/3),f(1/3)][channel]);e('add');e('fract');k(6);e('mul');k(3);e('sub');e('abs');k(1);e('sub');e('clamp');
    k(1);e('sub');get(11);e('mul');k(1);e('add');get(12);e('mul');e('set',13); // processed component
    k(nfSpark[channel]);get(8);e('mul');e('set',14); // spark component
    get(13);k(.5);e('ge'); // overlay branch condition
    k(1);k(2);k(1);get(13);e('sub');e('mul');k(1);get(14);e('sub');e('mul');e('sub');
    k(2);get(13);e('mul');get(14);e('mul');e('select');e('clamp');e('set',13);
    get(9);k(fillStrength);get(13);get(9);e('sub');e('mul');e('add');e('set',13); // spark-lit fill
    get(9);k(fillStrength);k(Math.max(0,f(f(2*scene[channel])-1)));get(9);e('sub');e('mul');e('add');e('set',14); // flat fill
    get(8);k(.0001);e('le');get(14);get(13);e('select');e('set',13);
    if (exposure>0) { get(13);k(nfEdge[channel]);get(13);e('sub');k(exposure);e('mul');e('add');e('set',13); }
    get(13);get(0);e('mul');k(nfEdge[channel]);get(1);k(edgeOpacity);e('mul');e('mul');e('add');
    k(nfGlow[channel]);get(2);k(1);get(1);get(0);e('add');e('sub');e('mul');k(glowOpacity);e('mul');e('mul');e('add');e('set',14);
    read(6);e('set',15);
    get(15);k(.0001);e('le');get(14);get(14);get(15);e('div');e('select');
    channels.push(arbitraryKernel(texW,texH,code,baseSources.concat([src(cellA),src(cellB),
      {handle:point,filter:'point',wrap:'repeat'},src(alphaPlane)])));
  }
  var packed=run({op:'pixelPack',channels:[channels[0],channels[1],channels[2],alphaPlane]});
  var glitch=clamp01(opts.glitch || 0);
  if (glitch>0) {
    function hash(a,b,c) {
      var h=(Math.imul(a,374761393)+Math.imul(b,668265263)+Math.imul(c,2147483647))>>>0;
      h=Math.imul(h^(h>>>13),1274126177)>>>0;
      return (h^(h>>>16))>>>0;
    }
    var frameSeed=(Math.imul(input.frameIndex || 0,7919)+Math.imul(input.seed || 0,2654435761))>>>0;
    var shifts=[], rowSpan=Math.max(1,roundEven(f(f(f(glitch)*f(texW))*f(.18))));
    for(var row=0;row<texH;row++) {
      var h=hash(0x5bd1,row,frameSeed), shift=0;
      if (f(f(h&65535)/f(65535)) < f(f(glitch)*f(.35)))
        shift=roundEven(f(f(f(f((h>>>16)&65535)/f(65535))*f(2))-f(1)))*rowSpan;
      shifts.push(shift);
    }
    packed=run({op:'pixelShiftRows',source:packed,shifts:shifts});
  }
  return [packed];
}
