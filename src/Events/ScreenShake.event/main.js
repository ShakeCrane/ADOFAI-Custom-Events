// Screen Shake — verifies that two instances of the same event keep separate state.
// Each instance owns its own engine and its own closure, so `fired` is per-instance.

function create(ctx, params) {
  var fired = false;
  var instanceFloor = ctx.floorSeq;

  return {
    update: function (t) {
      if (t < 0 || fired) return;
      fired = true;

      var duration = params.duration * ctx.secondsPerBeat;
      fx.shake({ strength: params.strength, duration: duration });
    },

    dispose: function () {
      fired = false;
    }
  };
}
