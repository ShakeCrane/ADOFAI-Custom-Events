// Video — driver for the host's video surface.
//
// The surface is a decoration: where it sits, how big it is, how it is rotated, its colour,
// opacity, depth and tag all live on the decoration row, which the editor gizmo and the
// engine's own MoveDecorations tweens write. Sending them from here would make the script
// fight the engine, so this file only decides *when* in the media the picture should be.
//
// Timing is expressed as an absolute media position, never as a delta. The host reconciles
// toward it, so pause, restart, checkpoint jumps and editor scrubbing all stay correct
// without the script tracking elapsed time.

function num(value, fallback) {
  var n = Number(value);
  return isFinite(n) ? n : fallback;
}

function create(ctx, params) {
  var key = "video";

  var file = params.file || "";
  var speed = num(params.speed, 1);
  if (speed <= 0.0001) speed = 1;
  var trimStart = Math.max(0, num(params.trimStart, 0));
  var trimEnd = Math.max(0, num(params.trimEnd, 0));
  var loop = params.loop === true;
  var holdEnd = params.holdEnd !== false;
  var mode = params.mode || "Fit";
  var volume = num(params.volume, 100) / 100;
  var muted = params.muted !== false;

  // The tile tempo is fixed by the chart, including when playback begins at a checkpoint.
  var begin = num(ctx.tileTime, 0) +
    num(params.start, 0) * num(ctx.tileSecondsPerBeat, num(ctx.secondsPerBeat, 0.5));

  var reportedDuration = 0;

  function frame() {
    var now = num(ctx.time, 0);

    // Before the clip's start the surface shows the first frame of the window and holds it:
    // an author who scrubs to the beginning of the level still sees the video they placed.
    if (now < begin) {
      return { time: trimStart, playing: false, visible: true };
    }

    var media = trimStart + (now - begin) * speed;

    // Stop extending past the window when it is not looping. The surface knows the media
    // duration, so it clamps; this only avoids asking it to run forever.
    if (reportedDuration > 0) {
      var end = trimEnd > trimStart ? Math.min(trimEnd, reportedDuration) : reportedDuration;
      if (!loop && media >= end) {
        // Past the end the two choices are genuinely different pictures: hold the last
        // frame, or take the video off the screen and let what is behind it show through.
        return { time: end, playing: false, visible: holdEnd };
      }
    }

    return { time: media, playing: true, visible: true };
  }

  return {
    update: function () {
      var state = frame();
      var result = fx.video(key, {
        file: file,
        time: state.time,
        playing: state.playing,
        loop: loop,
        speed: speed,
        trimStart: trimStart,
        trimEnd: trimEnd,
        visible: state.visible,
        volume: volume,
        muted: muted,
        fit: mode
      });
      if (result && typeof result.duration === "number" && result.duration > 0) {
        reportedDuration = result.duration;
      }
    },

    dispose: function () {
      fx.videoDestroy(key);
    }
  };
}
