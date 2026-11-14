// SetNoiseFieldState — tag-scoped visibility and intensity, eased.
//
// "Active" in the original effect is not a separate render mode: it is the intensity of
// the noise layers. Mapping it to alpha keeps the parameter honest — one number that
// visibly controls how strongly the field reads.

function num(v, fallback) {
  var n = Number(v);
  return isFinite(n) ? n : fallback;
}

function bool(v, fallback) {
  if (v === true || v === "true" || v === 1) return true;
  if (v === false || v === "false" || v === 0) return false;
  return fallback;
}

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
    default: return 1 - (1 - t) * (1 - t);
  }
}

function create(ctx, params) {
  var tag = params.tag || "";
  var visible = bool(params.visible, true);
  var intensity = num(params.intensity, 1);
  var duration = Math.max(0.0001, num(params.duration, 0.5));
  var kind = params.ease || "OutQuad";
  var done = false;

  return {
    update: function (t) {
      if (t < 0 || done) return;

      var k = ease(kind, t / duration);

      // Fade the intensity toward the target; visibility switches at the halfway point
      // so a fade-out does not pop the field off at full brightness.
      var targetAlpha = visible ? intensity : 0;
      var alpha = 1 + (targetAlpha - 1) * k;
      var visibility = visible ? 1 : (k >= 0.5 ? 2 : 0);

      fx.fieldState({
        tag: tag,
        alpha: alpha,
        visibility: visibility,
        clear: false
      });

      if (t >= duration) done = true;
    },
    dispose: function () {
      if (!done) fx.fieldState({ tag: tag, clear: true });
    }
  };
}
