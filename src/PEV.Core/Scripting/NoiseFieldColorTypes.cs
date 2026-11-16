namespace PEV.Scripting
{
    // Transitional data shape for the existing field adapter. The computation itself
    // belongs to the event's vector program; the adapter will be generalized separately.
    internal static partial class NoiseFieldColorOwnership
    {
        internal struct Zone
        {
            internal float CenterX { get; set; }
            internal float CenterY { get; set; }
            internal float HalfX { get; set; }
            internal float HalfY { get; set; }
            internal float Angle { get; set; }
            internal float YScale { get; set; }
            internal float Opacity { get; set; }
            internal float Red { get; set; }
            internal float Green { get; set; }
            internal float Blue { get; set; }
            internal bool Active { get; set; }
            internal bool Inverted { get; set; }
        }

        internal sealed class Result { internal byte[] Active, Disabled; }
    }
}
