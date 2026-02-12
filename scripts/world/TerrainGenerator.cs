namespace ColonySim;

using Godot;

/// <summary>
/// Multi-layer noise terrain generator with vertical chunk support.
/// 4 noise layers create diverse terrain: flat valleys, rolling hills, mountains, and rivers.
/// Heights span 0-62 across multiple Y chunk layers (default 4 layers = 64 blocks tall).
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

    public const int WaterLevel = 25;
    public const int MaxHeight = 62;
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

        GD.Print($"TerrainGenerator initialized: seed={seed}, waterLevel={WaterLevel}, maxHeight={MaxHeight}");
    }

    /// <summary>
    /// Returns the surface height at the given world X/Z coordinate.
    /// Height is computed from continentalness-scaled elevation + detail noise.
    /// Rivers carve into the terrain where river noise ≈ 0.
    /// Returns a global Y coordinate (0-62), spanning multiple chunk layers.
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

        // Compute height with continentalness-scaled amplitude (scaled for 64-block range)
        float baseHeight = Mathf.Lerp(22.0f, 40.0f, cSquared);
        float amplitude = Mathf.Lerp(4.0f, 18.0f, cSquared);
        float detailAmp = Mathf.Lerp(0.5f, 3.0f, cNorm);

        float rawHeight = baseHeight + elevation * amplitude + detail * detailAmp;

        // River carving: only where terrain is above water and not mountainous
        // Skip if terrain is already at or below water level (lakes/oceans) —
        // carving a river through a lake creates artificial land barriers
        if (continental <= 0.5f && rawHeight > WaterLevel + 1)
        {
            float absRiver = Mathf.Abs(river);
            if (absRiver < RiverWidth)
            {
                // River center: carve down to water level - 1
                rawHeight = WaterLevel - 1;
            }
            else if (absRiver < RiverBankWidth)
            {
                // River banks: smooth slope from terrain to water edge
                float t = (absRiver - RiverWidth) / (RiverBankWidth - RiverWidth);
                t = t * t; // Ease-in for smoother bank slopes
                rawHeight = Mathf.Lerp(WaterLevel, rawHeight, t);
            }
        }

        int height = Mathf.RoundToInt(rawHeight);
        return Mathf.Clamp(height, 2, MaxHeight);
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
    /// Only true where terrain is naturally above water level (not in lakes).
    /// </summary>
    private bool IsRiverChannel(int worldX, int worldZ)
    {
        float continental = _continentalNoise.GetNoise2D(worldX, worldZ);
        if (continental > 0.5f) return false; // No rivers on mountains

        float river = _riverNoise.GetNoise2D(worldX, worldZ);
        if (Mathf.Abs(river) >= RiverWidth) return false;

        // Check if terrain is naturally above water (before river carving)
        float elevation = _elevationNoise.GetNoise2D(worldX, worldZ);
        float detail = _detailNoise.GetNoise2D(worldX, worldZ);
        float cNorm = (continental + 1.0f) * 0.5f;
        float cSquared = cNorm * cNorm;
        float baseH = Mathf.Lerp(22.0f, 40.0f, cSquared);
        float amp = Mathf.Lerp(4.0f, 18.0f, cSquared);
        float detAmp = Mathf.Lerp(0.5f, 3.0f, cNorm);
        float naturalHeight = baseH + elevation * amp + detail * detAmp;

        return naturalHeight > WaterLevel + 1;
    }

    /// <summary>
    /// Fill a chunk's block array based on multi-layer noise terrain.
    /// Supports any Y chunk layer — uses global world Y to determine block type.
    /// </summary>
    public void GenerateChunkBlocks(BlockType[,,] blocks, Vector3I chunkCoord)
    {
        int chunkWorldYBase = chunkCoord.Y * Chunk.SIZE;

        for (int lx = 0; lx < Chunk.SIZE; lx++)
        for (int lz = 0; lz < Chunk.SIZE; lz++)
        {
            int worldX = chunkCoord.X * Chunk.SIZE + lx;
            int worldZ = chunkCoord.Z * Chunk.SIZE + lz;
            int surfaceHeight = GetHeight(worldX, worldZ);
            float continental = GetContinentalness(worldX, worldZ);
            bool inRiver = IsRiverChannel(worldX, worldZ);

            for (int ly = 0; ly < Chunk.SIZE; ly++)
            {
                int worldY = chunkWorldYBase + ly;

                if (worldY > surfaceHeight && worldY > WaterLevel)
                {
                    // Above both terrain and water → Air (default, skip)
                    continue;
                }
                else if (worldY > surfaceHeight && worldY <= WaterLevel)
                {
                    // Above terrain but at or below water level → Water
                    blocks[lx, ly, lz] = BlockType.Water;
                }
                else if (worldY == surfaceHeight)
                {
                    // Surface block
                    blocks[lx, ly, lz] = GetSurfaceBlock(surfaceHeight, continental, inRiver);
                }
                else if (worldY >= surfaceHeight - 3)
                {
                    // Sub-surface (1-3 blocks below surface)
                    blocks[lx, ly, lz] = GetSubSurfaceBlock(worldY, surfaceHeight, inRiver);
                }
                else
                {
                    // Deep underground
                    blocks[lx, ly, lz] = BlockType.Stone;
                }
            }
        }
    }

    /// <summary>
    /// Determine the surface block type based on height, continentalness, and river proximity.
    /// </summary>
    private BlockType GetSurfaceBlock(int surfaceHeight, float continental, bool inRiver)
    {
        if (surfaceHeight < WaterLevel)
        {
            // Underwater surface
            return inRiver ? BlockType.Gravel : BlockType.Sand;
        }
        else if (surfaceHeight <= WaterLevel + 2)
        {
            // Beach / water edge
            return BlockType.Sand;
        }
        else if (continental > 0.6f && surfaceHeight >= 48)
        {
            // Mountain peak — bare stone
            return BlockType.Stone;
        }
        else
        {
            return BlockType.Grass;
        }
    }

    /// <summary>
    /// Determine the sub-surface block type (1-3 blocks below surface).
    /// </summary>
    private BlockType GetSubSurfaceBlock(int worldY, int surfaceHeight, bool inRiver)
    {
        if (worldY < WaterLevel && inRiver)
            return BlockType.Sand;
        else if (surfaceHeight <= WaterLevel + 2)
            return BlockType.Sand; // Beach subsurface
        else
            return BlockType.Dirt;
    }
}
