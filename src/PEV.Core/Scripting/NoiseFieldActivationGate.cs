namespace PEV.Scripting
{
    /// <summary>Waits one frame after a renderer group becomes active for Unity culling registration.</summary>
    internal struct NoiseFieldActivationGate
    {
        private bool _active;
        private int _activatedFrame;

        internal bool Observe(bool active, int frame)
        {
            if (!active) { _active = false; return true; }
            if (!_active) { _active = true; _activatedFrame = frame; return false; }
            return frame != _activatedFrame;
        }
    }
}
