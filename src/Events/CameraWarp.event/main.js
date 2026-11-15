// Camera Warp — P1 reference event.
// The host calls create() once per placed instance, then update(t) each frame
// while the instance is live. Returning { } is fine; effects are requested
// through the fx object handed in by the host.

var _started = false;
var _elapsed = 0;

function create(ctx, params) {
  _started = false;
  _elapsed = 0;

  return {
    update: function (t) {
      // t is seconds since the tile this event sits on was reached.
      if (t < 0) return;

      var durationSec = params.duration * ctx.secondsPerBeat;
      if (t > durationSec) return;

      if (!_started) {
        _started = true;
        fx.flash({ color: params.color, duration: durationSec, alpha: 0.35 * params.strength });
        fx.shake({ strength: 0.05 * params.strength, duration: durationSec });
      }
    },

    dispose: function () {
      _started = false;
    }
  };
}
