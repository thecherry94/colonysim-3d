namespace ColonySim;

using Godot;

/// <summary>
/// Generates Tier 1 ore deposits in underground rock using 3D noise clusters.
/// Each ore type has its own noise field, depth range, and host rock restrictions.
///
/// Tier 1 Ores (Upper Stone band, early game):
///   Coal:   large clusters (50-150 blocks), sedimentary rock only, depth 5-30
///   Iron:   medium clusters (30-80 blocks), sedimentary rock only, depth 10-35
///   Copper: medium clusters (20-60 blocks), any upper stone rock, depth 5-25
///   Tin:    small clusters (10-30 blocks), sedimentary + metamorphic, depth 10-30
///
/// Ore placement runs AFTER geology fill but BEFORE cave carving, so ores
/// naturally appear in cave walls when caves carve through ore deposits.
///
/// Host Rock Restriction: ores only replace specific rock types. This creates
/// natural regional variation — areas with Granite underground won't have iron
/// (which requires sedimentary), but may have copper.
///
/// Thread-safe: all noise instances are read-only after construction.
/// </summary>
public class OreGenerator
{
    // One 3D noise field per ore type for cluster shape generation
    private readonly FastNoiseLite _coalNoise;
    private readonly FastNoiseLite _ironNoise;
    private readonly FastNoiseLite _copperNoise;
    private readonly FastNoiseLite _tinNoise;

    public OreGenerator(int seed)
    {
        // Coal: slightly lower frequency for larger clusters
        _coalNoise = new FastNoiseLite();
        _coalNoise.Seed = seed + 2000;
        _coalNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _coalNoise.Frequency = 0.08f;
        _coalNoise.FractalType = FastNoiseLite.FractalTypeEnum.None;

        // Iron: same frequency as coal, different seed
        _ironNoise = new FastNoiseLite();
        _ironNoise.Seed = seed + 2100;
        _ironNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _ironNoise.Frequency = 0.10f;
        _ironNoise.FractalType = FastNoiseLite.FractalTypeEnum.None;

        // Copper: slightly different frequency for varied cluster sizes
        _copperNoise = new FastNoiseLite();
        _copperNoise.Seed = seed + 2200;
        _copperNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _copperNoise.Frequency = 0.09f;
        _copperNoise.FractalType = FastNoiseLite.FractalTypeEnum.None;

        // Tin: slightly higher frequency for smaller clusters
        _tinNoise = new FastNoiseLite();
        _tinNoise.Seed = seed + 2300;
        _tinNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _tinNoise.Frequency = 0.11f;
        _tinNoise.FractalType = FastNoiseLite.FractalTypeEnum.None;
    }

    /// <summary>
    /// Attempt to replace a rock block with ore based on position, depth, and host rock.
    /// Returns the ore BlockType if placement succeeds, or the original rock type if not.
    /// Called per-block for underground blocks after geology fill.
    /// </summary>
    /// <param name="worldX">World X coordinate</param>
    /// <param name="worldY">World Y coordinate</param>
    /// <param name="worldZ">World Z coordinate</param>
    /// <param name="depthBelowSurface">Depth below the surface at this column</param>
    /// <param name="currentRock">The geological rock type already placed at this position</param>
    /// <returns>Ore type if placed, or currentRock if no ore</returns>
    public BlockType TryPlaceOre(int worldX, int worldY, int worldZ,
                                  int depthBelowSurface, BlockType currentRock)
    {
        // Check each ore in priority order (rarer ores checked first to avoid
        // being overwritten by common ones at overlapping depths)

        // Tin: depth 10-30, sedimentary + metamorphic host rocks, tight threshold
        if (depthBelowSurface >= 10 && depthBelowSurface <= 30
            && IsTinHost(currentRock))
        {
            float noise = _tinNoise.GetNoise3D(worldX, worldY, worldZ);
            if (noise > 0.72f) return BlockType.TinOre;
        }

        // Copper: depth 5-25, any upper stone rock, medium threshold
        if (depthBelowSurface >= 5 && depthBelowSurface <= 25
            && IsUpperStoneRock(currentRock))
        {
            float noise = _copperNoise.GetNoise3D(worldX, worldY, worldZ);
            if (noise > 0.68f) return BlockType.CopperOre;
        }

        // Iron: depth 10-35, sedimentary only, medium threshold
        if (depthBelowSurface >= 10 && depthBelowSurface <= 35
            && IsSedimentary(currentRock))
        {
            float noise = _ironNoise.GetNoise3D(worldX, worldY, worldZ);
            if (noise > 0.65f) return BlockType.IronOre;
        }

        // Coal: depth 5-30, sedimentary only, most common (lowest threshold)
        if (depthBelowSurface >= 5 && depthBelowSurface <= 30
            && IsSedimentary(currentRock))
        {
            float noise = _coalNoise.GetNoise3D(worldX, worldY, worldZ);
            if (noise > 0.60f) return BlockType.CoalOre;
        }

        return currentRock;
    }

    /// <summary>Returns true if the rock type is sedimentary (hosts Coal, Iron).</summary>
    private static bool IsSedimentary(BlockType rock) =>
        rock == BlockType.Limestone || rock == BlockType.Sandstone || rock == BlockType.Mudstone;

    /// <summary>Returns true if the rock can host Tin (sedimentary + metamorphic).</summary>
    private static bool IsTinHost(BlockType rock) =>
        IsSedimentary(rock) || rock == BlockType.Marble || rock == BlockType.Slate || rock == BlockType.Quartzite;

    /// <summary>Returns true if the rock is any Upper Stone type (hosts Copper).</summary>
    private static bool IsUpperStoneRock(BlockType rock) =>
        IsSedimentary(rock) || rock == BlockType.Marble || rock == BlockType.Slate
        || rock == BlockType.Quartzite || rock == BlockType.Granite
        || rock == BlockType.Basalt || rock == BlockType.Andesite;
}
