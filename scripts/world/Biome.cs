namespace ColonySim;

/// <summary>
/// Biome classification. Determined by temperature, moisture, and continentalness noise.
/// </summary>
public enum BiomeType : byte
{
    Grassland  = 0,
    Forest     = 1,
    Desert     = 2,
    Tundra     = 3,
    Swamp      = 4,
    Mountains  = 5,
}

/// <summary>
/// Per-biome terrain parameters: surface/subsurface blocks and height modifiers.
/// </summary>
public readonly struct BiomeData
{
    public readonly BlockType SurfaceBlock;
    public readonly BlockType SubSurfaceBlock;
    public readonly BlockType UnderwaterSurface;
    public readonly float BaseHeightOffset;
    public readonly float AmplitudeScale;
    public readonly float DetailScale;
    public readonly float TreeDensity;

    public BiomeData(BlockType surface, BlockType subSurface, BlockType underwater,
                     float heightOffset, float ampScale, float detailScale,
                     float treeDensity = 0f)
    {
        SurfaceBlock = surface;
        SubSurfaceBlock = subSurface;
        UnderwaterSurface = underwater;
        BaseHeightOffset = heightOffset;
        AmplitudeScale = ampScale;
        DetailScale = detailScale;
        TreeDensity = treeDensity;
    }
}

/// <summary>
/// Static biome definitions indexed by BiomeType.
/// </summary>
public static class BiomeTable
{
    public static readonly BiomeData[] Biomes = new BiomeData[]
    {
        //                  Surface          SubSurface       Underwater       HeightOff  Amp   Detail  Trees
        new BiomeData(BlockType.Grass,   BlockType.Dirt,  BlockType.Sand,     0f,  0.3f, 0.2f, 0.05f),  // Grassland (scattered trees)
        new BiomeData(BlockType.Grass,   BlockType.Dirt,  BlockType.Sand,     2f,  0.8f, 1.0f, 0.30f),  // Forest (dense trees)
        new BiomeData(BlockType.RedSand, BlockType.Sand,  BlockType.Sand,    -2f,  0.4f, 0.2f, 0.00f),  // Desert (no trees)
        new BiomeData(BlockType.Snow,    BlockType.Dirt,  BlockType.Gravel,  -1f,  0.5f, 0.3f, 0.01f),  // Tundra (very rare trees)
        new BiomeData(BlockType.Grass,   BlockType.Clay,  BlockType.Clay,    -4f,  0.2f, 0.1f, 0.15f),  // Swamp (moderate trees)
        new BiomeData(BlockType.Stone,   BlockType.Stone, BlockType.Gravel,   8f,  1.8f, 1.5f, 0.02f),  // Mountains (rare trees)
    };
}
