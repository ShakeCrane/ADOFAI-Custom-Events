// Pulse — the continuous-effect reference.
//
// Unlike CameraWarp and ScreenShake, this event does real work on every frame: it
// derives position, scale and alpha from t and pushes them into a host-managed object.
// That is the shape most non-trivial effects take.
//
// Things worth copying from here:
//   - t is relative to the tile and is NOT monotonic; it resets on restart.
//   - the object key includes the uid, so two instances never fight over one object.
//   - dispose() destroys the object; the host also reclaims it on teardown.

function create(ctx, params) {
  var key = "pulse:" + ctx.floorSeq;
  var started = false;
  var duration = params.duration > 0 ? params.duration : 1;
  var cycles = params.cycles > 0 ? params.cycles : 1;

  return {
    update: function (t) {
      if (t < 0) return;

      if (t > duration) {
        if (started) {
          started = false;
          fx.destroy(key);
        }
        return;
      }

      if (!started) {
        started = true;
        fx.create(key, { color: params.color, scale: params.size, alpha: 0 });
      }

      // Normalised progress through the event's own lifetime.
      var p = t / duration;

      // Breathing: a sine over the requested cycle count.
      var wave = Math.sin(p * cycles * Math.PI * 2);

      // Drift sideways and back, scaled by the drift parameter.
      var x = Math.sin(p * Math.PI * 2) * params.drift;
      var y = Math.cos(p * Math.PI * 2) * params.drift * 0.5;

      fx.set(key, {
        color: params.color,
        x: x,
        y: y,
        scale: params.size * (1 + wave * 0.25),
        alpha: 0.35 * (0.6 + 0.4 * wave),
        angle: p * 360
      });
    },

    dispose: function () {
      if (started) fx.destroy(key);
      started = false;
    }
  };
}
