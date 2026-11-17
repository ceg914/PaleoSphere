using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using PaleoSphere.Core;
using PaleoSphere.Creatures;
using PaleoSphere.Diagnostics;
using PaleoSphere.Ecology;
using PaleoSphere.Modding;
using PaleoSphere.Simulation;

namespace PaleoSphere.Tools;

/// <summary>
/// 사용법: dotnet run --project tools/SimRunner -c Release -- [--data data] [--scenario id] [--days N]
///         [--count N] [--seed N] [--size cells] [--csv out.csv] [--save out.psave] [--quiet]
///         [--trace-deaths] [--trace-creature id] [--trace-every ticks] [--mem-every days]
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        string dataDir = "data";
        string scenarioId = "lush_lowland";
        double days = 10;
        int countOverride = -1;
        ulong? seed = null;
        string? csv = null;
        bool quiet = false;
        int memEveryDays = 0;
        string? savePath = null;
        int sizeOverride = -1;
        bool traceDeaths = false;
        int traceCreature = -1;
        int traceEvery = 500;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data": dataDir = args[++i]; break;
                case "--scenario": scenarioId = args[++i]; break;
                case "--days": days = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--count": countOverride = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed": seed = ulong.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--csv": csv = args[++i]; break;
                case "--save": savePath = args[++i]; break;
                case "--size": sizeOverride = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--mem-every": memEveryDays = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--quiet": quiet = true; break;
                case "--trace-deaths": traceDeaths = true; break;
                case "--trace-creature": traceCreature = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--trace-every": traceEvery = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default:
                    Console.Error.WriteLine("알 수 없는 인자: " + args[i]);
                    return 2;
            }
        }

        GameData data = GameData.LoadFromDirectory(dataDir);
        foreach (string w in data.Report.Warnings) Console.Error.WriteLine("경고 " + w);
        foreach (string e in data.Report.Errors) Console.Error.WriteLine("오류 " + e);
        if (!data.IsUsable) return 1;

        ScenarioDefinition? scenario = null;
        foreach (ScenarioDefinition s in data.Scenarios)
            if (s.Id == scenarioId) scenario = s;
        if (scenario == null)
        {
            Console.Error.WriteLine($"시나리오 '{scenarioId}'이(가) 없습니다.");
            return 1;
        }

        var config = scenario.World.Clone();
        if (seed.HasValue) config.Seed = seed.Value;
        if (sizeOverride > 0) config.SizeCells = sizeOverride;
        var sw = Stopwatch.StartNew();
        SimulationCore sim = data.CreateSimulation(config);
        double genMs = sw.Elapsed.TotalMilliseconds;

        float size = sim.World.Terrain.WorldSize;
        var center = new Vec2(size / 2, size / 2);
        for (int pi = 0; pi < scenario.Populations.Count; pi++)
        {
            ScenarioPopulation p = scenario.Populations[pi];
            int count = pi == 0 && countOverride > 0 ? Math.Min(countOverride, 500) : p.Count;
            sim.Enqueue(new SpawnCreaturesCommand(p.SpeciesId, count, center, size * 0.45f));
        }
        for (int extra = countOverride - 500; extra > 0; extra -= 500)
            sim.Enqueue(new SpawnCreaturesCommand(scenario.Populations[0].SpeciesId, Math.Min(extra, 500), center, size * 0.45f));
        sim.ApplyPendingCommands();

        if (traceDeaths)
        {
            sim.Events.Subscribe(e =>
            {
                if (e.Type != SimEventType.CreatureDied) return;
                var t = sim.World.Terrain;
                var v = sim.World.Vegetation;
                int cell = t.CellIndexAt(e.Position);
                int n = t.Size, cx = cell % n, cz = cell / n, r = (int)(40f / t.CellSize);
                double b = 0, k = 0, edible = 0;
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int x = cx + dx, z = cz + dz;
                        if (x < 0 || z < 0 || x >= n || z >= n) continue;
                        b += v.Biomass[z * n + x];
                        k += v.Capacity[z * n + x];
                        edible += v.EdibleAt(z * n + x, sim.World.Forage);
                    }
                Console.WriteLine($"  사망 #{e.CreatureId} {e.Detail} Day {e.Day} {e.Hour:0.0}h 위치 {e.Position} 생물군계 {sim.World.Biomes[t.Biome[cell]].Id} 물까지 {t.WaterPathDistance[cell]:0} m | 반경 40 m 식생 {b:0}/{k:0} kg (먹을 수 있는 양 {edible:0} kg)");
            });
        }

        var terrain = sim.World.Terrain;
        int water = 0, walkable = 0;
        for (int i = 0; i < terrain.CellCount; i++)
        {
            if (terrain.IsWater(i)) water++;
            if (terrain.Walkable[i]) walkable++;
        }
        Console.WriteLine($"세계 {size:0} m, 시드 {config.Seed}, 생성 {genMs:0} ms, 수면 {100.0 * water / terrain.CellCount:0.0}%, 보행 가능 {100.0 * walkable / terrain.CellCount:0.0}%");
        Console.WriteLine($"식생 수용량 {sim.World.Vegetation.SumCapacity() / 1000:0.0} t, 초기 {sim.World.Vegetation.SumBiomass() / 1000:0.0} t, 개체 {sim.CreatureCount}");

        long totalTicks = (long)(days * config.TicksPerDay);
        int reportEvery = config.TicksPerDay / 2;
        sw.Restart();
        for (long t = 0; t < totalTicks; t++)
        {
            sim.Step();
            if (!quiet && sim.Clock.Tick % reportEvery == 0) PrintDay(sim);
            if (memEveryDays > 0 && sim.Clock.Tick % ((long)memEveryDays * config.TicksPerDay) == 0)
                Console.WriteLine($"  메모리 Day {sim.Clock.Day - 1,5}: 관리 힙 {GC.GetTotalMemory(true) / 1048576.0:0.0} MiB, 작업 집합 {Environment.WorkingSet / 1048576.0:0.0} MiB, 통계 표본 {sim.Stats.Samples.Count}, 개체 {sim.CreatureCount}");
            if (traceCreature > 0 && sim.Clock.Tick % traceEvery == 0 && sim.TryGetCreature(traceCreature, out Creature? tc) && tc != null)
            {
                var tt = sim.World.Terrain;
                int cell = tt.CellIndexAt(tc.Position);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  #{0} Day {1} {2:0.0}h pos ({3:0.000}, {4:0.000}) {5}/{6} target {7}{8} stuck {9} E {10:0.00} H {11:0.00} F {12:0.00} cell[{13}] walk {14} water {15} depth {16:0.00} wdist {17:0.0} speed {18:0.00}",
                    tc.Id, sim.Clock.Day, sim.Clock.TimeOfDayHours, tc.Position.X, tc.Position.Z, tc.Behavior, tc.Activity,
                    tc.HasTarget ? "→" : "x", tc.Target, tc.StuckTicks, tc.Energy, tc.Hydration, tc.Fatigue,
                    sim.World.Biomes[tt.Biome[cell]].Id, tt.Walkable[cell], tt.IsWater(cell), tt.WaterDepth(cell), tt.WaterPathDistance[cell], tc.CurrentSpeed));
            }
        }
        double runMs = sw.Elapsed.TotalMilliseconds;

        PopulationStats st = sim.Stats;
        Console.WriteLine($"--- {days:0.##}일({totalTicks}틱) 실행: {runMs:0} ms, 틱당 평균 {runMs / totalTicks * 1000:0.0} µs, 최대 {sim.Profiler.MaxTickMs:0.00} ms");
        Console.WriteLine($"    구간 평균(ms): 식생 {sim.Profiler.SectionAverageMs(TickProfiler.Section.Vegetation):0.000}(갱신 틱만), 의사결정·생리 {sim.Profiler.SectionAverageMs(TickProfiler.Section.Brain):0.000}, 이동 {sim.Profiler.SectionAverageMs(TickProfiler.Section.Movement):0.000}");
        Console.WriteLine($"    생존 {sim.CreatureCount}, 아사 {st.TotalDeathsByCause(DeathCause.Starvation)}, 탈수 {st.TotalDeathsByCause(DeathCause.Dehydration)}, 포식 {st.TotalDeathsByCause(DeathCause.Predation)}");
        for (int s = 0; s < sim.World.Species.Count; s++)
        {
            int alive = 0, kills = 0;
            foreach (Creature c in sim.Creatures) if (c.SpeciesIndex == s) { alive++; kills += c.Kills; }
            Console.WriteLine($"    {sim.World.Species[s].Id}: 배치 {st.Spawned[s]}, 생존 {alive}, 아사 {st.DeathsOf(s, DeathCause.Starvation)}, 탈수 {st.DeathsOf(s, DeathCause.Dehydration)}, 포식당함 {st.DeathsOf(s, DeathCause.Predation)}, 사냥 성공(생존 개체 합) {kills}");
        }
        var world = sim.World;
        Console.WriteLine($"    사체 질량 장부: 생김 {world.CarcassMassCreated / 1000:0.0} t = 먹힘 {world.CarcassMassEaten / 1000:0.0} + 분해 {world.CarcassMassDecomposed / 1000:0.0} + 남음 {SumRemains(sim) / 1000:0.0} t | 토양 영양 더함 {veg2(sim).TotalNutrientAdded:0} 씀 {veg2(sim).TotalNutrientUsed:0} 씻김 {veg2(sim).TotalNutrientLeached:0} 남음 {veg2(sim).SumNutrient():0} kg");
        var veg = sim.World.Vegetation;
        double expected = veg.InitialTotal + veg.TotalGrown - veg.TotalConsumed - veg.TotalDieback;
        Console.WriteLine($"    식생 {veg.SumBiomass() / 1000:0.00} t (성장 {veg.TotalGrown / 1000:0.00}, 섭식 {veg.TotalConsumed / 1000:0.00}, 고사 {veg.TotalDieback / 1000:0.00}; 수지 오차 {Math.Abs(expected - veg.SumBiomass()):0.000} kg)");
        Console.WriteLine($"    상태 해시 {StateHasher.ToHex(sim.ComputeStateHash())}");

        if (savePath != null)
        {
            File.WriteAllBytes(savePath, PaleoSphere.Persistence.SaveSerializer.Serialize(sim, Path.GetFileNameWithoutExtension(savePath)));
            Console.WriteLine("세이브 저장: " + savePath);
        }

        if (csv != null)
        {
            using var w = new StreamWriter(csv);
            w.WriteLine("tick,day,creatures,vegetation_kg,mean_energy,mean_hydration,total_deaths");
            foreach (StatSample s in st.Samples)
            {
                int n = 0;
                foreach (int c in s.Counts) n += c;
                w.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:0.###},{2},{3:0.#},{4:0.###},{5:0.###},{6}",
                    s.Tick, s.Day, n, s.VegetationKg, s.MeanEnergy, s.MeanHydration, s.TotalDeaths));
            }
            Console.WriteLine("CSV 저장: " + csv);
        }
        return 0;
    }

    private static double SumRemains(SimulationCore sim)
    {
        double t = 0;
        foreach (PaleoSphere.Ecology.Carcass k in sim.World.Carcasses) t += k.RemainsKg;
        return t;
    }

    private static PaleoSphere.Ecology.VegetationGrid veg2(SimulationCore sim) => sim.World.Vegetation;

    private static void PrintDay(SimulationCore sim)
    {
        var counts = new int[BehaviorKinds.Count];
        var perSpecies = new int[sim.World.Species.Count];
        double e = 0, h = 0, f = 0;
        foreach (Creature c in sim.Creatures)
        {
            counts[(int)c.Behavior]++;
            perSpecies[c.SpeciesIndex]++;
            e += c.Energy;
            h += c.Hydration;
            f += c.Fatigue;
        }
        int n = Math.Max(1, sim.CreatureCount);
        var species = new System.Text.StringBuilder();
        for (int s = 0; s < perSpecies.Length; s++)
            species.Append(sim.World.Species[s].Id.Split('_')[0]).Append(' ').Append(perSpecies[s]).Append(' ');
        Console.WriteLine($"Day {sim.Clock.Day,3} {sim.Clock.TimeOfDayHours,4:0.0}h {species}| 에너지 {e / n:0.00} 수분 {h / n:0.00} 피로 {f / n:0.00} | 식생 {sim.World.Vegetation.LastTotal / 1000,6:0.0} t | 사체 {sim.World.Carcasses.Count} 고기 {sim.CarcassMeatKg() / 1000:0.0} t 영양 {sim.World.Vegetation.SumNutrient():0} kg | 섭식 {counts[1]} 음수 {counts[2]} 휴식 {counts[3]} 도주 {counts[4]} 탐색 {counts[5]} 사냥 {counts[6]}");
    }
}
