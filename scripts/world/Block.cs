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
    Sand = 4,
    Water = 5,
    Gravel = 6,
    Snow = 7,
    Ice = 8,
    RedSand = 9,
    Clay = 10,
}

/// <summary>
/// Static utilities for block type properties.
/// </summary>
public static class BlockData
{
    public static bool IsSolid(BlockType type) => type != BlockType.Air && type != BlockType.Water;
    public static bool IsLiquid(BlockType type) => type == BlockType.Water;

    public static Color GetColor(BlockType type) => type switch
    {
        BlockType.Stone => new Color(0.55f, 0.55f, 0.58f),
        BlockType.Dirt => new Color(0.60f, 0.40f, 0.22f),
        BlockType.Grass => new Color(0.35f, 0.70f, 0.25f),
        BlockType.Sand => new Color(0.85f, 0.78f, 0.52f),
        BlockType.Water => new Color(0.2f, 0.4f, 0.75f, 0.7f),
        BlockType.Gravel => new Color(0.52f, 0.48f, 0.42f),
        BlockType.Snow => new Color(0.92f, 0.93f, 0.96f),
        BlockType.Ice => new Color(0.70f, 0.85f, 0.95f),
        BlockType.RedSand => new Color(0.82f, 0.52f, 0.28f),
        BlockType.Clay => new Color(0.65f, 0.50f, 0.38f),
        _ => Colors.Transparent,
    };

    /// <summary>
    /// Darkened color for side faces to give blocks visual depth.
    /// </summary>
    public static Color GetSideColor(BlockType type)
    {
        var c = GetColor(type);
        return new Color(c.R * 0.75f, c.G * 0.75f, c.B * 0.75f);
    }

    /// <summary>
    /// Darkened color for bottom faces.
    /// </summary>
    public static Color GetBottomColor(BlockType type)
    {
        var c = GetColor(type);
        return new Color(c.R * 0.55f, c.G * 0.55f, c.B * 0.55f);
    }
}
