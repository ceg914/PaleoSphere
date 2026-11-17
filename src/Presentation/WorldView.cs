using System;
using Godot;
using PaleoSphere.Core;
using PaleoSphere.Ecology;
using PaleoSphere.Simulation;
using PaleoSphere.World;

namespace PaleoSphere.Presentation;

/// <summary>
/// 지형·수면·하늘의 3D 표시. 코어의 지형과 식생 배열을 읽기만 한다. 식물은 VegetationLayer가 그린다.
/// 식생량은 텍스처로 올려 셰이더가 색에 반영하므로, 뜯어 먹힌 자리가 화면에 그대로 드러난다.
/// </summary>
public partial class WorldView : Node3D
{
    private MeshInstance3D _terrainMesh = null!;
    private MeshInstance3D _water = null!;
    private DirectionalLight3D _sun = null!;
    private WorldEnvironment _worldEnv = null!;
    private Godot.Environment _env = null!;
    private ShaderMaterial _terrainMaterial = null!;

    private Image? _vegImage;
    private ImageTexture? _vegTexture;
    private byte[] _vegBytes = Array.Empty<byte>();
    private double _vegTimer;
    private SimulationCore? _sim;

    public override void _Ready()
    {
        _terrainMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://assets/shaders/terrain.gdshader") };
        _terrainMesh = new MeshInstance3D { Name = "Terrain", MaterialOverride = _terrainMaterial };
        AddChild(_terrainMesh);

        _water = new MeshInstance3D
        {
            Name = "Water",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://assets/shaders/water.gdshader") },
        };
        AddChild(_water);

        _sun = new DirectionalLight3D
        {
            Name = "Sun",
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 420f,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
        };
        AddChild(_sun);

        _env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky
            {
                SkyMaterial = new ProceduralSkyMaterial
                {
                    SkyTopColor = new Color(0.24f, 0.42f, 0.70f),
                    SkyHorizonColor = new Color(0.66f, 0.76f, 0.84f),
                    GroundBottomColor = new Color(0.16f, 0.19f, 0.16f),
                    GroundHorizonColor = new Color(0.60f, 0.68f, 0.70f),
                },
            },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            FogEnabled = true,
            FogDensity = 0.00035f,
            FogLightColor = new Color(0.70f, 0.78f, 0.84f),
        };
        _worldEnv = new WorldEnvironment { Environment = _env };
        AddChild(_worldEnv);
    }

    /// <summary>새 세계에 맞춰 지형 메시와 텍스처를 다시 만든다.</summary>
    public void Rebuild(SimulationCore sim)
    {
        _sim = sim;
        TerrainGrid t = sim.World.Terrain;
        int n = t.Size;
        float cs = t.CellSize;

        var verts = new Vector3[n * n];
        var normals = new Vector3[n * n];
        var uvs = new Vector2[n * n];
        for (int cz = 0; cz < n; cz++)
        {
            for (int cx = 0; cx < n; cx++)
            {
                int i = cz * n + cx;
                verts[i] = new Vector3((cx + 0.5f) * cs, t.Height[i], (cz + 0.5f) * cs);
                uvs[i] = new Vector2((cx + 0.5f) / n, (cz + 0.5f) / n);
                float hl = t.Height[cz * n + Math.Max(cx - 1, 0)], hr = t.Height[cz * n + Math.Min(cx + 1, n - 1)];
                float hd = t.Height[Math.Max(cz - 1, 0) * n + cx], hu = t.Height[Math.Min(cz + 1, n - 1) * n + cx];
                normals[i] = new Vector3(hl - hr, 2f * cs, hd - hu).Normalized();
            }
        }
        var indices = new int[(n - 1) * (n - 1) * 6];
        int k = 0;
        for (int cz = 0; cz < n - 1; cz++)
        {
            for (int cx = 0; cx < n - 1; cx++)
            {
                int i00 = cz * n + cx, i10 = i00 + 1, i01 = i00 + n, i11 = i01 + 1;
                indices[k++] = i00; indices[k++] = i10; indices[k++] = i01;
                indices[k++] = i10; indices[k++] = i11; indices[k++] = i01;
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _terrainMesh.Mesh = mesh;

        // 생물군계 색 텍스처(식생이 풍부할 때 / 맨땅일 때).
        var lush = new byte[n * n * 3];
        var bare = new byte[n * n * 3];
        for (int i = 0; i < n * n; i++)
        {
            BiomeDefinition b = sim.World.Biomes[t.Biome[i]];
            lush[i * 3] = ToByte(b.ColorR); lush[i * 3 + 1] = ToByte(b.ColorG); lush[i * 3 + 2] = ToByte(b.ColorB);
            bare[i * 3] = ToByte(b.BareR); bare[i * 3 + 1] = ToByte(b.BareG); bare[i * 3 + 2] = ToByte(b.BareB);
        }
        _terrainMaterial.SetShaderParameter("lush_tex", ImageTexture.CreateFromImage(Image.CreateFromData(n, n, false, Image.Format.Rgb8, lush)));
        _terrainMaterial.SetShaderParameter("bare_tex", ImageTexture.CreateFromImage(Image.CreateFromData(n, n, false, Image.Format.Rgb8, bare)));
        _terrainMaterial.SetShaderParameter("water_level", t.WaterLevel);
        _terrainMaterial.SetShaderParameter("cell_count", (float)n);
        _terrainMaterial.SetShaderParameter("world_size", t.WorldSize);

        _vegBytes = new byte[n * n];
        FillVegetationBytes(sim.World.Vegetation);
        _vegImage = Image.CreateFromData(n, n, false, Image.Format.R8, _vegBytes);
        _vegTexture = ImageTexture.CreateFromImage(_vegImage);
        _terrainMaterial.SetShaderParameter("veg_tex", _vegTexture);

        float size = t.WorldSize;
        _water.Mesh = new PlaneMesh { Size = new Vector2(size, size), SubdivideWidth = 0, SubdivideDepth = 0 };
        _water.Position = new Vector3(size * 0.5f, t.WaterLevel, size * 0.5f);

        SetHighlight(Vector3.Zero, 0f, Colors.White);
    }

    private static byte ToByte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

    private void FillVegetationBytes(VegetationGrid veg)
    {
        for (int i = 0; i < _vegBytes.Length; i++)
        {
            float k = veg.Capacity[i];
            _vegBytes[i] = k > 0f ? ToByte(veg.Biomass[i] / k) : (byte)0;
        }
    }

    /// <summary>도구 범위 표시. radius가 0이면 끈다.</summary>
    public void SetHighlight(Vector3 center, float radius, Color color)
    {
        _terrainMaterial.SetShaderParameter("highlight_center", center);
        _terrainMaterial.SetShaderParameter("highlight_radius", radius);
        _terrainMaterial.SetShaderParameter("highlight_color", color);
    }

    public override void _Process(double delta)
    {
        if (_sim == null) return;

        // 식생 텍스처는 0.4초마다 갱신한다(시뮬레이션의 식생 갱신보다 자주 할 필요가 없다).
        _vegTimer += delta;
        if (_vegTimer >= 0.4 && _vegImage != null && _vegTexture != null)
        {
            _vegTimer = 0;
            FillVegetationBytes(_sim.World.Vegetation);
            _vegImage.SetData(_sim.World.Terrain.Size, _sim.World.Terrain.Size, false, Image.Format.R8, _vegBytes);
            _vegTexture.Update(_vegImage);
        }

        // 해의 위치와 밝기를 시각에 맞춘다.
        float hour = _sim.Clock.TimeOfDayHours;
        float light = _sim.World.Env.Light;
        float sunAngle = (hour - 6f) / 12f * Mathf.Pi;
        bool day = light > 0.001f;
        float elevation = day ? Mathf.Max(0.12f, Mathf.Sin(sunAngle)) : 0.6f;
        float azimuth = day ? Mathf.Cos(sunAngle) : 0.3f;
        var toSun = new Vector3(azimuth, elevation, 0.35f).Normalized();
        _sun.LookAtFromPosition(toSun * 100f, Vector3.Zero, Vector3.Up);
        _sun.LightEnergy = day ? 0.25f + 1.15f * light : 0.22f;
        _sun.LightColor = day
            ? new Color(1f, 0.90f + 0.08f * light, 0.76f + 0.2f * light)
            : new Color(0.55f, 0.65f, 0.95f);
        // 밤에도 개체를 알아볼 수 있을 만큼은 밝게 둔다.
        float sky = 0.16f + 0.84f * light;
        _env.BackgroundEnergyMultiplier = sky;
        _env.AmbientLightEnergy = 0.45f + 0.55f * light;
        _env.FogLightEnergy = sky;
    }
}
