namespace ColonySim;

using System;
using Godot;

/// <summary>
/// Multi-layer noise terrain generator with biome support.
/// 6 noise layers create diverse biome-aware terrain with seamless transitions.
///
/// Layer 1 — Continentalness (freq 0.003): large-scale terrain category
/// Layer 2 — Elevation (freq 0.01): primary height variation
/// Layer 3 — Detail (freq 0.06): surface roughness
/// Layer 4 — River (freq 0.005): rivers form where abs(noise) ≈ 0
/// Layer 5 — Temperature (freq 0.002): climate zones (cold ↔ hot)
/// Layer 6 — Moisture (freq 0.0025): wet/dry variation
/// </summary>
public class TerrainGenerator
{
    private readonly FastNoiseLite _continentalNoise;
    private readonly FastNoiseLite _elevationNoise;
    private readonly FastNoiseLite _detailNoise;
    private readonly FastNoiseLite _riverNoise;
    private readonly FastNoiseLite _temperatureNoise;
    private readonly FastNoiseLite _moistureNoise;

    public const int WaterLevel = 25;
    public const int MaxHeight = 62;
    private const float RiverWidth = 0.04f;
    private const float RiverBankWidth = 0.08f;

    // Biome blending: Gaussian weight sharpness. Higher = sharper biome borders.
    private const float BlendSharpness = 4.0f;

    // Biome center positions in (temperature, moisture, continentalness) noise space [0,1].
    private static readonly (float t, float m, float c)[] BiomeCenters =
    {
        (0.50f, 0.50f, 0.35f),  // Grassland: mid everything
        (0.50f, 0.80f, 0.35f),  // Forest: mid temp, wet
        (0.80f, 0.15f, 0.35f),  // Desert: hot, dry
        (0.15f, 0.50f, 0.35f),  // Tundra: cold
        (0.80f, 0.80f, 0.35f),  // Swamp: hot, wet
        (0.50f, 0.50f, 0.85f),  // Mountains: high continentalness
    };

    /// <summary>
    /// All noise values sampled once per XZ column to avoid redundant noise calls.
    /// </summary>
    private readonly struct ColumnSample
    {
        public readonly float Continental, Elevation, Detail, River;
        public readonly float CNorm, TNorm, MNorm;

        public ColumnSample(float continental, float elevation, float detail, float river,
                            float cNorm, float tNorm, float mNorm)
        {
            Continental = continental;
            Elevation = elevation;
            Detail = detail;
            River = river;
            CNorm = cNorm;
            TNorm = tNorm;
            MNorm = mNorm;
        }
    }

    public TerrainGenerator(int seed = 42)
    {
        // Layer 1 — Continentalness: very low frequency for broad terrain categories
        _continentalNoise = new FastNoiseLite();
        _continentalNoise.Seed = seed;
        _continentalNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _continentalNoise.Frequency = 0.003f;
        _continentalNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _continentalNoise.FractalOctaves = 3;
        _continentalNoise.FractalLacunarity = 2.0f;
        _continentalNoise.FractalGain = 0.5f;

        // Layer 2 — Elevation: primary height variation
        _elevationNoise = new FastNoiseLite();
        _elevationNoise.Seed = seed + 100;
        _elevationNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _elevationNoise.Frequency = 0.01f;
        _elevationNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _elevationNoise.FractalOctaves = 4;
        _elevationNoise.FractalLacunarity = 2.0f;
        _elevationNoise.FractalGain = 0.5f;

        // Layer 3 — Detail: fine surface roughness
        _detailNoise = new FastNoiseLite();
        _detailNoise.Seed = seed + 200;
        _detailNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _detailNoise.Frequency = 0.06f;
        _detailNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _detailNoise.FractalOctaves = 2;

        // Layer 4 — River: single octave for clean channel boundaries
        _riverNoise = new FastNoiseLite();
        _riverNoise.Seed = seed + 300;
        _riverNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _riverNoise.Frequency = 0.005f;
        _riverNoise.FractalType = FastNoiseLite.FractalTypeEnum.None;

        // Layer 5 — Temperature: broad climate zones
        _temperatureNoise = new FastNoiseLite();
        _temperatureNoise.Seed = seed + 400;
        _temperatureNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _temperatureNoise.Frequency = 0.002f;
        _temperatureNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _temperatureNoise.FractalOctaves = 2;

        // Layer 6 — Moisture: wet/dry variation
        _moistureNoise = new FastNoiseLite();
        _moistureNoise.Seed = seed + 500;
        _moistureNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _moistureNoise.Frequency = 0.0025f;
        _moistureNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _moistureNoise.FractalOctaves = 2;

        GD.Print($"TerrainGenerator initialized: seed={seed}, waterLevel={WaterLevel}, maxHeight={MaxHeight}, biomes=6");
    }

    /// <summary>
    /// Sample all 6 noise layers at a world XZ position, normalize to [0,1].
    /// </summary>
    private ColumnSample SampleColumn(int worldX, int worldZ)
    {
        float continental = _continentalNoise.GetNoise2D(worldX, worldZ);
        float elevation = _elevationNoise.GetNoise2D(worldX, worldZ);
        float detail = _detailNoise.GetNoise2D(worldX, worldZ);
        float river = _riverNoise.GetNoise2D(worldX, worldZ);
        float temp = _temperatureNoise.GetNoise2D(worldX, worldZ);
        float moisture = _moistureNoise.GetNoise2D(worldX, worldZ);

        return new ColumnSample(
            continental, elevation, detail, river,
            (continental + 1f) * 0.5f,
            (temp + 1f) * 0.5f,
            (moisture + 1f) * 0.5f
        );
    }

    /// <summary>
    /// Classify which biome dominates at the given normalized noise coordinates.
    /// Used for block type selection (hard threshold — not blended).
    /// </summary>
    private static BiomeType ClassifyBiome(float t, float m, float c)
    {
        if (c > 0.7f) return BiomeType.Mountains;
        if (t < 0.35f) return BiomeType.Tundra;
        if (t > 0.65f)
        {
            if (m < 0.35f) return BiomeType.Desert;
            if (m > 0.65f) return BiomeType.Swamp;
        }
        if (m > 0.65f) return BiomeType.Forest;
        return BiomeType.Grassland;
    }

    /// <summary>
    /// Public biome query for external use (World.GetBiome pass-through).
    /// </summary>
    public BiomeType GetBiome(int worldX, int worldZ)
    {
        float c = (_continentalNoise.GetNoise2D(worldX, worldZ) + 1f) * 0.5f;
        float t = (_temperatureNoise.GetNoise2D(worldX, worldZ) + 1f) * 0.5f;
        float m = (_moistureNoise.GetNoise2D(worldX, worldZ) + 1f) * 0.5f;
        return ClassifyBiome(t, m, c);
    }

    /// <summary>
    /// Compute blended height parameters using Gaussian weights in noise space.
    /// Each biome contributes based on proximity to its center in (t, m, c) space.
    /// This prevents cliff walls at biome boundaries.
    /// </summary>
    private static void ComputeBlendedParams(float t, float m, float c,
        out float heightOffset, out float ampScale, out float detScale)
    {
        Span<float> weights = stackalloc float[6];
        float totalWeight = 0f;

        for (int i = 0; i < 6; i++)
        {
            float dt = t - BiomeCenters[i].t;
            float dm = m - BiomeCenters[i].m;
            float dc = (c - BiomeCenters[i].c) * 2f; // Continental axis weighted more
            float distSq = dt * dt + dm * dm + dc * dc;
            weights[i] = Mathf.Exp(-BlendSharpness * distSq);
            totalWeight += weights[i];
        }

        heightOffset = 0f;
        ampScale = 0f;
        detScale = 0f;

        for (int i = 0; i < 6; i++)
        {
            float w = weights[i] / totalWeight;
            heightOffset += BiomeTable.Biomes[i].BaseHeightOffset * w;
            ampScale += BiomeTable.Biomes[i].AmplitudeScale * w;
            detScale += BiomeTable.Biomes[i].DetailScale * w;
        }
    }

    /// <summary>
    /// Compute surface height from a pre-sampled column.
    /// Uses blended biome parameters for seamless transitions.
    /// </summary>
    private int ComputeHeight(in ColumnSample s)
    {
        float cSquared = s.CNorm * s.CNorm;

        // Get blended biome height modifiers
        ComputeBlendedParams(s.TNorm, s.MNorm, s.CNorm,
            out float heightOffset, out float ampScale, out float detScale);

        // Base height from continentalness + biome offset
        float baseHeight = Mathf.Lerp(22.0f, 40.0f, cSquared) + heightOffset;
        float amplitude = Mathf.Lerp(4.0f, 18.0f, cSquared) * ampScale;
        float detailAmp = Mathf.Lerp(0.5f, 3.0f, s.CNorm) * detScale;

        float rawHeight = baseHeight + s.Elevation * amplitude + s.Detail * detailAmp;

        // River carving: only where terrain is above water and not mountainous
        if (s.Continental <= 0.5f && rawHeight > WaterLevel + 1)
        {
            float absRiver = Mathf.Abs(s.River);
            if (absRiver < RiverWidth)
            {
                rawHeight = WaterLevel - 1;
            }
            else if (absRiver < RiverBankWidth)
            {
                float t = (absRiver - RiverWidth) / (RiverBankWidth - RiverWidth);
                t = t * t;
                rawHeight = Mathf.Lerp(WaterLevel, rawHeight, t);
            }
        }

        return Mathf.Clamp(Mathf.RoundToInt(rawHeight), 2, MaxHeight);
    }

    /// <summary>
    /// Returns the surface height at the given world X/Z coordinate.
    /// Public API used by World.GetSurfaceHeight() for camera/colonist positioning.
    /// </summary>
    public int GetHeight(int worldX, int worldZ)
    {
        var sample = SampleColumn(worldX, worldZ);
        return ComputeHeight(sample);
    }

    /// <summary>
    /// Returns true if this position is in a river channel.
    /// </summary>
    private bool IsRiverChannel(in ColumnSample s, int surfaceHeight)
    {
        if (s.Continental > 0.5f) return false;
        if (Mathf.Abs(s.River) >= RiverWidth) return false;

        // Check if terrain would naturally be above water (before river carving).
        // Recompute height without river carving.
        float cSquared = s.CNorm * s.CNorm;
        ComputeBlendedParams(s.TNorm, s.MNorm, s.CNorm,
            out float heightOffset, out float ampScale, out float detScale);
        float baseH = Mathf.Lerp(22.0f, 40.0f, cSquared) + heightOffset;
        float amp = Mathf.Lerp(4.0f, 18.0f, cSquared) * ampScale;
        float detAmp = Mathf.Lerp(0.5f, 3.0f, s.CNorm) * detScale;
        float naturalHeight = baseH + s.Elevation * amp + s.Detail * detAmp;

        return naturalHeight > WaterLevel + 1;
    }

    /// <summary>
    /// Fill a chunk's block array based on biome-aware terrain.
    /// Samples all noise once per XZ column for performance.
    /// </summary>
    public void GenerateChunkBlocks(BlockType[,,] blocks, Vector3I chunkCoord)
    {
        int chunkWorldYBase = chunkCoord.Y * Chunk.SIZE;

        for (int lx = 0; lx < Chunk.SIZE; lx++)
        for (int lz = 0; lz < Chunk.SIZE; lz++)
        {
            int worldX = chunkCoord.X * Chunk.SIZE + lx;
            int worldZ = chunkCoord.Z * Chunk.SIZE + lz;

            var sample = SampleColumn(worldX, worldZ);
            int surfaceHeight = ComputeHeight(sample);
            BiomeType biome = ClassifyBiome(sample.TNorm, sample.MNorm, sample.CNorm);
            bool inRiver = IsRiverChannel(sample, surfaceHeight);

            for (int ly = 0; ly < Chunk.SIZE; ly++)
            {
                int worldY = chunkWorldYBase + ly;

                if (worldY > surfaceHeight && worldY > WaterLevel)
                {
                    continue; // Air
                }
                else if (worldY > surfaceHeight && worldY <= WaterLevel)
                {
                    // Above terrain but at/below water level
                    // Tundra: freeze the water surface
                    if (biome == BiomeType.Tundra && worldY == WaterLevel)
                        blocks[lx, ly, lz] = BlockType.Ice;
                    else
                        blocks[lx, ly, lz] = BlockType.Water;
                }
                else if (worldY == surfaceHeight)
                {
                    blocks[lx, ly, lz] = GetSurfaceBlock(surfaceHeight, inRiver, biome);
                }
                else if (worldY >= surfaceHeight - 3)
                {
                    blocks[lx, ly, lz] = GetSubSurfaceBlock(worldY, surfaceHeight, inRiver, biome);
                }
                else
                {
                    blocks[lx, ly, lz] = BlockType.Stone;
                }
            }
        }
    }

    /// <summary>
    /// Determine surface block type based on height, river, and biome.
    /// </summary>
    private BlockType GetSurfaceBlock(int surfaceHeight, bool inRiver, BiomeType biome)
    {
        var data = BiomeTable.Biomes[(int)biome];

        if (surfaceHeight < WaterLevel)
        {
            // Underwater surface
            return inRiver ? BlockType.Gravel : data.UnderwaterSurface;
        }
        if (surfaceHeight <= WaterLevel + 2)
        {
            // Beach / water edge — always Sand
            return BlockType.Sand;
        }
        // Mountain snow caps
        if (biome == BiomeType.Mountains && surfaceHeight >= 48)
            return BlockType.Snow;

        return data.SurfaceBlock;
    }

    /// <summary>
    /// Determine sub-surface block type (1-3 blocks below surface).
    /// </summary>
    private BlockType GetSubSurfaceBlock(int worldY, int surfaceHeight, bool inRiver, BiomeType biome)
    {
        var data = BiomeTable.Biomes[(int)biome];

        if (worldY < WaterLevel && inRiver)
            return BlockType.Sand;
        if (surfaceHeight <= WaterLevel + 2)
            return BlockType.Sand; // Beach subsurface
        return data.SubSurfaceBlock;
    }
}
