using System.Collections.Generic;
using PEV.Model;

namespace PEV.Registry
{
    /// <summary>
    /// The built-in event that stands in for payloads we could not resolve.
    ///
    /// Without it a missing package is invisible: the comment survives on disk but the
    /// editor shows nothing on the track, so an author opening a chart written by someone
    /// else concludes their events were lost.
    ///
    /// The placeholder is display-only. Its fields exist to tell the author which package
    /// is missing; they are force-disabled by the panel patch and never round-trip. The
    /// original payload is written back verbatim (see LevelStorage.Bake).
    /// </summary>
    public static class PlaceholderEvent
    {
        public const string Id = "pev.missing";

        public static EventManifest Build()
        {
            var m = new EventManifest
            {
                SchemaVersion = 1,
                Id = Id,
                Version = "1.0.0",
                ApiVersion = 1,
                Entry = "main.js",
                Category = "Conveniences",
                DisplayName = "Missing Event"
            };

            m.Params = new List<ParamSpec>
            {
                new ParamSpec
                {
                    Name = "missingId",
                    Type = "String",
                    Default = "",
                    CustomLabel = "Missing event id"
                },
                new ParamSpec
                {
                    Name = "missingVersion",
                    Type = "String",
                    Default = "",
                    CustomLabel = "Written by version"
                },
                new ParamSpec
                {
                    Name = "missingUid",
                    Type = "String",
                    Default = "",
                    CustomLabel = "Instance uid"
                }
            };

            return m;
        }
    }
}
