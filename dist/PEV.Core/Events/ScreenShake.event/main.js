// Screen Shake — verifies that two instances of the same event keep separate state.
// Each instance owns its own engine and its own closure, so `fired` is per-instance.

function create(ctx, params) {
  var fired = false;
  var instanceFloor = ctx.floorSeq;

  return {
    update: function (t) {
      if (t < 0 || fired) return;
      fired = true;

      var duration = params.duration * ctx_bpm_seconds(120);
      fx.shake({ strength: params.strength, duration: duration });
    },

    dispose: function () {
      fired = false;
    }
  };
}

// Beats -> seconds. The host does not expose bpm directly in P1, so use a fixed
// reference of 120 BPM; duration is a rough visual hint, not a sync-critical value.
function ctx_bpm_seconds(bpm) { return 60.0 / bpm; }
