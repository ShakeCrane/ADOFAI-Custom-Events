// NoiseField — driver for the host's field primitive.
//
// All animation state is derived from t (seconds since this event's tile). The host keeps
// no cursor of its own, so pause, restart and checkpoint jumps stay in sync with the song.

function vec(v, fx, fy) {
  if (v && v.length !== undefined && v.length >= 2) return [Number(v[0]), Number(v[1])];
  if (v && v.x !== undefined) return [Number(v.x), Number(v.y)];
  return [fx, fy];
}

function num(v, fallback) {
  var n = Number(v);
  return isFinite(n) ? n : fallback;
}

function create(ctx, params) {
  var key = "noisefield";

  // Geometry is the decoration's. Where it sits, how big it is, how it is rotated, what
  // colour and opacity it has, and which tag a mover looks for all live on the decoration
  // object, which the editor and the engine's own tweens write. Sending them from here
  // would make the script fight the engine: the value the author dragged would be
  // overwritten every frame by the value read at load.
  var p = {
    shape: params.shape || "Rect",
    feather: num(params.feather, 0),

    brightness: num(params.noiseBrightness, 1),
    saturation: num(params.noiseSaturation, 1),
    sampleRate: num(params.noiseSampleRate, 1),
    lineOpacity: num(params.lineOpacity, 0.35),
    frameCount: Math.max(2, Math.round(num(params.noiseFrameCount, 8))),
    evolveSpeed: num(params.noiseEvolveSpeed, 12),
    moveSpeed: vec(params.noiseMoveSpeed, 0.15, 0),
    tiling: vec(params.noiseScale, 1, 1),
    color: params.color || "ff5454",

    // Layer parameters. These must be forwarded or the host falls back to its own
    // defaults — with displace at 0 the coastline band collapses and the field renders as
    // plain geometry, whatever the texture code does.
    fillOpacity: num(params.fillOpacity, 0.667),
    edgeColor: params.edgeColor || "ff5454",
    edgeOpacity: num(params.edgeOpacity, 0.8),
    sparkColor: params.sparkColor || "ff5a5a",
    sparkOpacity: num(params.sparkOpacity, 1),
    glowColor: params.glowColor || "ff5454",
    glowOpacity: num(params.glowOpacity, 0.8),
    glowPasses: num(params.glowPasses, 5),
    displace: num(params.displace, 0.35),
    preDuration: num(params.preDuration, 0),

    // Coastline controls. Same story as above: anything not forwarded is replaced by the
    // host's default, so these four have to travel with every call.
    edgeDistance: num(params.edgeDistance, 0.7),
    edgeDetail: num(params.edgeDetail, 8),
    edgeThickness: Math.max(1, Math.round(num(params.edgeThickness, 1))),
    edgeMargin: num(params.edgeMargin, 0.6),
    edgeFragment: num(params.edgeFragment, 1),
    glitch: num(params.glitchRange, 0)
  };

  var alive = false;

  function push(t) {
    var frame = Math.floor(t * p.evolveSpeed) % p.frameCount;
    if (frame < 0) frame += p.frameCount;

    fx.field(key, {
      shape: p.shape,
      feather: p.feather,

      frame: frame,
      frameCount: p.frameCount,
      uv: [t * p.moveSpeed[0], t * p.moveSpeed[1]],
      brightness: p.brightness,
      saturation: p.saturation,
      sampleRate: p.sampleRate,
      lineOpacity: p.lineOpacity,
      tiling: p.tiling,
      color: p.color,

      // Layer parameters. The host reads these from the call, not from the manifest, so
      // omitting them here silently renders with the host defaults.
      fillOpacity: p.fillOpacity,
      edgeColor: p.edgeColor,
      edgeOpacity: p.edgeOpacity,
      sparkColor: p.sparkColor,
      sparkOpacity: p.sparkOpacity,
      glowColor: p.glowColor,
      glowOpacity: p.glowOpacity,
      glowPasses: p.glowPasses,
      displace: p.displace,
      pre: t < p.preDuration,

      edgeDistance: p.edgeDistance,
      edgeDetail: p.edgeDetail,
      edgeThickness: p.edgeThickness,
      edgeMargin: p.edgeMargin,
      edgeFragment: p.edgeFragment,
      glitch: p.glitch
    });
  }

  return {
    update: function (t) {
      if (t < 0) return;
      alive = true;
      push(t);
    },
    dispose: function () {
      if (alive) fx.fieldDestroy(key);
      alive = false;
    }
  };
}
