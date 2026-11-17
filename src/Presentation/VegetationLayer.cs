using System;
using System.Collections.Generic;
using Godot;
using PaleoSphere.Core;
using PaleoSphere.Ecology;
using PaleoSphere.Simulation;
using PaleoSphere.World;

namespace PaleoSphere.Presentation;

/// <summary>
/// 식물 표시. 시뮬레이션에는 영향이 없고 코어의 지형·식생 배열을 읽기만 한다.
/// - 지피 식생(양치류·속새·덤불): 64 m 타일마다 MultiMesh 하나씩 두고 가까운 타일만 그린다.
///   셀의 식생량(바이오매스 ÷ 수용량)에 따라 크기가 줄어들어, 뜯어 먹힌 자리가 듬성해진다.
/// - 나무(침엽수·활엽수·소철류): 생물군계별 확률로 흩뿌린다. 표시용이며 이동을 막지 않는다.
/// 배치는 세계 시드에서 정해지므로 같은 세계는 늘 같은 모습이다.
/// </summary>
public partial class VegetationLayer : Node3D
{
    private enum Cover
    {
        Fern = 0,
        Horsetail = 1,
        Shrub = 2,
        Count = 3,
    }

    private enum TreeKind
    {
        Conifer = 0,
        Broadleaf = 1,
        Cycad = 2,
        Count = 3,
    }

    private sealed class Tile
    {
        public readonly MultiMeshInstance3D?[] Nodes = new MultiMeshInstance3D?[(int)Cover.Count];
        public readonly List<Placement>[] Items = { new(), new(), new() };
        public readonly float[]?[] Buffers = new float[]?[(int)Cover.Count];
    }

    private struct Placement
    {
        public int Cell;
        public Vector3 Position;
        public float Yaw;
        public float BaseScale;
        public Color Tint;
    }

    public const int TileCells = 32;
    public const float CoverRange = 190f;
    private const int Floats = 16;

    private readonly List<Tile> _tiles = new();
    private readonly List<MultiMeshInstance3D> _trees = new();
    private SimulationCore? _sim;
    private int _nextTile;
    private Mesh[] _coverMeshes = Array.Empty<Mesh>();
    private Mesh[] _treeMeshes = Array.Empty<Mesh>();
    private ShaderMaterial _coverMaterial = null!;
    private ShaderMaterial _treeMaterial = null!;

    public int CoverInstanceCount { get; private set; }
    public int TreeInstanceCount { get; private set; }

    public override void _Ready()
    {
        var shader = GD.Load<Shader>("res://assets/shaders/plant.gdshader");
        _coverMaterial = new ShaderMaterial { Shader = shader };
        _coverMaterial.SetShaderParameter("sway", 0.10f);
        _coverMaterial.SetShaderParameter("translucency", 0.12f);
        _treeMaterial = new ShaderMaterial { Shader = shader };
        _treeMaterial.SetShaderParameter("sway", 0.0025f);
        _treeMaterial.SetShaderParameter("sway_speed", 0.7f);
        _treeMaterial.SetShaderParameter("translucency", 0.05f);
        _coverMeshes = new Mesh[] { PlantMeshes.Fern(), PlantMeshes.Horsetail(), PlantMeshes.Shrub() };
        _treeMeshes = new Mesh[] { PlantMeshes.Conifer(), PlantMeshes.Broadleaf(), PlantMeshes.Cycad() };
    }

    public void Clear()
    {
        foreach (Tile t in _tiles)
            foreach (MultiMeshInstance3D? n in t.Nodes) n?.QueueFree();
        _tiles.Clear();
        foreach (MultiMeshInstance3D n in _trees) n.QueueFree();
        _trees.Clear();
        CoverInstanceCount = 0;
        TreeInstanceCount = 0;
        _sim = null;
    }

    private static float H(ulong seed, int a, int b) => ValueNoise.Hash(seed, a, b);

    public void Rebuild(SimulationCore sim)
    {
        Clear();
        _sim = sim;
        TerrainGrid t = sim.World.Terrain;
        BiomeRegistry biomes = sim.World.Biomes;
        int n = t.Size;
        float cs = t.CellSize;
        ulong seed = sim.World.Config.Seed + 7331UL;
        int plain = biomes.TryGetIndex("open_plain", out int bp) ? bp : -1;
        int forest = biomes.TryGetIndex("forest", out int bf) ? bf : -1;
        int wetland = biomes.TryGetIndex("wetland", out int bw) ? bw : -1;
        int upland = biomes.TryGetIndex("upland", out int bu) ? bu : -1;

        int tilesPerSide = (n + TileCells - 1) / TileCells;
        for (int tz = 0; tz < tilesPerSide; tz++)
        {
            for (int tx = 0; tx < tilesPerSide; tx++)
            {
                var tile = new Tile();
                for (int cz = tz * TileCells; cz < Math.Min(n, (tz + 1) * TileCells); cz++)
                {
                    for (int cx = tx * TileCells; cx < Math.Min(n, (tx + 1) * TileCells); cx++)
                    {
                        int cell = cz * n + cx;
                        if (t.IsWater(cell) || !t.Walkable[cell]) continue;
                        int biome = t.Biome[cell];
                        // 생물군계별 지피 식생 종류와 밀도.
                        Cover kind;
                        float chance;
                        float r = H(seed, cx, cz);
                        if (biome == wetland) { kind = r < 0.55f ? Cover.Horsetail : Cover.Fern; chance = 0.85f; }
                        else if (biome == forest) { kind = r < 0.75f ? Cover.Fern : Cover.Shrub; chance = 0.55f; }
                        else if (biome == upland) { kind = Cover.Shrub; chance = 0.22f; }
                        else if (biome == plain) { kind = r < 0.6f ? Cover.Fern : Cover.Shrub; chance = 0.6f; }
                        else continue;
                        // 고르게 흩뿌리지 않고 군락을 이루게 한다(10~30 m 크기의 얼룩).
                        float clump = ValueNoise.Fbm(seed + 9UL, cx * cs / 22f, cz * cs / 22f, 3);
                        chance *= Math.Clamp((clump - 0.32f) * 2.6f, 0f, 1.6f);
                        int count = H(seed + 1, cx, cz) < chance ? (H(seed + 2, cx, cz) < chance * 0.5f ? 2 : 1) : 0;
                        bool dry = biome == upland;
                        for (int k = 0; k < count; k++)
                        {
                            float jx = H(seed + 3UL + (ulong)k * 10, cx, cz), jz = H(seed + 4UL + (ulong)k * 10, cx, cz);
                            var p = new Vec2((cx + jx) * cs, (cz + jz) * cs);
                            float shade = 0.82f + 0.3f * H(seed + 6UL + (ulong)k, cx, cz);
                            // 마른 땅의 덤불은 누렇고 작다.
                            Color dryTint = dry ? new Color(1.25f, 1.12f, 0.75f) : Colors.White;
                            tile.Items[(int)kind].Add(new Placement
                            {
                                Cell = cell,
                                Position = new Vector3(p.X, t.HeightAt(p) - 0.03f, p.Z),
                                Yaw = H(seed + 5UL + (ulong)k, cx, cz) * Mathf.Tau,
                                BaseScale = (dry ? 0.55f : 0.75f) + (dry ? 0.35f : 0.55f) * H(seed + 7UL + (ulong)k, cx, cz),
                                Tint = new Color(shade * (0.95f + 0.1f * jx) * dryTint.R, shade * dryTint.G, shade * (0.9f + 0.1f * jz) * dryTint.B),
                            });
                        }
                    }
                }
                for (int c = 0; c < (int)Cover.Count; c++)
                {
                    List<Placement> items = tile.Items[c];
                    if (items.Count == 0) continue;
                    var mm = new MultiMesh
                    {
                        TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                        UseColors = true,
                        Mesh = _coverMeshes[c],
                    };
                    mm.InstanceCount = items.Count;
                    float minY = float.MaxValue, maxY = float.MinValue;
                    foreach (Placement it in items)
                    {
                        minY = Math.Min(minY, it.Position.Y);
                        maxY = Math.Max(maxY, it.Position.Y);
                    }
                    mm.CustomAabb = new Aabb(new Vector3(tx * TileCells * cs - 2f, minY - 1f, tz * TileCells * cs - 2f),
                        new Vector3(TileCells * cs + 4f, maxY - minY + 4f, TileCells * cs + 4f));
                    var node = new MultiMeshInstance3D
                    {
                        Multimesh = mm,
                        MaterialOverride = _coverMaterial,
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                        VisibilityRangeEnd = CoverRange,
                        VisibilityRangeEndMargin = 20f,
                    };
                    AddChild(node);
                    tile.Nodes[c] = node;
                    tile.Buffers[c] = new float[items.Count * Floats];
                    CoverInstanceCount += items.Count;
                }
                _tiles.Add(tile);
                UpdateTile(tile);
            }
        }

        BuildTrees(sim, seed, plain, forest, wetland, upland);
    }

    private void BuildTrees(SimulationCore sim, ulong seed, int plain, int forest, int wetland, int upland)
    {
        TerrainGrid t = sim.World.Terrain;
        int n = t.Size;
        float cs = t.CellSize;
        var items = new List<Placement>[(int)TreeKind.Count];
        for (int k = 0; k < items.Length; k++) items[k] = new List<Placement>();

        for (int cz = 0; cz < n; cz++)
        {
            for (int cx = 0; cx < n; cx++)
            {
                int cell = cz * n + cx;
                if (t.IsWater(cell)) continue;
                int biome = t.Biome[cell];
                float chance = biome == forest ? 0.05f : biome == wetland ? 0.012f : biome == upland ? 0.004f : biome == plain ? 0.003f : 0f;
                if (H(seed + 40, cx, cz) >= chance) continue;
                float r = H(seed + 41, cx, cz);
                TreeKind kind = biome == forest ? (r < 0.55f ? TreeKind.Conifer : TreeKind.Broadleaf)
                    : biome == wetland ? (r < 0.5f ? TreeKind.Conifer : TreeKind.Cycad)
                    : biome == upland ? (r < 0.6f ? TreeKind.Conifer : TreeKind.Cycad)
                    : (r < 0.5f ? TreeKind.Broadleaf : TreeKind.Cycad);
                var p = new Vec2((cx + H(seed + 42, cx, cz)) * cs, (cz + H(seed + 43, cx, cz)) * cs);
                float shade = 0.8f + 0.35f * H(seed + 44, cx, cz);
                items[(int)kind].Add(new Placement
                {
                    Cell = cell,
                    Position = new Vector3(p.X, t.HeightAt(p) - 0.2f, p.Z),
                    Yaw = H(seed + 45, cx, cz) * Mathf.Tau,
                    BaseScale = 0.7f + 0.6f * H(seed + 46, cx, cz),
                    Tint = new Color(shade * (0.9f + 0.2f * r), shade, shade * 0.95f),
                });
            }
        }

        for (int k = 0; k < items.Length; k++)
        {
            if (items[k].Count == 0) continue;
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = _treeMeshes[k] };
            mm.InstanceCount = items[k].Count;
            var buffer = new float[items[k].Count * Floats];
            for (int i = 0; i < items[k].Count; i++) Write(buffer, i, items[k][i], items[k][i].BaseScale);
            RenderingServer.MultimeshSetBuffer(mm.GetRid(), buffer);
            mm.CustomAabb = new Aabb(new Vector3(-20f, -50f, -20f), new Vector3(t.WorldSize + 40f, 200f, t.WorldSize + 40f));
            var node = new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = _treeMaterial };
            AddChild(node);
            _trees.Add(node);
            TreeInstanceCount += items[k].Count;
        }
    }

    /// <summary>MultiMesh 버퍼 한 칸(변환 12개 + 색 4개, 행 우선).</summary>
    private static void Write(float[] buffer, int index, in Placement it, float scale)
    {
        float c = MathF.Cos(it.Yaw) * scale, s = MathF.Sin(it.Yaw) * scale;
        int o = index * Floats;
        buffer[o + 0] = c; buffer[o + 1] = 0f; buffer[o + 2] = s; buffer[o + 3] = it.Position.X;
        buffer[o + 4] = 0f; buffer[o + 5] = scale; buffer[o + 6] = 0f; buffer[o + 7] = it.Position.Y;
        buffer[o + 8] = -s; buffer[o + 9] = 0f; buffer[o + 10] = c; buffer[o + 11] = it.Position.Z;
        buffer[o + 12] = it.Tint.R; buffer[o + 13] = it.Tint.G; buffer[o + 14] = it.Tint.B; buffer[o + 15] = 1f;
    }

    /// <summary>타일의 지피 식생 크기를 지금 식생량에 맞춘다.</summary>
    private void UpdateTile(Tile tile)
    {
        if (_sim == null) return;
        VegetationGrid veg = _sim.World.Vegetation;
        for (int c = 0; c < (int)Cover.Count; c++)
        {
            MultiMeshInstance3D? node = tile.Nodes[c];
            float[]? buffer = tile.Buffers[c];
            if (node == null || buffer == null) continue;
            List<Placement> items = tile.Items[c];
            for (int i = 0; i < items.Count; i++)
            {
                Placement it = items[i];
                float k = veg.Capacity[it.Cell];
                float fill = k > 0f ? veg.Biomass[it.Cell] / k : 0f;
                // 거의 다 먹힌 셀은 그루터기만 남기고, 나머지는 식생량의 제곱근에 비례해 키운다.
                float scale = fill < 0.12f ? 0.12f : 0.3f + 0.7f * MathF.Sqrt(fill);
                Write(buffer, i, it, it.BaseScale * scale);
            }
            RenderingServer.MultimeshSetBuffer(node.Multimesh.GetRid(), buffer);
        }
    }

    public override void _Process(double delta)
    {
        if (_sim == null || _tiles.Count == 0) return;
        // 프레임마다 타일 두 개씩 돌아가며 갱신한다(8×8 타일이면 약 0.5초에 한 바퀴).
        for (int i = 0; i < 2; i++)
        {
            _nextTile = (_nextTile + 1) % _tiles.Count;
            UpdateTile(_tiles[_nextTile]);
        }
    }
}
