namespace ColonySim;

using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Builds procedural ArrayMesh from chunk block data using greedy meshing.
/// Merges adjacent same-type faces into larger rectangles for dramatically fewer triangles.
/// Uses vertex colors for per-face shading (top/side/bottom differentiation).
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

    // Reusable per-slice buffers (single-threaded — safe as static)
    private static readonly BlockType[] _sliceMask = new BlockType[SIZE * SIZE];
    private static readonly bool[] _sliceVisited = new bool[SIZE * SIZE];

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
    /// Build the slice mask for render meshing: marks cells where a specific BlockType needs a face.
    /// Cells set to BlockType.Air mean "no face needed" (block is wrong type, or face is culled).
    /// </summary>
    private static void BuildSliceMask(
        BlockType[,,] blocks,
        Func<int, int, int, BlockType> getNeighborBlock,
        int faceIdx, int sliceDepth, BlockType targetType)
    {
        ref readonly FaceConfig face = ref FaceConfigs[faceIdx];

        for (int v = 0; v < SIZE; v++)
        for (int u = 0; u < SIZE; u++)
        {
            int idx = v * SIZE + u;
            SliceToBlockCoords(face, sliceDepth, u, v, out int x, out int y, out int z);

            if (blocks[x, y, z] != targetType)
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

            // Solid blocks cull all faces; water-water faces are hidden
            if (BlockData.IsSolid(neighbor))
                _sliceMask[idx] = BlockType.Air;
            else if (targetType == BlockType.Water && neighbor == BlockType.Water)
                _sliceMask[idx] = BlockType.Air;
            else
                _sliceMask[idx] = targetType;
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
    /// Greedy-merge the slice mask and append vertices/normals/colors/indices for the render mesh.
    /// </summary>
    private static void GreedyMergeAndEmit(
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
    /// Builds an ArrayMesh with one surface per visible block type using greedy meshing.
    /// Merges adjacent same-type faces into larger rectangles for fewer triangles.
    /// </summary>
    public static ArrayMesh GenerateMesh(
        BlockType[,,] blocks,
        Func<int, int, int, BlockType> getNeighborBlock)
    {
        var mesh = new ArrayMesh();
        int totalVertices = 0;
        int totalTriangles = 0;
        int surfaceCount = 0;

        foreach (BlockType blockType in Enum.GetValues<BlockType>())
        {
            if (blockType == BlockType.Air) continue;

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var colors = new List<Color>();
            var indices = new List<int>();

            // Pre-compute face colors for this block type
            Color topColor = BlockData.GetColor(blockType);
            Color sideColor = BlockData.GetSideColor(blockType);
            Color bottomColor = BlockData.GetBottomColor(blockType);

            for (int faceIdx = 0; faceIdx < 6; faceIdx++)
            {
                ref readonly FaceConfig face = ref FaceConfigs[faceIdx];
                Color faceColor = face.Type switch
                {
                    FaceType.Top => topColor,
                    FaceType.Bottom => bottomColor,
                    FaceType.Side => sideColor,
                    _ => topColor,
                };

                for (int slice = 0; slice < SIZE; slice++)
                {
                    BuildSliceMask(blocks, getNeighborBlock, faceIdx, slice, blockType);
                    GreedyMergeAndEmit(faceIdx, slice, face.NormalF, faceColor,
                        vertices, normals, colors, indices);
                }
            }

            if (vertices.Count == 0) continue;

            // Build surface arrays
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            // Material that uses vertex colors
            var material = new StandardMaterial3D();
            material.VertexColorUseAsAlbedo = true;
            material.Roughness = 0.85f;
            material.Metallic = 0.0f;
            if (blockType == BlockType.Water)
            {
                material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                material.Roughness = 0.2f;
                material.Metallic = 0.1f;
            }
            mesh.SurfaceSetMaterial(surfaceCount, material);

            totalVertices += vertices.Count;
            totalTriangles += indices.Count / 3;
            surfaceCount++;
        }

        return mesh;
    }

    /// <summary>
    /// Builds a flat Vector3[] of collision triangles for ConcavePolygonShape3D using greedy meshing.
    /// All solid block types are merged together for maximum rectangle merging.
    /// Every 3 consecutive vertices form one triangle.
    /// </summary>
    public static Vector3[] GenerateCollisionFaces(
        BlockType[,,] blocks,
        Func<int, int, int, BlockType> getNeighborBlock)
    {
        var collisionVerts = new List<Vector3>();

        for (int faceIdx = 0; faceIdx < 6; faceIdx++)
        for (int slice = 0; slice < SIZE; slice++)
        {
            BuildCollisionSliceMask(blocks, getNeighborBlock, faceIdx, slice);
            GreedyMergeCollision(faceIdx, slice, collisionVerts);
        }

        return collisionVerts.ToArray();
    }
}
