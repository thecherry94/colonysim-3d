namespace ColonySim;

/// <summary>
/// Deterministic tree placement using grid-based spacing.
/// The world is divided into TreeGridSize×TreeGridSize cells.
/// Each cell can spawn at most one tree at a deterministic position.
///
/// Trees are placed during terrain generation and span:
///   - Trunk: 4-6 blocks of Wood above surface
///   - Lower canopy: 2 layers of Leaves at radius 2 (diamond shape)
///   - Upper canopy: 2 layers of Leaves at radius 1 (cross shape)
///
/// Cross-chunk correctness: each chunk checks tree cells in a border
/// around itself, so trees at chunk edges are placed consistently
/// by both neighboring chunks.
/// </summary>
public static class TreeGenerator
{
    /// <summary>
    /// Grid cell size in blocks. One tree candidate per cell.
    /// Guarantees minimum ~4-block spacing between trees.
    /// </summary>
    public const int TreeGridSize = 4;

    /// <summary>
    /// Maximum horizontal reach of canopy from trunk center.
    /// Used to determine how far outside a chunk we need to check for trees.
    /// </summary>
    public const int TreeInfluenceRadius = 2;

    /// <summary>
    /// Deterministic integer hash for world position.
    /// Returns a pseudo-random uint for any (x, z, seed) triple.
    /// </summary>
    private static uint PositionHash(int x, int z, int seed)
    {
        uint h = (uint)(x * 374761393 + z * 668265263 + seed * 2147483647);
        h = (h ^ (h >> 13)) * 1274126177;
        h = h ^ (h >> 16);
        return h;
    }

    /// <summary>
    /// Integer floor division (rounds toward negative infinity).
    /// C# integer division truncates toward zero, which gives wrong
    /// results for negative coordinates.
    /// </summary>
    private static int FloorDiv(int a, int b)
    {
        return a >= 0 ? a / b : (a - b + 1) / b;
    }

    /// <summary>
    /// Check if a grid cell contains a tree, and if so, return its world position.
    /// The density check uses the biome's TreeDensity threshold.
    /// </summary>
    /// <param name="cellX">Grid cell X coordinate (world X / TreeGridSize)</param>
    /// <param name="cellZ">Grid cell Z coordinate (world Z / TreeGridSize)</param>
    /// <param name="seed">World seed</param>
    /// <param name="density">Biome tree density threshold (0.0 - 1.0)</param>
    /// <param name="treeX">Output: world X of tree trunk</param>
    /// <param name="treeZ">Output: world Z of tree trunk</param>
    /// <returns>True if this cell has a tree</returns>
    public static bool TryGetTreeInCell(int cellX, int cellZ, int seed, float density,
                                         out int treeX, out int treeZ)
    {
        treeX = treeZ = 0;
        if (density <= 0f) return false;

        // Hash the cell to check probability
        uint cellHash = PositionHash(cellX, cellZ, seed + 9999);
        float prob = (cellHash & 0xFFFF) / 65536f;
        if (prob >= density) return false;

        // Determine tree position within the cell
        uint posHash = PositionHash(cellX, cellZ, seed + 12345);
        treeX = cellX * TreeGridSize + (int)(posHash % (uint)TreeGridSize);
        treeZ = cellZ * TreeGridSize + (int)((posHash >> 8) % (uint)TreeGridSize);
        return true;
    }

    /// <summary>
    /// Deterministic trunk height for a tree at the given world position.
    /// Returns 4, 5, or 6 blocks.
    /// </summary>
    public static int GetTreeHeight(int treeX, int treeZ, int seed)
    {
        uint hash = PositionHash(treeX, treeZ, seed + 7777);
        return 4 + (int)(hash % 3);
    }

    /// <summary>
    /// Place all blocks for a tree rooted at (treeX, surfaceY, treeZ) into
    /// a chunk's block array. Only blocks that fall within the chunk's bounds
    /// are written. Existing non-Air blocks are not overwritten.
    /// </summary>
    /// <param name="treeX">World X of tree trunk</param>
    /// <param name="treeZ">World Z of tree trunk</param>
    /// <param name="surfaceY">World Y of the ground surface (tree grows above this)</param>
    /// <param name="seed">World seed</param>
    /// <param name="blocks">Chunk's block array to write into</param>
    /// <param name="chunkMinX">World X of chunk's (0,0,0) corner</param>
    /// <param name="chunkMinY">World Y of chunk's (0,0,0) corner</param>
    /// <param name="chunkMinZ">World Z of chunk's (0,0,0) corner</param>
    public static void PlaceTreeBlocks(int treeX, int treeZ, int surfaceY, int seed,
                                        BlockType[,,] blocks,
                                        int chunkMinX, int chunkMinY, int chunkMinZ)
    {
        int trunkHeight = GetTreeHeight(treeX, treeZ, seed);
        int trunkTop = surfaceY + trunkHeight;

        // Trunk: vertical Wood column from surfaceY+1 to trunkTop
        for (int y = surfaceY + 1; y <= trunkTop; y++)
            TryPlace(blocks, treeX, y, treeZ, BlockType.Wood, chunkMinX, chunkMinY, chunkMinZ);

        // Lower canopy: 2 layers at trunkTop-1 and trunkTop, radius 2 (diamond)
        for (int dy = -1; dy <= 0; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dz = -2; dz <= 2; dz++)
                {
                    int distSq = dx * dx + dz * dz;
                    if (distSq > 4 || distSq == 0) continue; // outside radius 2, or trunk column
                    TryPlace(blocks, treeX + dx, trunkTop + dy, treeZ + dz,
                             BlockType.Leaves, chunkMinX, chunkMinY, chunkMinZ);
                }
            }
        }

        // Upper canopy: 2 layers at trunkTop+1 and trunkTop+2, radius 1 (cross/plus)
        for (int dy = 1; dy <= 2; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx * dx + dz * dz > 1) continue; // cross shape only
                    TryPlace(blocks, treeX + dx, trunkTop + dy, treeZ + dz,
                             BlockType.Leaves, chunkMinX, chunkMinY, chunkMinZ);
                }
            }
        }
    }

    /// <summary>
    /// Try to place a block at a world position into the chunk's local array.
    /// Skips if outside chunk bounds or if the position already has a non-Air block.
    /// </summary>
    private static void TryPlace(BlockType[,,] blocks, int wx, int wy, int wz,
                                  BlockType type,
                                  int chunkMinX, int chunkMinY, int chunkMinZ)
    {
        int lx = wx - chunkMinX;
        int ly = wy - chunkMinY;
        int lz = wz - chunkMinZ;

        if (lx < 0 || lx >= Chunk.SIZE || ly < 0 || ly >= Chunk.SIZE || lz < 0 || lz >= Chunk.SIZE)
            return;

        // Only place in air — don't overwrite terrain or other trees
        if (blocks[lx, ly, lz] == BlockType.Air)
            blocks[lx, ly, lz] = type;
    }
}
