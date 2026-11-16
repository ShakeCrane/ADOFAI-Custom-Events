// Event policy lives here. Core only combines tag-scoped phase overrides.
function create(ctx, params) {
  var tag = String(params.tag || "");
  var action = String(params.action || "Active");
  var warning = Number(params.warningDuration);
  if (!isFinite(warning) || warning < 0) warning = 0.5;
  return {
    update: function (t) {
      if (!tag) return;
      var phase = action === "WarnThenActive" ? (t < warning ? "Ready" : "Active") : action;
      fx.fieldState({ tag: tag, phase: phase });
    }
  };
}
