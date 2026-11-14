// MoveNoiseField — eased, tag-scoped movement of live noise fields.
//
// Values written to the host are absolute: they are recomputed from the song clock on
// every update. Nothing accumulates, so pausing and seeking are automatically correct.

function num(v, fallback) {
  var n = Number(v);
  return isFinite(n) ? n : fallback;
}

function vec(v, fx, fy) {
  if (v && v.length !== undefined && v.length >= 2) return [Number(v[0]), Number(v[1])];
  if (v && v.x !== undefined) return [Number(v.x), Number(v.y)];
  return [fx, fy];
}

// Same curve set the game uses for its own eased events.
function ease(kind, t) {
  t = t < 0 ? 0 : t > 1 ? 1 : t;
  switch (kind) {
    case "Linear": return t;
    case "InQuad": return t * t;
    case "OutQuad": return 1 - (1 - t) * (1 - t);
    case "InOutQuad": return t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
    case "InCubic": return t * t * t;
    case "OutCubic": return 1 - Math.pow(1 - t, 3);
    case "InOutCubic": return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
    case "InOutSine": return -(Math.cos(Math.PI * t) - 1) / 2;
    case "OutBack":
      var c1 = 1.70158, c3 = c1 + 1;
      return 1 + c3 * Math.pow(t - 1, 3) + c1 * Math.pow(t - 1, 2);
    default: return 1 - (1 - t) * (1 - t);
  }
}

function create(ctx, params) {
  var tag = params.tag || "";
  var duration = Math.max(0.0001, num(params.duration, 1));
  var kind = params.ease || "OutQuad";
  var offset = vec(params.offset, 0, 0);
  var rotation = num(params.rotation, 0);
  var scale = num(params.scale, 1);
  var alpha = num(params.alpha, 1);
  var done = false;

  function push(t) {
    var k = ease(kind, t / duration);
    fx.fieldMove({
      tag: tag,
      offset: [offset[0] * k, offset[1] * k],
      rotation: rotation * k,
      scale: 1 + (scale - 1) * k,
      alpha: 1 + (alpha - 1) * k,
      clear: false
    });
  }

  return {
    update: function (t) {
      if (t < 0 || done) return;
      push(t);
      if (t >= duration) done = true;
    },
    dispose: function () {
      // Leaving the range must not leave the field displaced.
      if (!done) fx.fieldMove({ tag: tag, clear: true });
    }
  };
}
