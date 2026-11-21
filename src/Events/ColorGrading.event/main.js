// Color Grading — event script.
//
// The shader does the colour science; this script's job is to turn the event's parameters
// into the uniform block Core hands to the shader, and to run the fade the timeline asks for.
//
// Nothing here re-sends a value it has already sent. A grade that is not animating produces
// exactly one host call for the whole event, and an animating one produces one per frame
// while it moves. That is what keeps a screen-wide effect free at rest.

var NEUTRAL_CURVE = '0,0;1,1';
var NEUTRAL_HUE_CURVE = '0,0.5;1,0.5';

var EASES = {
  Linear: function (p) { return p; },
  InSine: function (p) { return 1 - Math.cos(p * Math.PI * 0.5); },
  OutSine: function (p) { return Math.sin(p * Math.PI * 0.5); },
  InOutSine: function (p) { return -(Math.cos(Math.PI * p) - 1) * 0.5; },
  InQuad: function (p) { return p * p; },
  OutQuad: function (p) { return 1 - (1 - p) * (1 - p); },
  InOutQuad: function (p) { return p < 0.5 ? 2 * p * p : 1 - Math.pow(-2 * p + 2, 2) * 0.5; },
  InCubic: function (p) { return p * p * p; },
  OutCubic: function (p) { return 1 - Math.pow(1 - p, 3); },
  InOutCubic: function (p) { return p < 0.5 ? 4 * p * p * p : 1 - Math.pow(-2 * p + 2, 3) * 0.5; },
  OutExpo: function (p) { return p >= 1 ? 1 : 1 - Math.pow(2, -10 * p); }
};

var BUNDLED_LUTS = {
  BundledTealOrange: 'assets/luts/teal-orange.cube',
  BundledBleachBypass: 'assets/luts/bleach-bypass.cube',
  BundledWarmFilm: 'assets/luts/warm-film.cube'
};

function num(value, fallback) {
  var n = Number(value);
  return isFinite(n) ? n : fallback;
}

function parseCurve(text, fallbackFlat) {
  if (typeof text !== 'string') return fallbackFlat;
  var parts = text.split(';');
  var flat = [];
  for (var i = 0; i < parts.length; i++) {
    var pair = parts[i].split(',');
    if (pair.length < 2) continue;
    var x = num(pair[0], NaN), y = num(pair[1], NaN);
    if (!isFinite(x) || !isFinite(y)) continue;
    flat.push(x, y);
    if (flat.length >= 32) break;
  }
  return flat.length >= 4 ? flat : fallbackFlat;
}

// A curve is "identity" when every point sits on y = x. Detecting that lets the shader skip
// the table lookup, and it is what makes the default state a true no-op rather than a
// pass-through that still costs six texture fetches per pixel.
function isIdentityCurve(flat) {
  for (var i = 0; i + 1 < flat.length; i += 2) {
    if (Math.abs(flat[i] - flat[i + 1]) > 0.002) return false;
  }
  return true;
}

function isFlatHalf(flat) {
  for (var i = 0; i + 1 < flat.length; i += 2) {
    if (Math.abs(flat[i + 1] - 0.5) > 0.002) return false;
  }
  return true;
}

function parseHslBands(text) {
  var bands = [];
  var parts = typeof text === 'string' ? text.split(';') : [];
  for (var i = 0; i < parts.length && bands.length < 8; i++) {
    var fields = parts[i].split(',');
    if (fields.length < 3) { bands.push([0, 0, 0, 1]); continue; }
    bands.push([num(fields[0], 0), num(fields[1], 0), num(fields[2], 0), 1]);
  }
  while (bands.length < 8) bands.push([0, 0, 0, 1]);
  return bands;
}

function clamp01(v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

function vec2(value) {
  if (value && typeof value.length === 'number' && value.length >= 2) {
    return [num(value[0], 0), num(value[1], 0)];
  }
  return [0, 0];
}

function create(ctx, params) {
  var easeName = typeof params.ease === 'string' ? params.ease : 'OutCubic';
  var ease = EASES[easeName] || EASES.OutCubic;
  var seconds = Math.max(0, num(params.duration, 0)) * (ctx.tileSecondsPerBeat || ctx.secondsPerBeat || 0.5);
  var mode = params.mode === 'Stack' ? 'stack' : 'replace';

  var master = parseCurve(params.curveMaster, [0, 0, 1, 1]);
  var red = parseCurve(params.curveRed, [0, 0, 1, 1]);
  var green = parseCurve(params.curveGreen, [0, 0, 1, 1]);
  var blue = parseCurve(params.curveBlue, [0, 0, 1, 1]);
  var hueHue = parseCurve(params.hueVsHue, [0, 0.5, 1, 0.5]);
  var hueSat = parseCurve(params.hueVsSat, [0, 0.5, 1, 0.5]);
  var hueLuma = parseCurve(params.hueVsLuma, [0, 0.5, 1, 0.5]);

  var curvesUsed = !isIdentityCurve(master) || !isIdentityCurve(red) ||
    !isIdentityCurve(green) || !isIdentityCurve(blue);
  var hueUsed = !isFlatHalf(hueHue) || !isFlatHalf(hueSat) || !isFlatHalf(hueLuma);

  var lutPath = '';
  if (typeof params.lutPreset === 'string' && BUNDLED_LUTS[params.lutPreset]) {
    lutPath = BUNDLED_LUTS[params.lutPreset];
  } else if (typeof params.lutFile === 'string') {
    lutPath = params.lutFile.replace(/^\s+|\s+$/g, '');
  }

  var lift = vec2(params.liftXY), gammaWheel = vec2(params.gammaXY);
  var gain = vec2(params.gainXY), offset = vec2(params.offsetXY);
  var hslBands = parseHslBands(params.hslBands);

  var base = {
    mode: mode,
    order: 0,
    fade: seconds,
    enabled: params.enabled !== false,
    floats: {
      _Strength: clamp01(num(params.strength, 1)),
      _Exposure: num(params.exposure, 0),
      _Brightness: num(params.brightness, 0),
      _Contrast: num(params.contrast, 0),
      _Saturation: num(params.saturation, 0),
      _Vibrance: num(params.vibrance, 0),
      _Temperature: num(params.temperature, 0),
      _Tint: num(params.tint, 0),
      _Highlights: num(params.highlights, 0),
      _Shadows: num(params.shadows, 0),
      _Whites: num(params.whites, 0),
      _Blacks: num(params.blacks, 0),
      _Gamma: Math.max(0.05, num(params.gamma, 1)),
      _CurveEnabled: curvesUsed ? 1 : 0,
      _HueCurveEnabled: hueUsed ? 1 : 0,
      _LutStrength: clamp01(num(params.lutStrength, 1))
    },
    vectors: {
      _GainRGB: [num(params.gainR, 1), num(params.gainG, 1), num(params.gainB, 1), 1],
      _OffsetRGB: [num(params.offsetR, 0), num(params.offsetG, 0), num(params.offsetB, 0), 0],
      _Lift: [lift[0], lift[1], num(params.liftL, 0), 0],
      _GammaWheel: [gammaWheel[0], gammaWheel[1], num(params.gammaL, 0), 0],
      _GainWheel: [gain[0], gain[1], num(params.gainL, 0), 0],
      _OffsetWheel: [offset[0], offset[1], num(params.offsetL, 0), 0]
    },
    vectorArrays: { _HslBands: hslBands },
    curves: {
      _CurveTex: { master: master, red: red, green: green, blue: blue },
      _HueCurves: { master: null, red: hueHue, green: hueSat, blue: hueLuma }
    },
    lut: { enabled: lutPath.length > 0, path: lutPath }
  };

  var lastPayload = null;
  var settled = false;

  function payload(weight) {
    base.weight = weight;
    return JSON.stringify(base);
  }

  return {
    update: function (t) {
      if (t < 0) return;

      var weight = 1;
      if (seconds > 0.0001 && t < seconds) {
        weight = ease(clamp01(t / seconds));
      } else if (seconds > 0.0001 && settled) {
        // Already at full weight; nothing about the effect can change from here.
        return;
      }

      var body = payload(seconds > 0.0001 ? weight : 1);
      if (body === lastPayload) return;

      var result = fx.screen(base);
      if (result && result.ok) {
        lastPayload = body;
        if (seconds <= 0.0001 || t >= seconds) settled = true;
      } else {
        // Retry on the next frame rather than leaving the screen ungraded for the event.
        lastPayload = null;
      }
    },

    dispose: function () {
      fx.screenClear();
      lastPayload = null;
      settled = false;
    }
  };
}
