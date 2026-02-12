namespace ColonySim;

using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Builds procedural ArrayMesh from chunk block data using greedy meshing.
/// Merges adjacent same-type faces into larger rectangles for dramatically fewer triangles.
/// Uses vertex colors for per-face shading (top/side/bottom differentiation).
///
/// OPTIMIZATION: All opaque block types are merged into a SINGLE mesh surface per chunk.
/// Only water gets a separate surface (different shader). This reduces draw calls from
/// ~80k (one surface per block type per chunk) to ~13k (one surface per chunk).
/// </summary>
public static class ChunkMeshGenerator
{
    private const int SIZE = 16;

    private enum FaceType { Top, Bottom, Side }

    /// <summary>
    /// Maps a face direction to its 2D slice coordinate system for greedy meshing.
    /// DepthAxis: which world axis is perpendicular to the face (slices iterate over this).
    /// UAxis/VAxis: the two axes forming the 2D grid within each slice.
    /// FaceAtDepthPlusOne: whether the face plane sits at depth or depth+1.
    /// </summary>
    private struct FaceConfig
    {
        public Vector3I Normal;
        public Vector3 NormalF;
        public FaceType Type;
        public int DepthAxis;  // 0=X, 1=Y, 2=Z
        public int UAxis;
        public int VAxis;
        public bool FaceAtDepthPlusOne;
    }

    private static readonly FaceConfig[] FaceConfigs = new FaceConfig[]
    {
        // Top (+Y): depth=Y, U=X, V=Z, face at d+1
        new() { Normal = new Vector3I(0, 1, 0),  NormalF = Vector3.Up,
                Type = FaceType.Top,    DepthAxis = 1, UAxis = 0, VAxis = 2, FaceAtDepthPlusOne = true },
        // Bottom (-Y): depth=Y, U=X, V=Z, face at d
        new() { Normal = new Vector3I(0, -1, 0), NormalF = Vector3.Down,
                Type = FaceType.Bottom, DepthAxis = 1, UAxis = 0, VAxis = 2, FaceAtDepthPlusOne = false },
        // North (+Z): depth=Z, U=X, V=Y, face at d+1
        new() { Normal = new Vector3I(0, 0, 1),  NormalF = new Vector3(0, 0, 1),
                Type = FaceType.Side,   DepthAxis = 2, UAxis = 0, VAxis = 1, FaceAtDepthPlusOne = true },
        // South (-Z): depth=Z, U=X, V=Y, face at d
        new() { Normal = new Vector3I(0, 0, -1), NormalF = new Vector3(0, 0, -1),
                Type = FaceType.Side,   DepthAxis = 2, UAxis = 0, VAxis = 1, FaceAtDepthPlusOne = false },
        // East (+X): depth=X, U=Z, V=Y, face at d+1
        new() { Normal = new Vector3I(1, 0, 0),  NormalF = new Vector3(1, 0, 0),
                Type = FaceType.Side,   DepthAxis = 0, UAxis = 2, VAxis = 1, FaceAtDepthPlusOne = true },
        // West (-X): depth=X, U=Z, V=Y, face at d
        new() { Normal = new Vector3I(-1, 0, 0), NormalF = new Vector3(-1, 0, 0),
                Type = FaceType.Side,   DepthAxis = 0, UAxis = 2, VAxis = 1, FaceAtDepthPlusOne = false },
    };

    // Thread-local scratch buffers for greedy meshing (safe for parallel chunk generation)
    [ThreadStatic] private static BlockType[] _sliceMask;
    [ThreadStatic] private static bool[] _sliceVisited;

    private static void EnsureBuffers()
    {
        _sliceMask ??= new BlockType[SIZE * SIZE];
        _sliceVisited ??= new bool[SIZE * SIZE];
    }

    /// <summary>
    /// Pre-computed mesh data that can be built on a background thread,
    /// then applied to Godot objects on the main thread.
    /// </summary>
    public struct ChunkMeshData
    {
        public bool IsEmpty;
        // Per-surface data for the render mesh (max 2: opaque + water)
        public SurfaceData[] Surfaces;
        // Collision triangles (flat vertex triples)
        public Vector3[] CollisionFaces;
    }

    public struct SurfaceData
    {
        public bool IsWater;  // true = water shader, false = opaque shader
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public Color[] Colors;
        public int[] Indices;
    }

    /// <summary>
    /// Map abstract slice coordinates (depth, u, v) to concrete block coordinates (x, y, z).
    /// </summary>
    private static void SliceToBlockCoords(in FaceConfig face, int d, int u, int v,
        out int x, out int y, out int z)
    {
        Span<int> pos = stackalloc int[3];
        pos[face.DepthAxis] = d;
        pos[face.UAxis] = u;
        pos[face.VAxis] = v;
        x = pos[0]; y = pos[1]; z = pos[2];
    }

    /// <summary>
    /// Compute the 4 quad vertices for a greedy-merged rectangle.
    /// Vertex order is CW when viewed from outside — verified against original FaceDef for 1×1 quads.
    /// </summary>
    private static void EmitGreedyQuad(int faceIdx, int depth, int u0, int v0, int w, int h,
        out Vector3 vert0, out Vector3 vert1, out Vector3 vert2, out Vector3 vert3)
    {
        float d = FaceConfigs[faceIdx].FaceAtDepthPlusOne ? depth + 1 : depth;
        float u1 = u0 + w;
        float v1 = v0 + h;

        switch (faceIdx)
        {
            case 0: // Top (+Y)
                vert0 = new Vector3(u0, d, v0);
                vert1 = new Vector3(u0, d, v1);
                vert2 = new Vector3(u1, d, v1);
                vert3 = new Vector3(u1, d, v0);
                break;
            case 1: // Bottom (-Y)
                vert0 = new Vector3(u0, d, v1);
                vert1 = new Vector3(u0, d, v0);
                vert2 = new Vector3(u1, d, v0);
                vert3 = new Vector3(u1, d, v1);
                break;
            case 2: // North (+Z)
                vert0 = new Vector3(u1, v0, d);
                vert1 = new Vector3(u1, v1, d);
                vert2 = new Vector3(u0, v1, d);
                vert3 = new Vector3(u0, v0, d);
                break;
            case 3: // South (-Z)
                vert0 = new Vector3(u0, v0, d);
                vert1 = new Vector3(u0, v1, d);
                vert2 = new Vector3(u1, v1, d);
                vert3 = new Vector3(u1, v0, d);
                break;
            case 4: // East (+X)
                vert0 = new Vector3(d, v0, u0);
                vert1 = new Vector3(d, v1, u0);
                vert2 = new Vector3(d, v1, u1);
                vert3 = new Vector3(d, v0, u1);
                break;
            case 5: // West (-X)
                vert0 = new Vector3(d, v0, u1);
                vert1 = new Vector3(d, v1, u1);
                vert2 = new Vector3(d, v1, u0);
                vert3 = new Vector3(d, v0, u0);
                break;
            default:
                vert0 = vert1 = vert2 = vert3 = Vector3.Zero;
                break;
        }
    }

    /// <summary>
    /// Build the slice mask for ALL solid blocks (opaque surface).
    /// Stores the actual BlockType so greedy merge only merges same-type cells,
    /// preserving correct vertex colors per block type.
    /// Culling: emit face only if neighbor is non-solid (Air or Water).
    /// </summary>
    private static void BuildSliceMaskAllSolid(
        BlockType[,,] blocks,
        Func<int, int, int, BlockType> getNeighborBlock,
        int faceIdx, int sliceDepth)
    {
        ref readonly FaceConfig face = ref FaceConfigs[faceIdx];

        for (int v = 0; v < SIZE; v++)
        for (int u = 0; u < SIZE; u++)
        {
            int idx = v * SIZE + u;
            SliceToBlockCoords(face, sliceDepth, u, v, out int x, out int y, out int z);

            BlockType block = blocks[x, y, z];
            if (!BlockData.IsSolid(block))
            {
                _sliceMask[idx] = BlockType.Air;
                continue;
            }

            // Check neighbor for face culling
            int nx = x + face.Normal.X;
            int ny = y + face.Normal.Y;
            int nz = z + face.Normal.Z;

            BlockType neighbor;
            if (nx >= 0 && nx < SIZE && ny >= 0 && ny < SIZE && nz >= 0 && nz < SIZE)
                neighbor = blocks[nx, ny, nz];
            else
                neighbor = getNeighborBlock(nx, ny, nz);

            // Solid neighbor = face is hidden (culled)
            _sliceMask[idx] = BlockData.IsSolid(neighbor) ? BlockType.Air : block;
        }
    }

    /// <summary>
    /// Build the slice mask for water blocks only.
    /// Culling: hide water faces adjacent to solid blocks or other water.
    /// </summary>
    private static void BuildSliceMaskWater(
        BlockType[,,] blocks,
        Func<int, int, int, BlockType> getNeighborBlock,
        int faceIdx, int sliceDepth)
    {
        ref readonly FaceConfig face = ref FaceConfigs[faceIdx];

        for (int v = 0; v < SIZE; v++)
        for (int u = 0; u < SIZE; u++)
        {
            int idx = v * SIZE + u;
            SliceToBlockCoords(face, sliceDepth, u, v, out int x, out int y, out int z);

            if (blocks[x, y, z] != BlockType.Water)
            {
                _sliceMask[idx] = BlockType.Air;
                continue;
            }

            int nx = x + face.Normal.X;
            int ny = y + face.Normal.Y;
            int nz = z + face.Normal.Z;

            BlockType neighbor;
            if (nx >= 0 && nx < SIZE && ny >= 0 && ny < SIZE && nz >= 0 && nz < SIZE)
                neighbor = blocks[nx, ny, nz];
            else
                neighbor = getNeighborBlock(nx, ny, nz);

            // Hide face if neighbor is solid or water
            if (BlockData.IsSolid(neighbor) || neighbor == BlockType.Water)
                _sliceMask[idx] = BlockType.Air;
            else
                _sliceMask[idx] = BlockType.Water;
        }
    }

    /// <summary>
    /// Build the slice mask for collision meshing: marks cells where ANY solid block needs a face.
    /// Merges across different solid types since collision has no visual properties.
    /// </summary>
    private static void BuildCollisionSliceMask(
        BlockType[,,] blocks,
        Func<int, int, int, BlockType> getNeighborBlock,
        int faceIdx, int sliceDepth)
    {
        ref readonly FaceConfig face = ref FaceConfigs[faceIdx];

        for (int v = 0; v < SIZE; v++)
        for (int u = 0; u < SIZE; u++)
        {
            int idx = v * SIZE + u;
            SliceToBlockCoords(face, sliceDepth, u, v, out int x, out int y, out int z);

            if (!BlockData.IsSolid(blocks[x, y, z]))
            {
                _sliceMask[idx] = BlockType.Air;
                continue;
            }

            int nx = x + face.Normal.X;
            int ny = y + face.Normal.Y;
            int nz = z + face.Normal.Z;

            BlockType neighbor;
            if (nx >= 0 && nx < SIZE && ny >= 0 && ny < SIZE && nz >= 0 && nz < SIZE)
                neighbor = blocks[nx, ny, nz];
            else
                neighbor = getNeighborBlock(nx, ny, nz);

            // Use Stone as sentinel — any non-Air value works for the greedy merge
            _sliceMask[idx] = BlockData.IsSolid(neighbor) ? BlockType.Air : BlockType.Stone;
        }
    }

    /// <summary>
    /// Greedy-merge the slice mask and append vertices/normals/colors/indices.
    /// Supports multiple block types in the same surface — looks up color per-quad from the
    /// actual BlockType stored in _sliceMask. Only merges adjacent cells of the SAME type.
    /// </summary>
    private static void GreedyMergeAndEmitMultiType(
        int faceIdx, int sliceDepth,
        List<Vector3> vertices, List<Vector3> normals, List<Color> colors, List<int> indices)
    {
        Array.Clear(_sliceVisited, 0, SIZE * SIZE);

        ref readonly FaceConfig face = ref FaceConfigs[faceIdx];

        for (int v = 0; v < SIZE; v++)
        for (int u = 0; u < SIZE; u++)
        {
            int idx = v * SIZE + u;
            if (_sliceVisited[idx] || _sliceMask[idx] == BlockType.Air) continue;

            BlockType type = _sliceMask[idx];

            // Expand width (along U axis) — only merge same block type
            int w = 1;
            while (u + w < SIZE)
            {
                int nextIdx = v * SIZE + u + w;
                if (_sliceVisited[nextIdx] || _sliceMask[nextIdx] != type) break;
                w++;
            }

            // Expand height (along V axis) — only merge same block type
            int h = 1;
            while (v + h < SIZE)
            {
                bool rowOk = true;
                for (int du = 0; du < w; du++)
                {
                    int checkIdx = (v + h) * SIZE + u + du;
                    if (_sliceVisited[checkIdx] || _sliceMask[checkIdx] != type)
                    {
                        rowOk = false;
                        break;
                    }
                }
                if (!rowOk) break;
                h++;
            }

            // Mark rectangle as visited
            for (int dv = 0; dv < h; dv++)
            for (int du = 0; du < w; du++)
                _sliceVisited[(v + dv) * SIZE + u + du] = true;

            // Look up face color from the actual block type
            Color faceColor = face.Type switch
            {
                FaceType.Top => BlockData.GetColor(type),
                FaceType.Bottom => BlockData.GetBottomColor(type),
                FaceType.Side => BlockData.GetSideColor(type),
                _ => BlockData.GetColor(type),
            };

            // Emit merged quad
            EmitGreedyQuad(faceIdx, sliceDepth, u, v, w, h,
                out Vector3 v0, out Vector3 v1, out Vector3 v2, out Vector3 v3);

            int baseIdx = vertices.Count;
            vertices.Add(v0); vertices.Add(v1); vertices.Add(v2); vertices.Add(v3);
            normals.Add(face.NormalF); normals.Add(face.NormalF);
            normals.Add(face.NormalF); normals.Add(face.NormalF);
            colors.Add(faceColor); colors.Add(faceColor);
            colors.Add(faceColor); colors.Add(faceColor);

            // CW winding: {0,2,1, 0,3,2}
            indices.Add(baseIdx + 0); indices.Add(baseIdx + 2); indices.Add(baseIdx + 1);
            indices.Add(baseIdx + 0); indices.Add(baseIdx + 3); indices.Add(baseIdx + 2);
        }
    }

    /// <summary>
    /// Greedy-merge the slice mask and append flat collision vertex triples.
    /// </summary>
    private static void GreedyMergeCollision(
        int faceIdx, int sliceDepth,
        List<Vector3> collisionVerts)
    {
        Array.Clear(_sliceVisited, 0, SIZE * SIZE);

        for (int v = 0; v < SIZE; v++)
        for (int u = 0; u < SIZE; u++)
        {
            int idx = v * SIZE + u;
            if (_sliceVisited[idx] || _sliceMask[idx] == BlockType.Air) continue;

            BlockType type = _sliceMask[idx];

            // Expand width
            int w = 1;
            while (u + w < SIZE)
            {
                int nextIdx = v * SIZE + u + w;
                if (_sliceVisited[nextIdx] || _sliceMask[nextIdx] != type) break;
                w++;
            }

            // Expand height
            int h = 1;
            while (v + h < SIZE)
            {
                bool rowOk = true;
                for (int du = 0; du < w; du++)
                {
                    int checkIdx = (v + h) * SIZE + u + du;
                    if (_sliceVisited[checkIdx] || _sliceMask[checkIdx] != type)
                    {
                        rowOk = false;
                        break;
                    }
                }
                if (!rowOk) break;
                h++;
            }

            // Mark visited
            for (int dv = 0; dv < h; dv++)
            for (int du = 0; du < w; du++)
                _sliceVisited[(v + dv) * SIZE + u + du] = true;

            // Emit flat vertex triples, CW winding: {v0,v2,v1, v0,v3,v2}
            EmitGreedyQuad(faceIdx, sliceDepth, u, v, w, h,
                out Vector3 v0, out Vector3 v1, out Vector3 v2, out Vector3 v3);

            collisionVerts.Add(v0); collisionVerts.Add(v2); collisionVerts.Add(v1);
            collisionVerts.Add(v0); collisionVerts.Add(v3); collisionVerts.Add(v2);
        }
    }

    /// <summary>
    /// Generate all chunk mesh data (render + collision) on any thread.
    /// Returns raw arrays that can later be applied to Godot objects on the main thread.
    ///
    /// OPTIMIZATION: Produces at most 2 surfaces (one opaque, one water) instead of
    /// one surface per block type. This reduces draw calls by ~6-8x.
    /// Greedy meshing still respects block type boundaries for correct vertex colors.
    /// </summary>
    public static ChunkMeshData GenerateMeshData(
        BlockType[,,] blocks,
        Func<int, int, int, BlockType> getNeighborBlock)
    {
        EnsureBuffers();

        var surfaces = new List<SurfaceData>(2);  // max 2: opaque + water

        // === Pass 1: All opaque/solid blocks → single surface ===
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var colors = new List<Color>();
            var indices = new List<int>();

            for (int faceIdx = 0; faceIdx < 6; faceIdx++)
            for (int slice = 0; slice < SIZE; slice++)
            {
                BuildSliceMaskAllSolid(blocks, getNeighborBlock, faceIdx, slice);
                GreedyMergeAndEmitMultiType(faceIdx, slice, vertices, normals, colors, indices);
            }

            if (vertices.Count > 0)
            {
                surfaces.Add(new SurfaceData
                {
                    IsWater = false,
                    Vertices = vertices.ToArray(),
                    Normals = normals.ToArray(),
                    Colors = colors.ToArray(),
                    Indices = indices.ToArray(),
                });
            }
        }

        // === Pass 2: Water blocks → separate surface (different shader) ===
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var colors = new List<Color>();
            var indices = new List<int>();

            Color waterTop = BlockData.GetColor(BlockType.Water);
            Color waterSide = BlockData.GetSideColor(BlockType.Water);
            Color waterBottom = BlockData.GetBottomColor(BlockType.Water);

            for (int faceIdx = 0; faceIdx < 6; faceIdx++)
            {
                ref readonly FaceConfig face = ref FaceConfigs[faceIdx];
                Color faceColor = face.Type switch
                {
                    FaceType.Top => waterTop,
                    FaceType.Bottom => waterBottom,
                    FaceType.Side => waterSide,
                    _ => waterTop,
                };

                for (int slice = 0; slice < SIZE; slice++)
                {
                    BuildSliceMaskWater(blocks, getNeighborBlock, faceIdx, slice);
                    GreedyMergeAndEmitSingleType(faceIdx, slice, face.NormalF, faceColor,
                        vertices, normals, colors, indices);
                }
            }

            if (vertices.Count > 0)
            {
                surfaces.Add(new SurfaceData
                {
                    IsWater = true,
                    Vertices = vertices.ToArray(),
                    Normals = normals.ToArray(),
                    Colors = colors.ToArray(),
                    Indices = indices.ToArray(),
                });
            }
        }

        // === Collision faces (all solid types merged, unchanged) ===
        var collisionVerts = new List<Vector3>();
        for (int faceIdx = 0; faceIdx < 6; faceIdx++)
        for (int slice = 0; slice < SIZE; slice++)
        {
            BuildCollisionSliceMask(blocks, getNeighborBlock, faceIdx, slice);
            GreedyMergeCollision(faceIdx, slice, collisionVerts);
        }

        return new ChunkMeshData
        {
            IsEmpty = surfaces.Count == 0 && collisionVerts.Count == 0,
            Surfaces = surfaces.ToArray(),
            CollisionFaces = collisionVerts.ToArray(),
        };
    }

    /// <summary>
    /// Greedy-merge for single-type surfaces (water). Uses a fixed color per face direction.
    /// </summary>
    private static void GreedyMergeAndEmitSingleType(
        int faceIdx, int sliceDepth,
        Vector3 normal, Color faceColor,
        List<Vector3> vertices, List<Vector3> normals, List<Color> colors, List<int> indices)
    {
        Array.Clear(_sliceVisited, 0, SIZE * SIZE);

        for (int v = 0; v < SIZE; v++)
        for (int u = 0; u < SIZE; u++)
        {
            int idx = v * SIZE + u;
            if (_sliceVisited[idx] || _sliceMask[idx] == BlockType.Air) continue;

            BlockType type = _sliceMask[idx];

            // Expand width (along U axis)
            int w = 1;
            while (u + w < SIZE)
            {
                int nextIdx = v * SIZE + u + w;
                if (_sliceVisited[nextIdx] || _sliceMask[nextIdx] != type) break;
                w++;
            }

            // Expand height (along V axis)
            int h = 1;
            while (v + h < SIZE)
            {
                bool rowOk = true;
                for (int du = 0; du < w; du++)
                {
                    int checkIdx = (v + h) * SIZE + u + du;
                    if (_sliceVisited[checkIdx] || _sliceMask[checkIdx] != type)
                    {
                        rowOk = false;
                        break;
                    }
                }
                if (!rowOk) break;
                h++;
            }

            // Mark rectangle as visited
            for (int dv = 0; dv < h; dv++)
            for (int du = 0; du < w; du++)
                _sliceVisited[(v + dv) * SIZE + u + du] = true;

            // Emit merged quad
            EmitGreedyQuad(faceIdx, sliceDepth, u, v, w, h,
                out Vector3 v0, out Vector3 v1, out Vector3 v2, out Vector3 v3);

            int baseIdx = vertices.Count;
            vertices.Add(v0); vertices.Add(v1); vertices.Add(v2); vertices.Add(v3);
            normals.Add(normal); normals.Add(normal); normals.Add(normal); normals.Add(normal);
            colors.Add(faceColor); colors.Add(faceColor); colors.Add(faceColor); colors.Add(faceColor);

            // CW winding: {0,2,1, 0,3,2}
            indices.Add(baseIdx + 0); indices.Add(baseIdx + 2); indices.Add(baseIdx + 1);
            indices.Add(baseIdx + 0); indices.Add(baseIdx + 3); indices.Add(baseIdx + 2);
        }
    }

    // Cached shader resources (lazy-loaded on first use, main thread only)
    private static Shader _opaqueShader;
    private static Shader _waterShader;

    private static Shader OpaqueShader =>
        _opaqueShader ??= GD.Load<Shader>("res://shaders/chunk_opaque.gdshader");
    private static Shader WaterShader =>
        _waterShader ??= GD.Load<Shader>("res://shaders/chunk_water.gdshader");

    // Shared material instances — reused across all chunks to avoid per-chunk allocations.
    // Safe because there are no per-material uniforms (all differentiation via vertex colors).
    private static ShaderMaterial _opaqueMaterial;
    private static ShaderMaterial _waterMaterial;

    private static ShaderMaterial OpaqueMaterial
    {
        get
        {
            if (_opaqueMaterial == null)
            {
                _opaqueMaterial = new ShaderMaterial();
                _opaqueMaterial.Shader = OpaqueShader;
            }
            return _opaqueMaterial;
        }
    }

    private static ShaderMaterial WaterMaterial
    {
        get
        {
            if (_waterMaterial == null)
            {
                _waterMaterial = new ShaderMaterial();
                _waterMaterial.Shader = WaterShader;
            }
            return _waterMaterial;
        }
    }

    /// <summary>
    /// Apply pre-computed mesh data to Godot objects. MUST be called on the main thread.
    /// Uses shared ShaderMaterial instances for Y-level slice support.
    /// Max 2 surfaces per mesh: one opaque (all solid blocks), one water.
    /// </summary>
    public static ArrayMesh BuildArrayMesh(SurfaceData[] surfaces)
    {
        var mesh = new ArrayMesh();
        int surfaceCount = 0;

        foreach (var surface in surfaces)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = surface.Vertices;
            arrays[(int)Mesh.ArrayType.Normal] = surface.Normals;
            arrays[(int)Mesh.ArrayType.Color] = surface.Colors;
            arrays[(int)Mesh.ArrayType.Index] = surface.Indices;

            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(surfaceCount, surface.IsWater ? WaterMaterial : OpaqueMaterial);
            surfaceCount++;
        }

        return mesh;
    }

    /// <summary>
    /// Builds an ArrayMesh with at most 2 surfaces (opaque + water) using greedy meshing.
    /// Convenience method that generates data and builds the mesh in one call.
    /// </summary>
    public static ArrayMesh GenerateMesh(
        BlockType[,,] blocks,
        Func<int, int, int, BlockType> getNeighborBlock)
    {
        var data = GenerateMeshData(blocks, getNeighborBlock);
        return BuildArrayMesh(data.Surfaces);
    }

    /// <summary>
    /// Builds a flat Vector3[] of collision triangles for ConcavePolygonShape3D using greedy meshing.
    /// Convenience method — for threaded use, prefer GenerateMeshData() instead.
    /// </summary>
    public static Vector3[] GenerateCollisionFaces(
        BlockType[,,] blocks,
        Func<int, int, int, BlockType> getNeighborBlock)
    {
        var data = GenerateMeshData(blocks, getNeighborBlock);
        return data.CollisionFaces;
    }
}
