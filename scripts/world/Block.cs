namespace ColonySim;

using Godot;

/// <summary>
/// Block type identifier. Backed by byte (256 max types).
/// Air = 0 so default-initialized arrays are all air.
/// </summary>
public enum BlockType : byte
{
    Air = 0,
    Stone = 1,
    Dirt = 2,
    Grass = 3,
}

/// <summary>
/// Static utilities for block type properties.
/// </summary>
public static class BlockData
{
    public static bool IsSolid(BlockType type) => type != BlockType.Air;

    public static Color GetColor(BlockType type) => type switch
    {
        BlockType.Stone => new Color(0.5f, 0.5f, 0.5f),
        BlockType.Dirt => new Color(0.55f, 0.35f, 0.2f),
        BlockType.Grass => new Color(0.3f, 0.65f, 0.2f),
        _ => Colors.Transparent,
    };
}
