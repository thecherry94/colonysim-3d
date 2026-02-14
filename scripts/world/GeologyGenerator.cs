namespace ColonySim;

using Godot;

/// <summary>
/// Generates geological rock layers based on depth below surface and horizontal
/// province noise. Replaces uniform Stone underground with varied rock types.
///
/// Depth Bands (measured from surface):
///   Soil:        0-3 blocks  — handled by existing biome subsurface logic
///   Upper Stone: 4-20 blocks — Sedimentary: Limestone, Sandstone, Mudstone
///   Mid Stone:   20-45 blocks — Igneous/Metamorphic: Granite, Basalt, Andesite, Marble, Slate
///   Deep Stone:  45+ blocks  — Deepstone + Quartzite pockets
///
/// Province Noise (2D, freq 0.002):
///   Selects which rock type dominates within each band. Different regions have
///   different geological columns, creating natural resource variation (ores are
///   hosted by specific rock types).
///
/// Band Boundary Noise (2D, freq 0.015):
///   Offsets depth band transitions by ±4 blocks to prevent flat artificial lines.
///
/// Rock Blob Noise (3D, freq 0.05):
///   Creates pockets of secondary rock within the dominant rock matrix (70/30 split).
///
/// Thread-safe: all noise instances are read-only after construction.
/// Called per-block from background threads during terrain generation.
/// </summary>
public class GeologyGenerator
{
    private readonly FastNoiseLite _provinceNoise;     // 2D: horizontal geological variation
    private readonly FastNoiseLite _boundaryNoise;     // 2D: undulating band boundaries
    private readonly FastNoiseLite _rockBlobNoise;     // 3D: secondary rock type pockets

    // Depth band boundaries (blocks below surface, before boundary noise offset)
    private const int SoilDepth = 3;          // 0-3: existing biome subsurface
    private const int UpperStoneDepth = 20;   // 4-20: sedimentary
    private const int MidStoneDepth = 45;     // 20-45: igneous/metamorphic
    // 45+: deep stone

    // Boundary noise amplitude (±blocks offset)
    private const float BoundaryAmplitude = 4.0f;

    // Province noise thresholds for rock type selection (divides [0,1] into 3 zones)
    private const float ProvinceThreshold1 = 0.33f;
    private const float ProvinceThreshold2 = 0.66f;

    // Rock blob threshold: above this = secondary rock (roughly 25-30% of volume)
    private const float BlobThreshold = 0.45f;

    public GeologyGenerator(int seed)
    {
        // Province noise: very low frequency for broad geological regions
        _provinceNoise = new FastNoiseLite();
        _provinceNoise.Seed = seed + 1300;
        _provinceNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _provinceNoise.Frequency = 0.002f;
        _provinceNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _provinceNoise.FractalOctaves = 2;

        // Band boundary offset: medium frequency for undulating transitions
        _boundaryNoise = new FastNoiseLite();
        _boundaryNoise.Seed = seed + 1500;
        _boundaryNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _boundaryNoise.Frequency = 0.015f;
        _boundaryNoise.FractalType = FastNoiseLite.FractalTypeEnum.None;

        // Rock blob noise: 3D noise for secondary rock type patches
        _rockBlobNoise = new FastNoiseLite();
        _rockBlobNoise.Seed = seed + 1400;
        _rockBlobNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _rockBlobNoise.Frequency = 0.05f;
        _rockBlobNoise.FractalType = FastNoiseLite.FractalTypeEnum.None;
    }

    /// <summary>
    /// Sample the geological province value at a world XZ position.
    /// Returns normalized [0,1] value. Should be cached per-column (called once per XZ).
    /// </summary>
    public float SampleProvince(int worldX, int worldZ)
    {
        return (_provinceNoise.GetNoise2D(worldX, worldZ) + 1f) * 0.5f;
    }

    /// <summary>
    /// Get the rock type for an underground block based on depth, province, and 3D noise.
    /// Called per-block for blocks deeper than the soil layer.
    /// </summary>
    /// <param name="worldX">World X coordinate</param>
    /// <param name="worldY">World Y coordinate</param>
    /// <param name="worldZ">World Z coordinate</param>
    /// <param name="surfaceY">Surface height at this XZ column</param>
    /// <param name="province">Province noise value [0,1] from SampleProvince()</param>
    /// <returns>The geological rock type for this position</returns>
    public BlockType GetRockType(int worldX, int worldY, int worldZ, int surfaceY, float province)
    {
        int depthBelowSurface = surfaceY - worldY;

        // Sample boundary offset (2D, per-column — but we accept the extra eval for simplicity)
        float boundaryOffset = _boundaryNoise.GetNoise2D(worldX, worldZ) * BoundaryAmplitude;

        // Adjusted band boundaries
        float upperBoundary = SoilDepth + boundaryOffset;
        float midBoundary = UpperStoneDepth + boundaryOffset;
        float deepBoundary = MidStoneDepth + boundaryOffset;

        // Determine which depth band this block falls in
        if (depthBelowSurface < upperBoundary)
        {
            // Still in soil range — should not be called here (handled by biome subsurface)
            // Fallback to Stone for safety
            return BlockType.Stone;
        }

        // Sample 3D rock blob noise for secondary rock selection
        float blob = _rockBlobNoise.GetNoise3D(worldX, worldY, worldZ);
        bool useSecondary = blob > BlobThreshold;

        if (depthBelowSurface < midBoundary)
        {
            // Upper Stone Band: Sedimentary rocks
            return GetUpperStoneRock(province, useSecondary);
        }

        if (depthBelowSurface < deepBoundary)
        {
            // Mid Stone Band: Igneous and Metamorphic rocks
            return GetMidStoneRock(province, useSecondary);
        }

        // Deep Stone Band: Deepstone with Quartzite pockets
        return useSecondary ? BlockType.Quartzite : BlockType.Deepstone;
    }

    /// <summary>
    /// Select rock type for the Upper Stone band (sedimentary) based on province.
    /// Province [0, 0.33): Limestone dominant, Sandstone secondary
    /// Province [0.33, 0.66): Sandstone dominant, Mudstone secondary
    /// Province [0.66, 1.0]: Mudstone dominant, Limestone secondary
    /// </summary>
    private static BlockType GetUpperStoneRock(float province, bool useSecondary)
    {
        if (province < ProvinceThreshold1)
            return useSecondary ? BlockType.Sandstone : BlockType.Limestone;
        if (province < ProvinceThreshold2)
            return useSecondary ? BlockType.Mudstone : BlockType.Sandstone;
        return useSecondary ? BlockType.Limestone : BlockType.Mudstone;
    }

    /// <summary>
    /// Select rock type for the Mid Stone band (igneous/metamorphic) based on province.
    /// Province [0, 0.33): Granite dominant, Marble secondary
    /// Province [0.33, 0.66): Basalt dominant, Slate secondary
    /// Province [0.66, 1.0]: Andesite dominant, Quartzite secondary
    /// </summary>
    private static BlockType GetMidStoneRock(float province, bool useSecondary)
    {
        if (province < ProvinceThreshold1)
            return useSecondary ? BlockType.Marble : BlockType.Granite;
        if (province < ProvinceThreshold2)
            return useSecondary ? BlockType.Slate : BlockType.Basalt;
        return useSecondary ? BlockType.Quartzite : BlockType.Andesite;
    }
}
