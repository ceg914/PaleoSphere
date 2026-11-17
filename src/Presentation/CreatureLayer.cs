using System;
using System.Collections.Generic;
using Godot;
using PaleoSphere.Core;
using PaleoSphere.Creatures;
using PaleoSphere.Simulation;
using PaleoSphere.World;
using Section = PaleoSphere.Presentation.CreatureMeshBuilder.Section;

namespace PaleoSphere.Presentation;

/// <summary>
/// 개체의 3D 표시. 종 데이터에 리깅 모델이 있으면 그 모델과 애니메이션을 쓰고(ModelProxy),
/// 없거나 불러오지 못하면 코드로 만든 메시(CreatureMeshBuilder)를 관절 노드로 움직인다(CodeProxy).
/// 동작(서기·걷기·달리기·먹기·마시기·쉬기)에 따라 자세를 바꾸고, 걷기·달리기 재생 속도는
/// 실제 이동 속도에 맞춰 발이 미끄러지지 않게 한다.
/// 위치는 스냅샷의 이전·현재 값을 보간하므로 틱 속도와 프레임률이 달라도 부드럽다.
/// </summary>
public partial class CreatureLayer : Node3D
{
    private sealed class SpeciesMeshes
    {
        public ArrayMesh Torso = null!, Head = null!, Tail = null!;
        public ArrayMesh UpperFront = null!, LowerFront = null!, UpperHind = null!, LowerHind = null!;
        public Vector3 HeadPivot, TailPivot;
        public Vector3[] HipPositions = new Vector3[4];
        public float UpperFrontLength, UpperHindLength, BellyHeight;
        public StandardMaterial3D MarkerMaterial = null!;
    }

    /// <summary>종별 모델 정보(한 번만 불러온다). Scene이 null이면 코드 메시를 쓴다.</summary>
    private sealed class SpeciesModelCache
    {
        public PackedScene? Scene;
        public float Scale = 1f;
        public float WalkSpeed, RunSpeed;
        public float RestDrop;
        public readonly Dictionary<string, string> Animations = new();
        public string Error = "";
    }

    private sealed class Proxy
    {
        // 모델을 쓰는 경우.
        public Node3D? Model;
        public AnimationPlayer? Player;
        public CreatureRigModifier? Rig;
        public string CurrentAnimation = "";
        public float Heading;
        public float Graze;
        public float RestAmount;

        public Node3D Root = null!;
        public Node3D Body = null!;
        public Node3D Head = null!;
        public Node3D Tail = null!;
        public readonly Node3D[] Hips = new Node3D[4];
        public readonly Node3D[] Knees = new Node3D[4];
        public MeshInstance3D Marker = null!;
        public int SpeciesIndex;
        public float Phase;
        public float HeadPitch;
        public float Crouch;
        public bool Seen;
    }

    /// <summary>시뮬레이션의 사체 하나에 대응하는 화면 노드.</summary>
    private sealed class CarcassView
    {
        public Node3D Node = null!;
        public float SinkDepth;
        public float Ground;
        public bool Seen;
    }

    private readonly Dictionary<int, Proxy> _proxies = new();
    private readonly Dictionary<int, Stack<Proxy>> _pool = new();
    private readonly Dictionary<int, SpeciesMeshes> _meshes = new();
    private readonly Dictionary<int, SpeciesModelCache> _models = new();
    private readonly Dictionary<int, CarcassView> _carcasses = new();
    private readonly List<int> _stale = new();
    private MeshInstance3D _selectionRing = null!;

    // 정점 색은 sRGB 값으로 넣었으므로 그렇게 해석하게 한다(안 하면 색이 하얗게 뜬다).
    private static readonly StandardMaterial3D SkinMaterial = new() { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.9f };
    private static readonly Mesh MarkerMesh = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 8, Rings = 4 };

    public int SelectedId { get; set; } = -1;

    /// <summary>선택한 개체의 보간된 위치(추적 카메라용). 선택이 없으면 null.</summary>
    public Vector3? SelectedPosition { get; private set; }

    /// <summary>이 거리보다 멀면 다리를 그리지 않는다.</summary>
    public const float DetailDistance = 260f;

    /// <summary>이 거리보다 멀면 개체 위에 위치 표식을 띄운다(멀리서도 찾을 수 있게).</summary>
    public const float MarkerDistance = 90f;

    public override void _Ready()
    {
        _selectionRing = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 0.95f, OuterRadius = 1f, Rings = 48, RingSegments = 6 },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(1f, 0.86f, 0.25f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };
        AddChild(_selectionRing);
    }

    public void Clear()
    {
        foreach (Proxy p in _proxies.Values) p.Root.QueueFree();
        _proxies.Clear();
        foreach (Stack<Proxy> stack in _pool.Values)
            foreach (Proxy p in stack) p.Root.QueueFree();
        _pool.Clear();
        foreach (CarcassView v in _carcasses.Values) v.Node.QueueFree();
        _carcasses.Clear();
        _meshes.Clear();
        _models.Clear();
        SelectedId = -1;
        SelectedPosition = null;
        _selectionRing.Visible = false;
    }

    // ------------------------------------------------------------ 메시

    private static Section[] Sections(float L, params (float Z, float Y, float W, float H)[] s)
    {
        var result = new Section[s.Length];
        for (int i = 0; i < s.Length; i++) result[i] = new Section(s[i].Z * L, s[i].Y * L, s[i].W * L, s[i].H * L);
        return result;
    }

    /// <summary>
    /// 몸길이 L인 네발 동물의 메시를 만든다. 수치는 몸길이에 대한 비율이고 모델의 앞은 -Z다.
    /// ceratopsian이면 프릴과 뿔 세 개를 붙인다(화석으로 확인되는 윤곽만 흉내 낸 것).
    /// </summary>
    private SpeciesMeshes MeshesFor(int speciesIndex, SpeciesDefinition sp)
    {
        if (_meshes.TryGetValue(speciesIndex, out SpeciesMeshes? cached)) return cached;
        float L = sp.BodyLengthM;
        bool ceratopsian = sp.ProxyShape == "ceratopsian";
        var skin = new Color(sp.ProxyColorR, sp.ProxyColorG, sp.ProxyColorB);
        Color back = skin.Darkened(0.18f);
        Color belly = skin.Lerp(new Color(0.86f, 0.82f, 0.70f), 0.6f);
        var bone = new Color(0.90f, 0.86f, 0.74f);
        var m = new SpeciesMeshes
        {
            HeadPivot = new Vector3(0f, 0.255f, -0.20f) * L,
            TailPivot = new Vector3(0f, 0.250f, 0.21f) * L,
            UpperFrontLength = 0.100f * L,
            UpperHindLength = 0.110f * L,
            BellyHeight = 0.15f * L,
            MarkerMaterial = new StandardMaterial3D { AlbedoColor = skin.Lightened(0.55f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
        };

        CreatureMeshBuilder NewBuilder(float low, float high) => new() { Back = back, Belly = belly, ShadeLow = low * L, ShadeHigh = high * L };

        // 몸통: 목 뒤에서 꼬리 밑동까지.
        CreatureMeshBuilder torso = NewBuilder(0.17f, 0.34f);
        torso.AddBody(Sections(L,
            (-0.22f, 0.250f, 0.040f, 0.050f), (-0.16f, 0.262f, 0.085f, 0.100f), (-0.07f, 0.275f, 0.115f, 0.125f),
            (0.03f, 0.280f, 0.120f, 0.130f), (0.12f, 0.275f, 0.100f, 0.112f), (0.20f, 0.258f, 0.060f, 0.072f),
            (0.25f, 0.245f, 0.042f, 0.050f)), sides: 14);
        m.Torso = torso.Build();

        // 꼬리: 밑동 관절 기준 좌표.
        CreatureMeshBuilder tail = NewBuilder(-0.09f, 0.05f);
        tail.AddBody(Sections(L,
            (0.00f, 0.000f, 0.052f, 0.062f), (0.07f, -0.012f, 0.036f, 0.042f), (0.15f, -0.030f, 0.022f, 0.026f),
            (0.23f, -0.050f, 0.010f, 0.012f), (0.29f, -0.065f, 0.003f, 0.004f)), sides: 10);
        m.Tail = tail.Build();

        // 머리: 목 관절 기준 좌표.
        CreatureMeshBuilder head = NewBuilder(-0.07f, 0.06f);
        head.AddBody(Sections(L,
            (0.03f, 0.000f, 0.050f, 0.060f), (-0.04f, 0.000f, 0.060f, 0.068f), (-0.10f, -0.008f, 0.050f, 0.058f),
            (-0.17f, -0.022f, 0.034f, 0.042f), (-0.22f, -0.036f, 0.018f, 0.026f), (-0.245f, -0.048f, 0.004f, 0.006f)), sides: 12);
        var eye = new Color(0.04f, 0.04f, 0.04f);
        head.AddEllipsoid(new Vector3(-0.047f, 0.012f, -0.105f) * L, Vector3.One * 0.008f * L, eye, segments: 6, rings: 4);
        head.AddEllipsoid(new Vector3(0.047f, 0.012f, -0.105f) * L, Vector3.One * 0.008f * L, eye, segments: 6, rings: 4);
        if (ceratopsian)
        {
            // 프릴: 뒤로 젖힌 얇은 방패.
            head.ShadeLow = -0.02f * L;
            head.ShadeHigh = 0.20f * L;
            head.AddEllipsoid(new Vector3(0f, 0.085f, 0.040f) * L, new Vector3(0.150f, 0.125f, 0.018f) * L, back,
                new Basis(Vector3.Right, Mathf.DegToRad(-24f)), segments: 14, rings: 8, shadeAsBody: true);
            // 눈 위 뿔 둘과 코 뿔.
            head.AddCone(new Vector3(-0.036f, 0.045f, -0.085f) * L, new Vector3(-0.052f, 0.140f, -0.205f) * L, 0.015f * L, 0f, bone);
            head.AddCone(new Vector3(0.036f, 0.045f, -0.085f) * L, new Vector3(0.052f, 0.140f, -0.205f) * L, 0.015f * L, 0f, bone);
            head.AddCone(new Vector3(0f, 0.012f, -0.185f) * L, new Vector3(0f, 0.058f, -0.202f) * L, 0.012f * L, 0f, bone, sides: 6);
        }
        m.Head = head.Build();

        // 다리: 위·아래 두 마디와 발. 엉덩이·무릎 관절 기준 좌표.
        ArrayMesh Upper(float length, float top, float bottom)
        {
            CreatureMeshBuilder b = NewBuilder(-0.14f, 0.02f);
            b.AddCone(new Vector3(0f, 0.012f * L, 0f), new Vector3(0f, -length, 0f), top * L, bottom * L, back, 9, shadeAsBody: true);
            return b.Build();
        }
        ArrayMesh Lower(float length, float top, float bottom)
        {
            CreatureMeshBuilder b = NewBuilder(-0.02f, 0.14f);
            b.AddCone(new Vector3(0f, 0.008f * L, 0f), new Vector3(0f, -length + 0.012f * L, 0f), top * L, bottom * L, back, 9, shadeAsBody: true);
            b.AddEllipsoid(new Vector3(0f, -length + 0.014f * L, -0.010f * L), new Vector3(0.040f, 0.016f, 0.046f) * L, belly.Darkened(0.25f), segments: 8, rings: 4);
            return b.Build();
        }
        float frontTotal = 0.215f * L, hindTotal = 0.235f * L;
        m.UpperFront = Upper(m.UpperFrontLength, 0.048f, 0.036f);
        m.LowerFront = Lower(frontTotal - m.UpperFrontLength, 0.036f, 0.031f);
        m.UpperHind = Upper(m.UpperHindLength, 0.060f, 0.042f);
        m.LowerHind = Lower(hindTotal - m.UpperHindLength, 0.042f, 0.034f);
        m.HipPositions[0] = new Vector3(-0.085f * L, frontTotal, -0.135f * L);
        m.HipPositions[1] = new Vector3(0.085f * L, frontTotal, -0.135f * L);
        m.HipPositions[2] = new Vector3(-0.085f * L, hindTotal, 0.115f * L);
        m.HipPositions[3] = new Vector3(0.085f * L, hindTotal, 0.115f * L);

        _meshes[speciesIndex] = m;
        return m;
    }

    private static MeshInstance3D Instance(Node3D parent, Mesh mesh)
    {
        var node = new MeshInstance3D { Mesh = mesh, MaterialOverride = SkinMaterial };
        parent.AddChild(node);
        return node;
    }

    // ------------------------------------------------------------ 리깅 모델

    /// <summary>종의 모델을 불러와 크기·보폭·애니메이션 이름을 정리한다. 실패하면 Scene을 비워 둔다.</summary>
    private SpeciesModelCache ModelFor(int speciesIndex, SpeciesDefinition sp)
    {
        if (_models.TryGetValue(speciesIndex, out SpeciesModelCache? cached)) return cached;
        var cache = new SpeciesModelCache();
        _models[speciesIndex] = cache;
        SpeciesModel? info = sp.Model;
        if (info == null) return cache;
        if (!ResourceLoader.Exists(info.Path))
        {
            cache.Error = "모델 파일이 없습니다: " + info.Path;
            GD.PushWarning(cache.Error);
            return cache;
        }
        if (GD.Load(info.Path) is not PackedScene scene)
        {
            cache.Error = "모델을 불러오지 못했습니다: " + info.Path;
            GD.PushWarning(cache.Error);
            return cache;
        }

        // 애니메이션 이름 대응과 반복 설정을 위해 한 번 펼쳐 본다.
        Node3D probe = scene.Instantiate<Node3D>();
        AnimationPlayer? player = FindFirst<AnimationPlayer>(probe);
        Skeleton3D? skel = FindFirst<Skeleton3D>(probe);
        if (player == null || skel == null)
        {
            cache.Error = "모델에 AnimationPlayer나 Skeleton3D가 없습니다: " + info.Path;
            GD.PushWarning(cache.Error);
            probe.Free();
            return cache;
        }
        foreach (KeyValuePair<string, string> pair in info.Animations)
        {
            foreach (string full in player.GetAnimationList())
            {
                if (!full.EndsWith(pair.Value, StringComparison.Ordinal)) continue;
                cache.Animations[pair.Key] = full;
                Animation anim = player.GetAnimation(full);
                anim.LoopMode = pair.Key is "idle" or "walk" or "run" or "attack" ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
            }
        }
        if (!cache.Animations.ContainsKey("idle") || !cache.Animations.ContainsKey("walk") || !cache.Animations.ContainsKey("run"))
        {
            cache.Error = "모델에 idle·walk·run 애니메이션이 없습니다: " + info.Path;
            GD.PushWarning(cache.Error);
            probe.Free();
            return cache;
        }

        // 엎드릴 때 낮출 거리: 엉덩이 높이의 45%.
        int hips = skel.FindBone("Hips");
        Transform3D toModel = SkeletonToModel(probe, skel);
        float hipHeight = hips >= 0 ? (toModel * skel.GetBoneGlobalRest(hips).Origin).Y : info.LengthUnits * 0.3f;
        cache.RestDrop = hipHeight * 0.45f;
        probe.Free();

        cache.Scene = scene;
        cache.Scale = sp.BodyLengthM / info.LengthUnits;
        cache.WalkSpeed = info.WalkUnitsPerSecond * cache.Scale;
        cache.RunSpeed = info.RunUnitsPerSecond * cache.Scale;
        return cache;
    }

    private static T? FindFirst<T>(Node node) where T : Node
    {
        if (node is T match) return match;
        foreach (Node child in node.GetChildren())
        {
            T? found = FindFirst<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    private static Transform3D SkeletonToModel(Node3D modelRoot, Node3D skeleton)
    {
        Transform3D t = Transform3D.Identity;
        for (Node? n = skeleton; n != null && n != modelRoot; n = n.GetParent())
            if (n is Node3D n3) t = n3.Transform * t;
        return t;
    }

    private Proxy? BuildModel(int speciesIndex, SpeciesDefinition sp)
    {
        SpeciesModelCache cache = ModelFor(speciesIndex, sp);
        if (cache.Scene == null) return null;
        Node3D model = cache.Scene.Instantiate<Node3D>();
        model.Scale = Vector3.One * cache.Scale;
        AnimationPlayer player = FindFirst<AnimationPlayer>(model)!;
        Skeleton3D skel = FindFirst<Skeleton3D>(model)!;
        var rig = new CreatureRigModifier { SkeletonToModel = SkeletonToModel(model, skel), RestDrop = cache.RestDrop };
        skel.AddChild(rig);

        var proxy = new Proxy { SpeciesIndex = speciesIndex, Root = new Node3D(), Model = model, Player = player, Rig = rig };
        proxy.Root.AddChild(model);
        proxy.Body = proxy.Root;
        proxy.Head = proxy.Root;
        proxy.Tail = proxy.Root;
        proxy.Marker = NewMarker(sp, speciesIndex);
        proxy.Root.AddChild(proxy.Marker);
        return proxy;
    }

    private MeshInstance3D NewMarker(SpeciesDefinition sp, int speciesIndex)
    {
        SpeciesMeshes m = MeshesFor(speciesIndex, sp);
        return new MeshInstance3D
        {
            Mesh = MarkerMesh,
            MaterialOverride = m.MarkerMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
    }

    // ------------------------------------------------------------ 코드 메시

    private Proxy Build(int speciesIndex, SpeciesDefinition sp)
    {
        Proxy? withModel = BuildModel(speciesIndex, sp);
        if (withModel != null) return withModel;

        SpeciesMeshes m = MeshesFor(speciesIndex, sp);
        var proxy = new Proxy { SpeciesIndex = speciesIndex, Root = new Node3D() };

        proxy.Body = new Node3D();
        proxy.Root.AddChild(proxy.Body);
        Instance(proxy.Body, m.Torso);

        proxy.Head = new Node3D { Position = m.HeadPivot };
        proxy.Body.AddChild(proxy.Head);
        Instance(proxy.Head, m.Head);

        proxy.Tail = new Node3D { Position = m.TailPivot };
        proxy.Body.AddChild(proxy.Tail);
        Instance(proxy.Tail, m.Tail);

        for (int i = 0; i < 4; i++)
        {
            bool front = i < 2;
            var hip = new Node3D { Position = m.HipPositions[i] };
            proxy.Root.AddChild(hip);
            Instance(hip, front ? m.UpperFront : m.UpperHind);
            var knee = new Node3D { Position = new Vector3(0f, -(front ? m.UpperFrontLength : m.UpperHindLength), 0f) };
            hip.AddChild(knee);
            Instance(knee, front ? m.LowerFront : m.LowerHind);
            proxy.Hips[i] = hip;
            proxy.Knees[i] = knee;
        }

        proxy.Marker = NewMarker(sp, speciesIndex);
        proxy.Root.AddChild(proxy.Marker);
        return proxy;
    }

    private Proxy Acquire(int speciesIndex, SpeciesDefinition sp)
    {
        if (_pool.TryGetValue(speciesIndex, out Stack<Proxy>? stack) && stack.Count > 0)
        {
            Proxy reused = stack.Pop();
            reused.Root.Visible = true;
            return reused;
        }
        Proxy p = Build(speciesIndex, sp);
        AddChild(p.Root);
        return p;
    }

    private void Release(Proxy p)
    {
        p.Root.Visible = false;
        if (!_pool.TryGetValue(p.SpeciesIndex, out Stack<Proxy>? stack))
        {
            stack = new Stack<Proxy>();
            _pool[p.SpeciesIndex] = stack;
        }
        stack.Push(p);
    }

    /// <summary>
    /// 사체 노드를 만든다. 모델이 있으면 쓰러지는 동작을 재생하고(방금 죽었으면 처음부터, 아니면 마지막 자세로),
    /// 없으면 코드 메시 몸통을 옆으로 눕힌다.
    /// </summary>
    private CarcassView CreateCarcassView(in CarcassSnapshot k, SpeciesDefinition sp, TerrainGrid terrain, long tick)
    {
        float L = sp.BodyLengthM;
        // 물가에서 죽으면 바닥에 눕히면 거의 잠겨 보이지 않는다. 그런 사체는 수면 바로 아래에 떠 있게 둔다(화면 표현만).
        float ground = Mathf.Max(terrain.HeightAt(k.Position), terrain.WaterLevel - 0.25f);
        SpeciesModelCache cache = ModelFor(k.SpeciesIndex, sp);
        if (cache.Scene != null && cache.Animations.TryGetValue("death", out string? death))
        {
            Node3D body = cache.Scene.Instantiate<Node3D>();
            body.Scale = Vector3.One * cache.Scale;
            var holder = new Node3D
            {
                Position = new Vector3(k.Position.X, ground, k.Position.Z),
                Rotation = new Vector3(0f, Mathf.Pi * 0.5f - k.Heading, 0f),
            };
            holder.AddChild(body);
            AddChild(holder);
            AnimationPlayer? player = FindFirst<AnimationPlayer>(body);
            if (player != null)
            {
                player.Play(death);
                // 불러온 세계처럼 한참 전에 죽은 사체는 곧바로 쓰러진 자세로 둔다.
                if (tick - k.DiedTick > 30) player.Seek(player.GetAnimation(death).Length, true);
            }
            return new CarcassView { Node = holder, SinkDepth = L * 0.35f, Ground = ground };
        }

        SpeciesMeshes m = MeshesFor(k.SpeciesIndex, sp);
        var node = new MeshInstance3D
        {
            Mesh = m.Torso,
            MaterialOverride = SkinMaterial,
            // 옆으로 쓰러진 몸통.
            Rotation = new Vector3(0f, -k.Heading - Mathf.Pi * 0.5f, Mathf.DegToRad(82f)),
            Position = new Vector3(k.Position.X, ground + 0.10f * L, k.Position.Z),
        };
        AddChild(node);
        return new CarcassView { Node = node, SinkDepth = L * 0.3f, Ground = ground + 0.10f * L };
    }

    /// <summary>사체를 시뮬레이션 상태에 맞춘다. 분해된 만큼 땅으로 가라앉고, 다 분해되면 없어진다.</summary>
    private void UpdateCarcasses(SimSnapshot snapshot, SpeciesRegistry species, TerrainGrid terrain)
    {
        foreach (CarcassView v in _carcasses.Values) v.Seen = false;
        for (int i = 0; i < snapshot.CarcassCount; i++)
        {
            CarcassSnapshot k = snapshot.Carcasses[i];
            if (!_carcasses.TryGetValue(k.Id, out CarcassView? view))
            {
                view = CreateCarcassView(k, species[k.SpeciesIndex], terrain, snapshot.Tick);
                _carcasses[k.Id] = view;
            }
            view.Seen = true;
            float sink = (1f - Mathf.Clamp(k.Remaining, 0f, 1f)) * view.SinkDepth;
            Vector3 p = view.Node.Position;
            view.Node.Position = new Vector3(p.X, view.Ground - sink, p.Z);
        }
        _stale.Clear();
        foreach (KeyValuePair<int, CarcassView> kv in _carcasses)
            if (!kv.Value.Seen) _stale.Add(kv.Key);
        foreach (int id in _stale)
        {
            _carcasses[id].Node.QueueFree();
            _carcasses.Remove(id);
        }
    }

    public int CarcassViewCount => _carcasses.Count;

    // ------------------------------------------------------------ 프레임

    /// <summary>스냅샷을 화면에 반영한다. alpha는 이전 틱과 현재 틱 사이의 보간 계수.</summary>
    public void UpdateFrame(SimSnapshot snapshot, SpeciesRegistry species, TerrainGrid terrain, float alpha, double delta, Vector3 cameraPosition)
    {
        foreach (Proxy p in _proxies.Values) p.Seen = false;
        SelectedPosition = null;
        float dt = (float)delta;

        for (int i = 0; i < snapshot.Count; i++)
        {
            CreatureSnapshot c = snapshot.Creatures[i];
            SpeciesDefinition sp = species[c.SpeciesIndex];
            if (!_proxies.TryGetValue(c.Id, out Proxy? p))
            {
                p = Acquire(c.SpeciesIndex, sp);
                p.Phase = (c.Id * 0.618f) % 1f * Mathf.Tau;
                _proxies[c.Id] = p;
            }
            p.Seen = true;

            float x = Mathf.Lerp(c.PrevPosition.X, c.Position.X, alpha);
            float z = Mathf.Lerp(c.PrevPosition.Z, c.Position.Z, alpha);
            float heading = Mathf.LerpAngle(c.PrevHeading, c.Heading, alpha);
            float y = terrain.HeightAt(new Vec2(x, z));
            var position = new Vector3(x, y, z);
            float L = sp.BodyLengthM;
            float cameraDistance = position.DistanceTo(cameraPosition);
            p.Heading = heading;
            UpdateMarker(p, cameraDistance, L, c.BodySize);

            if (p.Model != null)
            {
                UpdateModel(p, c, position, heading, dt, cameraDistance);
            }
            else
            {
                UpdateCode(p, c, sp, position, heading, dt, cameraDistance);
            }

            if (c.Id == SelectedId)
            {
                SelectedPosition = position;
                float r = L * 0.62f * c.BodySize;
                _selectionRing.Visible = true;
                _selectionRing.Position = new Vector3(x, Mathf.Max(y, terrain.WaterLevel) + 0.25f, z);
                _selectionRing.Scale = new Vector3(r, r, r);
            }
        }

        FinishFrame(delta);
        UpdateCarcasses(snapshot, species, terrain);
    }

    private static void UpdateMarker(Proxy p, float cameraDistance, float L, float bodySize)
    {
        p.Marker.Visible = cameraDistance > MarkerDistance;
        if (!p.Marker.Visible) return;
        // 화면에서 늘 비슷한 크기로 보이도록 거리에 비례해 키운다. 모델은 따로 크기를 줄였으므로 Root 기준으로 둔다.
        float markerSize = cameraDistance * 0.011f / bodySize;
        p.Marker.Scale = new Vector3(markerSize, markerSize, markerSize);
        p.Marker.Position = new Vector3(0f, 0.55f * L + markerSize, 0f);
    }

    /// <summary>리깅 모델: 동작에 맞는 애니메이션을 고르고, 걷기·달리기는 이동 속도에 맞춰 재생 속도를 바꾼다.</summary>
    private void UpdateModel(Proxy p, CreatureSnapshot c, Vector3 position, float heading, float dt, float cameraDistance)
    {
        SpeciesModelCache cache = _models[p.SpeciesIndex];
        AnimationPlayer player = p.Player!;
        p.Root.Position = position;
        // 모델의 앞은 +Z다.
        p.Root.Rotation = new Vector3(0f, Mathf.Pi * 0.5f - heading, 0f);
        p.Root.Scale = Vector3.One * c.BodySize;

        bool moving = c.Speed > 0.05f && (c.Activity == ActivityKind.Walk || c.Activity == ActivityKind.Run);
        string key = "idle";
        float speedScale = 1f;
        if (moving)
        {
            // 너무 느린 달리기는 걷기로 보인다.
            bool run = c.Activity == ActivityKind.Run && c.Speed > cache.WalkSpeed * 1.4f;
            key = run ? "run" : "walk";
            float natural = (run ? cache.RunSpeed : cache.WalkSpeed) * c.BodySize;
            speedScale = Mathf.Clamp(c.Speed / Mathf.Max(natural, 0.01f), 0.3f, 2.5f);
        }
        else if (c.Activity == ActivityKind.Rest)
        {
            speedScale = 0.35f;
        }
        else if (c.Activity == ActivityKind.Attack && cache.Animations.ContainsKey("attack"))
        {
            key = "attack";
        }

        string anim = cache.Animations[key];
        if (anim != p.CurrentAnimation)
        {
            bool first = p.CurrentAnimation.Length == 0;
            player.Play(anim, first ? 0.0 : 0.3);
            if (first)
            {
                // 개체마다 시작 위치를 달리해 모두가 똑같이 움직이지 않게 한다.
                player.Seek(player.GetAnimation(anim).Length * ((c.Id * 0.618f) % 1f), true);
            }
            p.CurrentAnimation = anim;
        }
        player.SpeedScale = speedScale;

        float targetGraze = c.Activity is ActivityKind.Eat or ActivityKind.Drink ? 1f : 0f;
        float targetRest = c.Activity == ActivityKind.Rest ? 1f : 0f;
        float blend = Mathf.Min(1f, dt * 3f);
        p.Graze = Mathf.Lerp(p.Graze, targetGraze, blend);
        p.RestAmount = Mathf.Lerp(p.RestAmount, targetRest, Mathf.Min(1f, dt * 1.5f));
        p.Rig!.Graze = p.Graze;
        p.Rig.Rest = p.RestAmount;

        // 멀리 있으면 애니메이션 계산을 멈춰 비용을 줄인다(자세는 그대로 남는다).
        bool animate = cameraDistance < DetailDistance;
        if (player.Active != animate) player.Active = animate;
    }

    /// <summary>코드 메시: 관절 노드를 절차적으로 움직인다.</summary>
    private void UpdateCode(Proxy p, CreatureSnapshot c, SpeciesDefinition sp, Vector3 position, float heading, float dt, float cameraDistance)
    {
            SpeciesMeshes m = _meshes[c.SpeciesIndex];
            float L = sp.BodyLengthM;

            // 걸음: 한 걸음 길이에 맞춰 위상을 돌린다. 발이 미끄러져 보이지 않게 속도에 비례시킨다.
            bool moving = c.Speed > 0.05f && (c.Activity == ActivityKind.Walk || c.Activity == ActivityKind.Run);
            bool running = c.Activity == ActivityKind.Run;
            float stride = L * 0.42f;
            if (moving) p.Phase += c.Speed / stride * Mathf.Tau * dt;
            else p.Phase += dt * 0.9f;
            float swing = moving ? (running ? 0.62f : 0.36f) : 0f;
            float bob = moving ? Mathf.Abs(Mathf.Sin(p.Phase)) * 0.010f * L * (running ? 2.2f : 1f) : Mathf.Sin(p.Phase) * 0.003f * L;

            float targetCrouch = c.Activity == ActivityKind.Rest ? 1f : 0f;
            float targetPitch = c.Activity switch
            {
                ActivityKind.Eat => 0.62f + 0.10f * Mathf.Sin(p.Phase * 3f),
                ActivityKind.Drink => 0.72f,
                ActivityKind.Rest => 0.30f,
                ActivityKind.Run => -0.10f,
                ActivityKind.Attack => 0.35f * Mathf.Sin(p.Phase * 6f),
                _ => 0.04f + (moving ? 0.03f * Mathf.Sin(p.Phase * 2f) : 0f),
            };
            float blend = Mathf.Min(1f, dt * 5f);
            p.Crouch = Mathf.Lerp(p.Crouch, targetCrouch, blend);
            p.HeadPitch = Mathf.Lerp(p.HeadPitch, targetPitch, blend);

            p.Root.Position = position + new Vector3(0f, bob - p.Crouch * m.BellyHeight * 0.92f, 0f);
            p.Root.Rotation = new Vector3(0f, -heading - Mathf.Pi * 0.5f, 0f);
            p.Root.Scale = Vector3.One * c.BodySize;
            // 걸을 때 몸통이 좌우로 조금 흔들린다.
            p.Body.Rotation = new Vector3(0f, 0f, moving ? Mathf.Sin(p.Phase) * 0.025f : 0f);
            p.Head.Rotation = new Vector3(-p.HeadPitch, moving ? Mathf.Sin(p.Phase) * 0.04f : 0f, 0f);
            p.Tail.Rotation = new Vector3(0f, Mathf.Sin(p.Phase * (moving ? 1f : 0.5f)) * (moving ? 0.20f : 0.07f), 0f);

            bool legsVisible = cameraDistance < DetailDistance && p.Crouch < 0.6f;
            for (int l = 0; l < 4; l++)
            {
                Node3D hip = p.Hips[l];
                hip.Visible = legsVisible;
                if (!legsVisible) continue;
                // 대각선 다리끼리 같은 위상. 다리가 앞으로 나갈 때 무릎을 굽혀 발을 든다.
                float legPhase = p.Phase + ((l == 0 || l == 3) ? 0f : Mathf.Pi);
                float lift = moving ? Mathf.Max(0f, Mathf.Cos(legPhase)) : 0f;
                hip.Rotation = new Vector3(Mathf.Sin(legPhase) * swing + lift * swing * 0.35f, 0f, 0f);
                p.Knees[l].Rotation = new Vector3(-lift * swing * 1.7f, 0f, 0f);
            }
    }

    private void FinishFrame(double delta)
    {
        _stale.Clear();
        foreach (KeyValuePair<int, Proxy> kv in _proxies)
            if (!kv.Value.Seen) _stale.Add(kv.Key);
        foreach (int id in _stale)
        {
            Release(_proxies[id]);
            _proxies.Remove(id);
        }
        if (SelectedPosition == null) _selectionRing.Visible = false;
    }
}
