// Camera Warp — P1 reference event.
// The host calls create() once per placed instance, then update(t) each frame
// while the instance is live. Returning { } is fine; effects are requested
// through the fx object handed in by the host.

function create(ctx, params) {
  var started = false;
  var duration = Number(params.duration) * (ctx.tileSecondsPerBeat || ctx.secondsPerBeat);

  return {
    update: function (t) {
      // t is seconds since the tile this event sits on was reached.
      if (started || t < 0 || !(duration > t)) return;
      started = true;
      var remaining = duration - t;
      var weight = remaining / duration;
      fx.flash({ color: params.color, duration: remaining, alpha: 0.35 * params.strength * weight });
      fx.shake({ strength: 0.05 * params.strength * weight, duration: remaining });
    }
  };
}
