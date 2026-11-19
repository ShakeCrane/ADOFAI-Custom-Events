using System;
using System.Collections.Generic;
using System.Globalization;
using PEV.Model;
using PEV.Scripting;
using UnityEngine;

namespace PEV.Diagnostics
{
    /// <summary>
    /// Timeline, composition and lifecycle checks for the colour grading package.
    ///
    /// The pixel comparison answers "is the arithmetic right". These answer the questions a
    /// chart author actually runs into: does the fade reach full strength at the right time,
    /// does a second grading event replace or stack, and does everything the effect allocated
    /// go away when the level is torn down.
    /// </summary>
    internal static class GradingTimelineTests
    {
        private const string EventId = "pev.color-grading";

        internal static Dictionary<string, object> BaseParams() =>
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "enabled", true }, { "strength", 1.0 }, { "mode", "Replace" }, { "duration", 0.0 },
                { "ease", "OutCubic" }, { "exposure", 0.0 }, { "brightness", 0.0 }, { "contrast", 0.0 },
                { "saturation", 0.0 }, { "vibrance", 0.0 }, { "temperature", 0.0 }, { "tint", 0.0 },
                { "highlights", 0.0 }, { "shadows", 0.0 }, { "whites", 0.0 }, { "blacks", 0.0 },
                { "gamma", 1.0 }, { "gainR", 1.0 }, { "gainG", 1.0 }, { "gainB", 1.0 },
                { "offsetR", 0.0 }, { "offsetG", 0.0 }, { "offsetB", 0.0 },
                { "liftXY", new[] { 0f, 0f } }, { "liftL", 0f },
                { "gammaXY", new[] { 0f, 0f } }, { "gammaL", 0f },
                { "gainXY", new[] { 0f, 0f } }, { "gainL", 0f },
                { "offsetXY", new[] { 0f, 0f } }, { "offsetL", 0f },
                { "curveMaster", "0,0;1,1" }, { "curveRed", "0,0;1,1" },
                { "curveGreen", "0,0;1,1" }, { "curveBlue", "0,0;1,1" },
                { "hueVsHue", "0,0.5;1,0.5" }, { "hueVsSat", "0,0.5;1,0.5" }, { "hueVsLuma", "0,0.5;1,0.5" },
                { "hslBands", "0,0,0;0,0,0;0,0,0;0,0,0;0,0,0;0,0,0;0,0,0;0,0,0" },
                { "showRaw", false }, { "lutPreset", "None" }, { "lutFile", "" }, { "lutStrength", 1.0 }
            };

        internal static bool TryCreate(string scope, out ScriptHost host, out EventContext context,
            out string error, Dictionary<string, object> parameters = null)
        {
            host = null;
            context = null;
            error = null;
            if (!Registry.EventRegistry.TryGetById(EventId, out var manifest))
            { error = "package missing"; return false; }
            string source = EventScriptCache.Get(manifest);
            if (source == null) { error = "entry script missing"; return false; }

            var values = parameters ?? BaseParams();
            host = ScriptHost.Create(source, scope, out string createError, manifest);
            if (host == null) { error = createError; return false; }
            context = new EventContext { Bpm = 120, EventTime = 0, FloorSeq = 1, Params = values };
            if (!host.Initialize(context, values, out string initError))
            {
                host.Dispose();
                host = null;
                error = initError;
                return false;
            }
            return true;
        }

        internal static void Tick(ScriptHost host, EventContext context, double t)
        {
            context.Time = t;
            context.T = t;
            context.Beat = t * context.Bpm / 60.0;
            host.Update(t, context.ToStateJson());
        }

        internal static string PayloadOf(string scope) => ScreenEffectPipeline.Find(scope)?.Payload;
    }
}
