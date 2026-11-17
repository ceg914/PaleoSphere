using System.Collections.Generic;
using PaleoSphere.Core;
using PaleoSphere.Creatures;
using PaleoSphere.Ecology;
using PaleoSphere.World;

namespace PaleoSphere.Simulation;

/// <summary>위협 자극. 반경 안의 개체가 공포를 느끼고 달아난다. 포식자 구현 전에는 사용자 교란 도구가 만든다.</summary>
public struct ThreatSource
{
    public Vec2 Position;
    public float Radius;
    public float Intensity;
    public long UntilTick;
}

/// <summary>세계의 단일 권위 상태: 설정, 시계, 지형, 식생, 환경, 정의 데이터.</summary>
public sealed class WorldState
{
    public WorldConfig Config { get; }
    public SimClock Clock { get; }
    public TerrainGrid Terrain { get; }
    public VegetationGrid Vegetation { get; }
    public BiomeRegistry Biomes { get; }
    public SpeciesRegistry Species { get; }
    public PlantDefinition Forage { get; }
    public EnvironmentState Env;
    public List<ThreatSource> Threats { get; } = new();

    /// <summary>사체. 죽은 순서(= 사건 순서)대로 쌓인다.</summary>
    public List<Carcass> Carcasses { get; } = new();

    /// <summary>사체 질량 장부(kg): 생긴 양 = 먹힌 양 + 분해된 양 + 남은 양.</summary>
    public double CarcassMassCreated;
    public double CarcassMassEaten;
    public double CarcassMassDecomposed;

    /// <summary>이번 틱에 사냥으로 죽은 개체가 있는지. 이미 처리된 개체가 나중에 죽어도 같은 틱에 사체로 바꾸기 위함.</summary>
    internal bool DeathsPending;

    /// <summary>살아 있는 개체(id 순). SimulationCore가 연결한다. AI가 사냥감을 찾을 때 읽는다.</summary>
    public IReadOnlyList<Creature> CreatureList { get; internal set; } = new List<Creature>();
    public IReadOnlyDictionary<int, Creature> CreaturesById { get; internal set; } = new Dictionary<int, Creature>();

    public Carcass? FindCarcass(int id)
    {
        for (int i = 0; i < Carcasses.Count; i++)
            if (Carcasses[i].Id == id) return Carcasses[i];
        return null;
    }

    public WorldState(WorldConfig config, SimClock clock, TerrainGrid terrain, VegetationGrid vegetation,
        BiomeRegistry biomes, SpeciesRegistry species, PlantDefinition forage)
    {
        Config = config;
        Clock = clock;
        Terrain = terrain;
        Vegetation = vegetation;
        Biomes = biomes;
        Species = species;
        Forage = forage;
        Env = EnvironmentState.Compute(clock, config);
    }
}
