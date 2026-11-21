// Screen Shake — verifies that two instances of the same event keep separate state.
// Each instance owns its own engine and its own closure, so `fired` is per-instance.

function create(ctx, params) {
  var fired = false;
  var duration = Number(params.duration) * (ctx.tileSecondsPerBeat || ctx.secondsPerBeat);

  return {
    update: function (t) {
      if (t < 0 || fired || !(duration > t)) return;
      fired = true;

      var remaining = duration - t;
      fx.shake({ strength: params.strength * remaining / duration, duration: remaining });
    }
  };
}
