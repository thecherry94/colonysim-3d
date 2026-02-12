namespace ColonySim;

using System;
using Godot;

/// <summary>
/// Multi-layer noise terrain generator.
/// 4 noise layers create diverse terrain: flat valleys, rolling hills, mountains, and rivers.
///
/// Layer 1 — Continentalness (freq 0.003): large-scale terrain category (lowland/midland/highland)
/// Layer 2 — Elevation (freq 0.01): primary height variation, amplitude scaled by continentalness
/// Layer 3 — Detail (freq 0.06): surface roughness, suppressed in flat areas
/// Layer 4 — River (freq 0.005): rivers form where abs(noise) ≈ 0
/// </summary>
public class TerrainGenerator
{
    private readonly FastNoiseLite _continentalNoise;
    private readonly FastNoiseLite _elevationNoise;
    private readonly FastNoiseLite _detailNoise;
    private readonly FastNoiseLite _riverNoise;

    private const int WaterLevel = 3;
    private const float RiverWidth = 0.04f;
    private const float RiverBankWidth = 0.08f;

    public TerrainGenerator(int seed = 42)
    {
        // Layer 1 — Continentalness: very low frequency for broad terrain categories
        // One full transition per ~333 blocks
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

        GD.Print($"TerrainGenerator initialized: seed={seed}, waterLevel={WaterLevel}");
        GD.Print($"  Continentalness freq=0.003, Elevation freq=0.01, Detail freq=0.06, River freq=0.005");
    }

    /// <summary>
    /// Returns the surface height at the given world X/Z coordinate.
    /// Height is computed from continentalness-scaled elevation + detail noise.
    /// Rivers carve into the terrain where river noise ≈ 0.
    /// </summary>
    public int GetHeight(int worldX, int worldZ)
    {
        // Sample all noise layers
        float continental = _continentalNoise.GetNoise2D(worldX, worldZ);
        float elevation = _elevationNoise.GetNoise2D(worldX, worldZ);
        float detail = _detailNoise.GetNoise2D(worldX, worldZ);
        float river = _riverNoise.GetNoise2D(worldX, worldZ);

        // Remap continentalness from [-1,1] to [0,1]
        float cNorm = (continental + 1.0f) * 0.5f;
        float cSquared = cNorm * cNorm; // Squaring makes lowlands genuinely flat

        // Compute height with continentalness-scaled amplitude
        float baseHeight = Mathf.Lerp(3.0f, 9.0f, cSquared);
        float amplitude = Mathf.Lerp(1.0f, 4.5f, cSquared);
        float detailAmp = Mathf.Lerp(0.3f, 1.2f, cNorm);

        float rawHeight = baseHeight + elevation * amplitude + detail * detailAmp;

        // River carving: only in non-mountainous areas (continentalness <= 0.5)
        if (continental <= 0.5f)
        {
            float absRiver = Mathf.Abs(river);
            if (absRiver < RiverWidth)
            {
                // River center: carve down to water level - 1
                rawHeight = WaterLevel - 1;
            }
            else if (absRiver < RiverBankWidth)
            {
                // River banks: smooth transition from terrain to water edge
                float t = (absRiver - RiverWidth) / (RiverBankWidth - RiverWidth);
                t = t * t; // Ease-in for smoother bank slopes
                rawHeight = Mathf.Lerp(WaterLevel, rawHeight, t);
            }
        }

        int height = Mathf.RoundToInt(rawHeight);
        return Mathf.Clamp(height, 1, Chunk.SIZE - 2);
    }

    /// <summary>
    /// Returns the continentalness value at a given world X/Z coordinate.
    /// Used for block type decisions (mountain peaks get stone surface, etc.)
    /// </summary>
    private float GetContinentalness(int worldX, int worldZ)
    {
        return _continentalNoise.GetNoise2D(worldX, worldZ);
    }

    /// <summary>
    /// Returns true if this position is in a river channel.
    /// </summary>
    private bool IsRiverChannel(int worldX, int worldZ)
    {
        float continental = _continentalNoise.GetNoise2D(worldX, worldZ);
        if (continental > 0.5f) return false; // No rivers on mountains

        float river = _riverNoise.GetNoise2D(worldX, worldZ);
        return Mathf.Abs(river) < RiverWidth;
    }

    /// <summary>
    /// Fill a chunk's block array based on multi-layer noise terrain.
    /// Block assignment varies by continentalness, height, and river proximity.
    /// </summary>
    public void GenerateChunkBlocks(BlockType[,,] blocks, Vector3I chunkCoord)
    {
        if (chunkCoord.Y != 0) return;

        for (int lx = 0; lx < Chunk.SIZE; lx++)
        for (int lz = 0; lz < Chunk.SIZE; lz++)
        {
            int worldX = chunkCoord.X * Chunk.SIZE + lx;
            int worldZ = chunkCoord.Z * Chunk.SIZE + lz;
            int height = GetHeight(worldX, worldZ);
            float continental = GetContinentalness(worldX, worldZ);
            bool inRiver = IsRiverChannel(worldX, worldZ);

            int fillTop = Math.Max(height, WaterLevel);
            if (fillTop >= Chunk.SIZE) fillTop = Chunk.SIZE - 1;

            for (int ly = 0; ly <= fillTop; ly++)
            {
                if (ly > height)
                {
                    // Above terrain surface but at or below water level — fill with water
                    blocks[lx, ly, lz] = BlockType.Water;
                }
                else if (ly == height)
                {
                    // Surface block — choose type based on context
                    if (ly < WaterLevel)
                    {
                        // Underwater surface
                        blocks[lx, ly, lz] = inRiver ? BlockType.Gravel : BlockType.Sand;
                    }
                    else if (height <= WaterLevel + 1)
                    {
                        // Beach / water edge
                        blocks[lx, ly, lz] = BlockType.Sand;
                    }
                    else if (continental > 0.6f && height >= 12)
                    {
                        // Mountain peak — bare stone
                        blocks[lx, ly, lz] = BlockType.Stone;
                    }
                    else
                    {
                        blocks[lx, ly, lz] = BlockType.Grass;
                    }
                }
                else if (ly >= height - 2)
                {
                    // Sub-surface (1-2 blocks below surface)
                    if (ly < WaterLevel && inRiver)
                        blocks[lx, ly, lz] = BlockType.Sand;
                    else if (height <= WaterLevel + 1)
                        blocks[lx, ly, lz] = BlockType.Sand;
                    else
                        blocks[lx, ly, lz] = BlockType.Dirt;
                }
                else
                {
                    // Deep underground
                    blocks[lx, ly, lz] = BlockType.Stone;
                }
            }
        }
    }
}
