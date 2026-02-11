namespace ColonySim;

using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Builds procedural ArrayMesh from chunk block data with face culling.
/// </summary>
public static class ChunkMeshGenerator
{
    private const int SIZE = 16;

    // Each face: 4 vertex offsets, normal, neighbor check direction
    private struct FaceDef
    {
        public Vector3 V0, V1, V2, V3;
        public Vector3 Normal;
        public Vector3I NeighborDir;
    }

    // 6 face definitions — vertex order is CW when viewed from outside
    private static readonly FaceDef[] Faces = new FaceDef[]
    {
        // Top (+Y)
        new() {
            V0 = new Vector3(0, 1, 0), V1 = new Vector3(0, 1, 1),
            V2 = new Vector3(1, 1, 1), V3 = new Vector3(1, 1, 0),
            Normal = Vector3.Up, NeighborDir = new Vector3I(0, 1, 0)
        },
        // Bottom (-Y)
        new() {
            V0 = new Vector3(0, 0, 1), V1 = new Vector3(0, 0, 0),
            V2 = new Vector3(1, 0, 0), V3 = new Vector3(1, 0, 1),
            Normal = Vector3.Down, NeighborDir = new Vector3I(0, -1, 0)
        },
        // North (+Z)
        new() {
            V0 = new Vector3(1, 0, 1), V1 = new Vector3(1, 1, 1),
            V2 = new Vector3(0, 1, 1), V3 = new Vector3(0, 0, 1),
            Normal = new Vector3(0, 0, 1), NeighborDir = new Vector3I(0, 0, 1)
        },
        // South (-Z)
        new() {
            V0 = new Vector3(0, 0, 0), V1 = new Vector3(0, 1, 0),
            V2 = new Vector3(1, 1, 0), V3 = new Vector3(1, 0, 0),
            Normal = new Vector3(0, 0, -1), NeighborDir = new Vector3I(0, 0, -1)
        },
        // East (+X)
        new() {
            V0 = new Vector3(1, 0, 0), V1 = new Vector3(1, 1, 0),
            V2 = new Vector3(1, 1, 1), V3 = new Vector3(1, 0, 1),
            Normal = new Vector3(1, 0, 0), NeighborDir = new Vector3I(1, 0, 0)
        },
        // West (-X)
        new() {
            V0 = new Vector3(0, 0, 1), V1 = new Vector3(0, 1, 1),
            V2 = new Vector3(0, 1, 0), V3 = new Vector3(0, 0, 0),
            Normal = new Vector3(-1, 0, 0), NeighborDir = new Vector3I(-1, 0, 0)
        },
    };

    /// <summary>
    /// Builds an ArrayMesh with one surface per visible block type.
    /// getNeighborBlock handles out-of-bounds coords (below 0 or >= 16).
    /// </summary>
    public static ArrayMesh GenerateMesh(
        BlockType[,,] blocks,
        Func<int, int, int, BlockType> getNeighborBlock)
    {
        var mesh = new ArrayMesh();
        int totalVertices = 0;
        int totalTriangles = 0;
        int surfaceCount = 0;

        // Iterate each solid block type
        foreach (BlockType blockType in Enum.GetValues<BlockType>())
        {
            if (blockType == BlockType.Air) continue;

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var indices = new List<int>();

            for (int x = 0; x < SIZE; x++)
            for (int y = 0; y < SIZE; y++)
            for (int z = 0; z < SIZE; z++)
            {
                if (blocks[x, y, z] != blockType) continue;

                var blockPos = new Vector3(x, y, z);

                for (int f = 0; f < 6; f++)
                {
                    ref readonly FaceDef face = ref Faces[f];
                    int nx = x + face.NeighborDir.X;
                    int ny = y + face.NeighborDir.Y;
                    int nz = z + face.NeighborDir.Z;

                    // Check neighbor: in-bounds reads from blocks[], out-of-bounds uses callback
                    BlockType neighbor;
                    if (nx >= 0 && nx < SIZE && ny >= 0 && ny < SIZE && nz >= 0 && nz < SIZE)
                        neighbor = blocks[nx, ny, nz];
                    else
                        neighbor = getNeighborBlock(nx, ny, nz);

                    if (BlockData.IsSolid(neighbor)) continue; // Face hidden

                    // Emit quad: 4 vertices, 4 normals, 6 indices (CW winding)
                    int baseIdx = vertices.Count;
                    vertices.Add(blockPos + face.V0);
                    vertices.Add(blockPos + face.V1);
                    vertices.Add(blockPos + face.V2);
                    vertices.Add(blockPos + face.V3);

                    normals.Add(face.Normal);
                    normals.Add(face.Normal);
                    normals.Add(face.Normal);
                    normals.Add(face.Normal);

                    // CW winding: {0,2,1, 0,3,2}
                    indices.Add(baseIdx + 0);
                    indices.Add(baseIdx + 2);
                    indices.Add(baseIdx + 1);
                    indices.Add(baseIdx + 0);
                    indices.Add(baseIdx + 3);
                    indices.Add(baseIdx + 2);
                }
            }

            if (vertices.Count == 0) continue;

            // Build surface arrays
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

            mesh.AddSurface(Mesh.PrimitiveType.Triangles, arrays);

            // Set material for this surface
            var material = new StandardMaterial3D();
            material.AlbedoColor = BlockData.GetColor(blockType);
            material.Roughness = 0.9f;
            material.Metallic = 0.0f;
            mesh.SurfaceSetMaterial(surfaceCount, material);

            totalVertices += vertices.Count;
            totalTriangles += indices.Count / 3;
            surfaceCount++;
        }

        GD.Print($"ChunkMeshGenerator: {totalVertices} vertices, {totalTriangles} triangles across {surfaceCount} surfaces");
        return mesh;
    }

    /// <summary>
    /// Builds a flat Vector3[] of collision triangles for ConcavePolygonShape3D.
    /// Every 3 consecutive vertices form one triangle. All block types combined.
    /// </summary>
    public static Vector3[] GenerateCollisionFaces(
        BlockType[,,] blocks,
        Func<int, int, int, BlockType> getNeighborBlock)
    {
        var collisionVerts = new List<Vector3>();

        for (int x = 0; x < SIZE; x++)
        for (int y = 0; y < SIZE; y++)
        for (int z = 0; z < SIZE; z++)
        {
            if (!BlockData.IsSolid(blocks[x, y, z])) continue;

            var blockPos = new Vector3(x, y, z);

            for (int f = 0; f < 6; f++)
            {
                ref readonly FaceDef face = ref Faces[f];
                int nx = x + face.NeighborDir.X;
                int ny = y + face.NeighborDir.Y;
                int nz = z + face.NeighborDir.Z;

                BlockType neighbor;
                if (nx >= 0 && nx < SIZE && ny >= 0 && ny < SIZE && nz >= 0 && nz < SIZE)
                    neighbor = blocks[nx, ny, nz];
                else
                    neighbor = getNeighborBlock(nx, ny, nz);

                if (BlockData.IsSolid(neighbor)) continue;

                // Flat vertex triples, CW winding: {v0,v2,v1, v0,v3,v2}
                Vector3 v0 = blockPos + face.V0;
                Vector3 v1 = blockPos + face.V1;
                Vector3 v2 = blockPos + face.V2;
                Vector3 v3 = blockPos + face.V3;

                collisionVerts.Add(v0);
                collisionVerts.Add(v2);
                collisionVerts.Add(v1);
                collisionVerts.Add(v0);
                collisionVerts.Add(v3);
                collisionVerts.Add(v2);
            }
        }

        GD.Print($"ChunkMeshGenerator: {collisionVerts.Count / 3} collision triangles");
        return collisionVerts.ToArray();
    }
}
