namespace PEV.Scripting
{
    // Transitional data shape for the existing field adapter. The computation itself
    // belongs to the event's vector program; the adapter will be generalized separately.
    internal static partial class NoiseFieldColorOwnership
    {
        internal struct Zone
        {
            public float CenterX, CenterY, HalfX, HalfY, Angle, YScale, Opacity, Red, Green, Blue;
            public bool Active, Inverted;
        }

#if PEV_DIAGNOSTICS
        internal sealed class Result { internal byte[] Active, Disabled; }
#endif
    }
}
