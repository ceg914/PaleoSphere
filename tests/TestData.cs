using System;
using System.IO;
using System.Linq;
using System.Reflection;
using PaleoSphere.Core;
using PaleoSphere.Modding;
using PaleoSphere.Simulation;
using PaleoSphere.World;

namespace PaleoSphere.Tests;

/// <summary>테스트 공용 도우미: 저장소의 data/를 읽고 작은 시험 세계를 만든다.</summary>
internal static class TestData
{
    public static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        string? fromBuild = typeof(TestData).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepoRoot")?.Value;
        if (!string.IsNullOrEmpty(fromBuild) && File.Exists(Path.Combine(fromBuild, "project.godot"))) return fromBuild;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PaleoSphere.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("저장소 루트를 찾지 못했습니다.");
    }

    public static string DataDir => Path.Combine(RepoRoot, "data");
    public static string FixturesDir => Path.Combine(RepoRoot, "tests", "SaveCompatibility", "fixtures");

    private static readonly Lazy<GameData> Shared = new(() => GameData.LoadFromDirectory(DataDir));

    public static GameData Data => Shared.Value;

    public const string Herbivore = "triceratops_horridus";
    public const string Predator = "tyrannosaurus_rex";

    /// <summary>128 m 정사각 세계. 테스트가 빨리 끝나도록 작게 잡는다.</summary>
    public static WorldConfig SmallConfig(ulong seed = 7UL) => new()
    {
        Seed = seed,
        SizeCells = 64,
        CellSize = 2f,
        TicksPerDay = 6000,
    };

    public static SimulationCore NewSim(ulong seed = 7UL, int creatures = 0, WorldConfig? config = null)
    {
        SimulationCore sim = Data.CreateSimulation(config ?? SmallConfig(seed));
        if (creatures > 0) Spawn(sim, creatures);
        return sim;
    }

    public static void Spawn(SimulationCore sim, int count)
    {
        float size = sim.World.Terrain.WorldSize;
        sim.Enqueue(new SpawnCreaturesCommand(Herbivore, count, new Vec2(size / 2, size / 2), size * 0.45f));
        sim.ApplyPendingCommands();
    }

    public static void Run(SimulationCore sim, int ticks)
    {
        for (int i = 0; i < ticks; i++) sim.Step();
    }

    public static ScenarioDefinition Scenario(string id) =>
        Data.Scenarios.First(s => s.Id == id);
}
