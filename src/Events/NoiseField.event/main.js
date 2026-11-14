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

function bool(v, fallback) {
  if (v === true || v === "true" || v === 1) return true;
  if (v === false || v === "false" || v === 0) return false;
  return fallback;
}

function create(ctx, params) {
  var key = "noisefield";

  var p = {
    shape: params.shape || "Rect",
    size: vec(params.size, 4, 3),
    radius: num(params.radius, 2),
    relativeTo: params.relativeTo || "Tile",
    anchor: params.anchor || "Center",
    position: vec(params.positionOffset, 0, 0),
    rotation: num(params.rotation, 0),
    lockRotation: bool(params.lockRotation, false),
    lockSize: bool(params.lockSize, false),
    depth: num(params.depth, 0),
    parallax: vec(params.parallax, 1, 1),
    tag: params.tag || "",
    visible: bool(params.visible, true),
    opacity: num(params.opacity, 100) / 100,
    feather: num(params.feather, 0),

    brightness: num(params.noiseBrightness, 1),
    saturation: num(params.noiseSaturation, 1),
    sampleRate: num(params.noiseSampleRate, 1),
    lineOpacity: num(params.lineOpacity, 0.35),
    frameCount: Math.max(2, Math.round(num(params.noiseFrameCount, 8))),
    evolveSpeed: num(params.noiseEvolveSpeed, 12),
    moveSpeed: vec(params.noiseMoveSpeed, 0.15, 0),
    tiling: vec(params.noiseScale, 1, 1),
    color: params.color || "ff2020"
  };

  var alive = false;

  function push(t) {
    var frame = Math.floor(t * p.evolveSpeed) % p.frameCount;
    if (frame < 0) frame += p.frameCount;

    fx.field(key, {
      shape: p.shape,
      size: p.size,
      radius: p.radius,
      relativeTo: p.relativeTo,
      anchor: p.anchor,
      position: p.position,
      rotation: p.rotation,
      lockRotation: p.lockRotation,
      lockSize: p.lockSize,
      depth: p.depth,
      parallax: p.parallax,
      visible: p.visible,
      opacity: p.opacity,
      feather: p.feather,

      frame: frame,
      frameCount: p.frameCount,
      uv: [t * p.moveSpeed[0], t * p.moveSpeed[1]],
      brightness: p.brightness,
      saturation: p.saturation,
      sampleRate: p.sampleRate,
      lineOpacity: p.lineOpacity,
      tiling: p.tiling,
      color: p.color
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
