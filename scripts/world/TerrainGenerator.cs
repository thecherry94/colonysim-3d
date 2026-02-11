namespace ColonySim;

using Godot;

/// <summary>
/// Generates terrain using FastNoiseLite height maps.
/// Produces seamless terrain across chunk boundaries using world coordinates.
/// </summary>
public class TerrainGenerator
{
    private readonly FastNoiseLite _heightNoise;
    private readonly int _baseHeight;

    public TerrainGenerator(int seed = 42, int baseHeight = 6)
    {
        _baseHeight = baseHeight;

        _heightNoise = new FastNoiseLite();
        _heightNoise.Seed = seed;
        _heightNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _heightNoise.Frequency = 0.02f;
        _heightNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _heightNoise.FractalOctaves = 4;
        _heightNoise.FractalLacunarity = 2.0f;
        _heightNoise.FractalGain = 0.5f;

        GD.Print($"TerrainGenerator initialized: seed={seed}, baseHeight={baseHeight}");
    }

    /// <summary>
    /// Returns the surface height at the given world X/Z coordinate.
    /// The returned value is the Y of the topmost solid block.
    /// </summary>
    public int GetHeight(int worldX, int worldZ)
    {
        // FastNoiseLite returns values in roughly -1..1 range
        float noiseVal = _heightNoise.GetNoise2D(worldX, worldZ);
        // Map to terrain height: base +/- amplitude
        int height = _baseHeight + Mathf.RoundToInt(noiseVal * 6.0f);
        return Mathf.Clamp(height, 1, Chunk.SIZE - 1);
    }

    /// <summary>
    /// Fill a chunk's block array based on noise-generated terrain.
    /// </summary>
    public void GenerateChunkBlocks(BlockType[,,] blocks, Vector3I chunkCoord)
    {
        // Only fill Y=0 chunks for now (single vertical layer)
        if (chunkCoord.Y != 0) return;

        for (int lx = 0; lx < Chunk.SIZE; lx++)
        for (int lz = 0; lz < Chunk.SIZE; lz++)
        {
            int worldX = chunkCoord.X * Chunk.SIZE + lx;
            int worldZ = chunkCoord.Z * Chunk.SIZE + lz;
            int height = GetHeight(worldX, worldZ);

            for (int ly = 0; ly <= height && ly < Chunk.SIZE; ly++)
            {
                if (ly == height)
                    blocks[lx, ly, lz] = BlockType.Grass;
                else if (ly >= height - 2)
                    blocks[lx, ly, lz] = BlockType.Dirt;
                else
                    blocks[lx, ly, lz] = BlockType.Stone;
            }
        }
    }
}
