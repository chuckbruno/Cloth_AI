using System.Collections.Generic;
using UnityEngine;

namespace GoalNetXPBD
{
    /// <summary>
    /// Converts a Unity Mesh into the particle/edge data used by the XPBD goal-net solver.
    /// Mesh vertices can be welded into fewer simulation particles, then fanned back out
    /// through vertexToParticle when the simulator writes positions to the runtime mesh.
    /// </summary>
    public sealed class GoalNetMesh
    {
        public struct Edge
        {
            public int a;
            public int b;
            public float restLength;

            public Edge(int a, int b, float restLength)
            {
                this.a = a;
                this.b = b;
                this.restLength = restLength;
            }
        }

        public struct BendingConstraint
        {
            public int a;
            public int b;
            public float restLength;

            public BendingConstraint(int a, int b, float restLength)
            {
                this.a = a;
                this.b = b;
                this.restLength = restLength;
            }
        }

        private struct TriangleEdge
        {
            public int opposite;

            public TriangleEdge(int opposite)
            {
                this.opposite = opposite;
            }
        }

        public int meshVertexCount { get; private set; }
        public int particleCount { get; private set; }

        public Vector3[] restPositionsLocal { get; private set; }
        public bool[] isPinned { get; private set; }
        public float[] invMass { get; private set; }
        public int[] vertexToParticle { get; private set; }
        public Edge[] edges { get; private set; }
        public int[][] particleNeighbors { get; private set; }
        public BendingConstraint[] bendingConstraints { get; private set; }

        public bool Initialize(Mesh mesh, float blackThreshold, float weldDistance)
        {
            if (mesh == null)
            {
                Debug.LogError("[GoalNetMesh] Cannot initialize from a null mesh.");
                return false;
            }

            Vector3[] vertices = mesh.vertices;
            Color[] colors = mesh.colors;

            meshVertexCount = vertices != null ? vertices.Length : 0;
            if (meshVertexCount == 0)
            {
                Debug.LogError($"[GoalNetMesh] Mesh '{mesh.name}' has no vertices.");
                return false;
            }

            if (colors == null || colors.Length != meshVertexCount)
            {
                Debug.LogError(
                    $"[GoalNetMesh] Mesh '{mesh.name}' must have vertex colors. " +
                    "Black vertices are required to identify pinned particles.");
                return false;
            }

            BuildParticles(vertices, colors, blackThreshold, weldDistance);
            BuildTopology(mesh);

            int weldedAway = meshVertexCount - particleCount;
            Debug.Log(
                $"[GoalNetMesh] Initialized '{mesh.name}': " +
                $"meshVerts={meshVertexCount}, particles={particleCount} (welded {weldedAway} away), " +
                $"edges={edges.Length}, bending={bendingConstraints.Length}, neighbors={GetAverageNeighborCount():0.0} avg.");

            return true;
        }

        private void BuildParticles(Vector3[] vertices, Color[] colors, float blackThreshold, float weldDistance)
        {
            vertexToParticle = new int[vertices.Length];

            var rest = new List<Vector3>(vertices.Length);
            var pinned = new List<bool>(vertices.Length);
            var counts = new List<int>(vertices.Length);

            if (weldDistance <= 0f)
            {
                for (int i = 0; i < vertices.Length; i++)
                {
                    vertexToParticle[i] = i;
                    rest.Add(vertices[i]);
                    pinned.Add(IsBlack(colors[i], blackThreshold));
                    counts.Add(1);
                }
            }
            else
            {
                BuildWeldedParticles(vertices, colors, blackThreshold, weldDistance, rest, pinned, counts);
            }

            particleCount = rest.Count;
            restPositionsLocal = rest.ToArray();
            isPinned = pinned.ToArray();
            invMass = new float[particleCount];

            for (int i = 0; i < particleCount; i++)
            {
                invMass[i] = isPinned[i] ? 0f : 1f;
            }
        }

        private void BuildWeldedParticles(
            Vector3[] vertices,
            Color[] colors,
            float blackThreshold,
            float weldDistance,
            List<Vector3> rest,
            List<bool> pinned,
            List<int> counts)
        {
            float cellSize = Mathf.Max(weldDistance, 1e-8f);
            float weldDistanceSqr = weldDistance * weldDistance;
            var grid = new Dictionary<Vector3Int, List<int>>();

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 v = vertices[i];
                Vector3Int cell = Quantize(v, cellSize);
                int particle = FindParticleInNeighborCells(v, cell, weldDistanceSqr, grid, rest);

                if (particle < 0)
                {
                    particle = rest.Count;
                    rest.Add(v);
                    pinned.Add(IsBlack(colors[i], blackThreshold));
                    counts.Add(1);

                    if (!grid.TryGetValue(cell, out List<int> bucket))
                    {
                        bucket = new List<int>();
                        grid.Add(cell, bucket);
                    }
                    bucket.Add(particle);
                }
                else
                {
                    int count = counts[particle];
                    rest[particle] = (rest[particle] * count + v) / (count + 1);
                    counts[particle] = count + 1;
                    pinned[particle] = pinned[particle] || IsBlack(colors[i], blackThreshold);
                }

                vertexToParticle[i] = particle;
            }
        }

        private static int FindParticleInNeighborCells(
            Vector3 vertex,
            Vector3Int cell,
            float weldDistanceSqr,
            Dictionary<Vector3Int, List<int>> grid,
            List<Vector3> rest)
        {
            int best = -1;
            float bestDistanceSqr = weldDistanceSqr;

            for (int z = -1; z <= 1; z++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        var key = new Vector3Int(cell.x + x, cell.y + y, cell.z + z);
                        if (!grid.TryGetValue(key, out List<int> bucket)) continue;

                        for (int i = 0; i < bucket.Count; i++)
                        {
                            int particle = bucket[i];
                            float d2 = (rest[particle] - vertex).sqrMagnitude;
                            if (d2 <= bestDistanceSqr)
                            {
                                bestDistanceSqr = d2;
                                best = particle;
                            }
                        }
                    }
                }
            }

            return best;
        }

        private void BuildTopology(Mesh mesh)
        {
            var uniqueEdges = new HashSet<ulong>();
            var structural = new List<Edge>();
            var triangleEdges = new Dictionary<ulong, TriangleEdge>();
            var uniqueBending = new HashSet<ulong>();
            var bending = new List<BendingConstraint>();

            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                int[] triangles = mesh.GetTriangles(subMesh);
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    int a = vertexToParticle[triangles[i]];
                    int b = vertexToParticle[triangles[i + 1]];
                    int c = vertexToParticle[triangles[i + 2]];
                    if (a == b || b == c || c == a) continue;

                    AddStructuralEdge(a, b, uniqueEdges, structural);
                    AddStructuralEdge(b, c, uniqueEdges, structural);
                    AddStructuralEdge(c, a, uniqueEdges, structural);

                    AddTriangleEdge(a, b, c, triangleEdges, uniqueBending, bending);
                    AddTriangleEdge(b, c, a, triangleEdges, uniqueBending, bending);
                    AddTriangleEdge(c, a, b, triangleEdges, uniqueBending, bending);
                }
            }

            edges = structural.ToArray();
            particleNeighbors = BuildParticleNeighbors(structural);
            bendingConstraints = bending.ToArray();
        }

        private int[][] BuildParticleNeighbors(List<Edge> structural)
        {
            var neighbors = new List<int>[particleCount];
            for (int i = 0; i < particleCount; i++)
            {
                neighbors[i] = new List<int>(4);
            }

            for (int i = 0; i < structural.Count; i++)
            {
                Edge edge = structural[i];
                neighbors[edge.a].Add(edge.b);
                neighbors[edge.b].Add(edge.a);
            }

            var result = new int[particleCount][];
            for (int i = 0; i < particleCount; i++)
            {
                result[i] = neighbors[i].ToArray();
            }

            return result;
        }

        private float GetAverageNeighborCount()
        {
            if (particleNeighbors == null || particleNeighbors.Length == 0) return 0f;

            int total = 0;
            for (int i = 0; i < particleNeighbors.Length; i++)
            {
                total += particleNeighbors[i].Length;
            }

            return total / (float)particleNeighbors.Length;
        }

        private void AddStructuralEdge(int a, int b, HashSet<ulong> unique, List<Edge> result)
        {
            ulong key = PairKey(a, b);
            if (!unique.Add(key)) return;

            float restLength = Vector3.Distance(restPositionsLocal[a], restPositionsLocal[b]);
            if (restLength <= 1e-8f) return;

            result.Add(new Edge(a, b, restLength));
        }

        private void AddTriangleEdge(
            int a,
            int b,
            int opposite,
            Dictionary<ulong, TriangleEdge> triangleEdges,
            HashSet<ulong> uniqueBending,
            List<BendingConstraint> bending)
        {
            ulong sharedEdgeKey = PairKey(a, b);
            if (!triangleEdges.TryGetValue(sharedEdgeKey, out TriangleEdge first))
            {
                triangleEdges.Add(sharedEdgeKey, new TriangleEdge(opposite));
                return;
            }

            int x = first.opposite;
            int y = opposite;
            if (x == y) return;

            ulong bendKey = PairKey(x, y);
            if (!uniqueBending.Add(bendKey)) return;

            float restLength = Vector3.Distance(restPositionsLocal[x], restPositionsLocal[y]);
            if (restLength <= 1e-8f) return;

            bending.Add(new BendingConstraint(x, y, restLength));
        }

        private static ulong PairKey(int a, int b)
        {
            int min = Mathf.Min(a, b);
            int max = Mathf.Max(a, b);
            return ((ulong)(uint)min << 32) | (uint)max;
        }

        private static bool IsBlack(Color c, float threshold)
        {
            return c.r <= threshold && c.g <= threshold && c.b <= threshold;
        }

        private static Vector3Int Quantize(Vector3 value, float cellSize)
        {
            return new Vector3Int(
                Mathf.FloorToInt(value.x / cellSize),
                Mathf.FloorToInt(value.y / cellSize),
                Mathf.FloorToInt(value.z / cellSize));
        }
    }
}
