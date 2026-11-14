namespace PEV.Scripting
{
    // Enum member ORDER is the dropdown order and must not change after release:
    // the level file stores member names (StorageValue.Canonical), so renaming a member
    // orphans every chart that used it. Appending is safe.
    public enum NoiseFieldRelativeTo
    {
        Tile,        // anchored to the tile the event sits on
        Camera,      // anchored to the gameplay camera (screen-relative, world units)
        PlanetRed,
        PlanetBlue,
        PlanetGreen,
        CameraRatio  // screen-relative where size 1.0 spans the whole viewport
    }

    public enum NoiseFieldAnchor
    {
        Center,
        TopLeft, Top, TopRight,
        Left, Right,
        BottomLeft, Bottom, BottomRight
    }

    public enum NoiseFieldShape { Rect, Circle }
}
