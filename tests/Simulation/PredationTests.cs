using System;
using System.Linq;
using PaleoSphere.AI;
using PaleoSphere.Core;
using PaleoSphere.Creatures;
using PaleoSphere.Ecology;
using PaleoSphere.Persistence;
using PaleoSphere.Simulation;
using PaleoSphere.World;
using Xunit;

namespace PaleoSphere.Tests.Simulation;

/// <summary>Phase 2: 사냥, 도주, 사체, 분해와 토양 영양, 포식자 유입 시나리오.</summary>
public class PredationTests
{
    /// <summary>낮 시간의 512 m 세계(물·숲이 섞인 기본 시드).</summary>
    private static SimulationCore DaySim(ulong seed = 20261116) =>
        TestData.NewSim(config: new WorldConfig { Seed = seed, StartHour = 10f });

    /// <summary>반지름 안이 모두 마른 땅인 탁 트인 곳의 중심을 찾는다.</summary>
    private static Vec2 OpenLand(SimulationCore sim, float radius)
    {
        TerrainGrid t = sim.World.Terrain;
        float size = t.WorldSize;
        for (float z = radius + 10; z < size - radius - 10; z += 8)
        {
            for (float x = radius + 10; x < size - radius - 10; x += 8)
            {
                bool ok = true;
                for (float dz = -radius; dz <= radius && ok; dz += 2)
                    for (float dx = -radius; dx <= radius && ok; dx += 2)
                        ok = t.IsDryLandAt(new Vec2(x + dx, z + dz));
                if (ok) return new Vec2(x, z);
            }
        }
        throw new InvalidOperationException("탁 트인 땅을 찾지 못했습니다.");
    }

    private static Creature Place(SimulationCore sim, string species, Vec2 at)
    {
        int before = sim.NextCreatureId;
        sim.Enqueue(new SpawnCreaturesCommand(species, 1, at, 0.5f));
        sim.ApplyPendingCommands();
        Assert.True(sim.TryGetCreature(before, out Creature? c), "배치 실패: " + species);
        return c!;
    }

    private static int SpeciesIndex(SimulationCore sim, string id)
    {
        Assert.True(sim.World.Species.TryGetIndex(id, out int i));
        return i;
    }

    /// <summary>배고픈 포식자와 약한 사냥감을 30 m 떨어뜨려 둔다.</summary>
    private static (SimulationCore Sim, Creature Rex, Creature Prey) HuntSetup(float preyHealth = 0.3f)
    {
        SimulationCore sim = DaySim();
        Vec2 c = OpenLand(sim, 60f);
        Creature prey = Place(sim, TestData.Herbivore, c);
        Creature rex = Place(sim, TestData.Predator, c + new Vec2(30f, 0f));
        rex.Energy = 0.3f;
        rex.Fatigue = 0f;
        prey.Health = preyHealth;
        prey.Energy = 0.9f;
        prey.Hydration = 0.9f;
        prey.Fatigue = 0.8f;
        return (sim, rex, prey);
    }

    [Fact]
    public void HungryPredator_HuntsKillsAndLeavesCarcass()
    {
        (SimulationCore sim, Creature rex, Creature prey) = HuntSetup();
        int preyId = prey.Id;
        bool sawHunt = false, sawAttack = false;
        for (int i = 0; i < 6000 && prey.Alive; i++)
        {
            sim.Step();
            sawHunt |= rex.Behavior == BehaviorKind.Hunt && rex.TargetEntityId == preyId;
            sawAttack |= rex.Activity == ActivityKind.Attack;
        }
        Assert.True(sawHunt, "포식자가 사냥감을 겨누지 않았습니다.");
        Assert.True(sawAttack, "공격 동작이 없었습니다.");
        Assert.False(prey.Alive);
        Assert.Equal(DeathCause.Predation, prey.DeathCause);
        Assert.Equal(rex.Id, prey.KilledBy);
        Assert.Equal(1, rex.Kills);
        Assert.False(sim.TryGetCreature(preyId, out _));
        Assert.Equal(1, sim.Stats.DeathsOf(SpeciesIndex(sim, TestData.Herbivore), DeathCause.Predation));

        Carcass carcass = Assert.Single(sim.World.Carcasses);
        SpeciesDefinition sp = sim.World.Species[prey.SpeciesIndex];
        float mass = sp.BodyMassKg * MathF.Pow(prey.TraitBodySize, 3);
        Assert.Equal(preyId, carcass.Id);
        Assert.Equal(DeathCause.Predation, carcass.Cause);
        Assert.Equal(mass, carcass.InitialMassKg, 1);
        Assert.Equal(mass * CarcassRules.EdibleFraction, carcass.InitialMeatKg, 1);

        SimEvent kill = sim.Events.Recent.Last(e => e.Type == SimEventType.Predation);
        Assert.Equal(rex.Id, kill.CreatureId);
        Assert.Equal(TestData.Predator, kill.SpeciesId);
        Assert.StartsWith(preyId + "|", kill.Detail);
        Assert.Contains(rex.Log, l => l.Behavior == BehaviorKind.Hunt && l.Result == BehaviorResult.Kill);
    }

    [Fact]
    public void Predator_EatsCarcass_AndMassLedgerBalances()
    {
        (SimulationCore sim, Creature rex, Creature prey) = HuntSetup();
        for (int i = 0; i < 6000 && prey.Alive; i++) sim.Step();
        Assert.False(prey.Alive);
        float energyAtKill = rex.Energy;
        Carcass carcass = sim.World.Carcasses.Single();
        float meatAtKill = carcass.MeatKg;

        bool ate = false;
        for (int i = 0; i < 3000; i++)
        {
            sim.Step();
            ate |= rex.Activity == ActivityKind.Eat;
        }
        Assert.True(ate, "포식자가 사체를 먹지 않았습니다.");
        Assert.True(rex.Energy > energyAtKill + 0.2f, $"에너지가 충분히 오르지 않았습니다: {energyAtKill:0.00} → {rex.Energy:0.00}");
        Assert.True(carcass.MeatKg < meatAtKill);
        Assert.True(sim.World.CarcassMassEaten > 100);

        WorldState w = sim.World;
        double remains = w.Carcasses.Sum(k => (double)k.RemainsKg);
        Assert.Equal(w.CarcassMassCreated, w.CarcassMassEaten + w.CarcassMassDecomposed + remains, 0);
        Assert.All(w.Carcasses, k => Assert.InRange(k.MeatKg, 0f, k.RemainsKg));
    }

    [Fact]
    public void Prey_FleesFromHuntingPredator()
    {
        (SimulationCore sim, Creature rex, Creature prey) = HuntSetup(preyHealth: 1f);
        prey.Fatigue = 0.1f;
        bool fled = false;
        for (int i = 0; i < 1200 && prey.Alive && !fled; i++)
        {
            sim.Step();
            fled = prey.Behavior == BehaviorKind.Flee;
        }
        Assert.True(fled, "사냥감이 달아나지 않았습니다.");
        Assert.True(prey.Fear >= 0.55f);
        Assert.True(Vec2.Distance(prey.ThreatPosition, rex.Position) < 20f, "공포의 원인 위치가 포식자가 아닙니다.");
        Assert.Contains(prey.Log, l => l.Behavior == BehaviorKind.Flee);
    }

    [Fact]
    public void SatedPredator_DoesNotHunt()
    {
        (SimulationCore sim, Creature rex, Creature prey) = HuntSetup(preyHealth: 1f);
        rex.Energy = 0.97f;
        for (int i = 0; i < 1500; i++)
        {
            sim.Step();
            Assert.NotEqual(BehaviorKind.Hunt, rex.Behavior);
        }
        Assert.True(prey.Alive);
    }

    [Fact]
    public void Herbivores_NeverHunt()
    {
        SimulationCore sim = TestData.NewSim(creatures: 10);
        Creature c = sim.Creatures[0];
        c.Energy = 0.05f;
        for (int i = 0; i < 2000; i++)
        {
            sim.Step();
            Assert.All(sim.Creatures, x => Assert.NotEqual(BehaviorKind.Hunt, x.Behavior));
        }
        Assert.All(sim.Creatures, x => Assert.Equal(0f, x.LastScores[(int)BehaviorKind.Hunt]));
    }

    [Fact]
    public void PredatorWithoutPrey_Starves_AndLeavesCarcass()
    {
        SimulationCore sim = DaySim();
        Creature rex = Place(sim, TestData.Predator, OpenLand(sim, 20f));
        rex.Energy = 0.2f;
        SpeciesDefinition sp = sim.World.Species[rex.SpeciesIndex];
        int days = (int)MathF.Ceiling(0.2f * sp.EnergyReserveDays + sp.StarveHealthDays) + 3;
        for (int i = 0; i < sim.World.Config.TicksPerDay * days && rex.Alive; i++) sim.Step();
        Assert.False(rex.Alive);
        Assert.Equal(DeathCause.Starvation, rex.DeathCause);
        Assert.Contains(rex.Log, l => l.Behavior == BehaviorKind.Hunt && l.Result == BehaviorResult.NoPreyFound);
        Carcass k = Assert.Single(sim.World.Carcasses);
        // 굶어 죽은 몸에는 먹을 살이 적다.
        Assert.True(k.InitialMeatKg < k.InitialMassKg * CarcassRules.EdibleFraction);
    }

    [Fact]
    public void Carcass_Decomposes_IntoSoilNutrient_ThenDisappears()
    {
        // 먹을 식생이 없는 세계에서 굶겨 사체를 만든다.
        SimulationCore sim = TestData.NewSim(config: new WorldConfig { Seed = 20261116, StartHour = 10f, VegetationCapacityScale = 0f });
        Creature c = Place(sim, TestData.Herbivore, OpenLand(sim, 20f));
        for (int i = 0; i < 200 && c.Alive; i++)
        {
            // 먹이를 먹어 회복하지 못하게 매 틱 굶긴다.
            c.Energy = 0f;
            c.Health = MathF.Min(c.Health, 0.001f);
            sim.Step();
        }
        Assert.False(c.Alive);
        Carcass k = Assert.Single(sim.World.Carcasses);
        int cell = sim.World.Terrain.CellIndexAt(k.Position);
        float startRemains = k.RemainsKg;

        for (int day = 0; day < 3; day++) TestData.Run(sim, sim.World.Config.TicksPerDay);
        Assert.True(k.RemainsKg < startRemains * 0.8f);
        Assert.True(sim.World.Vegetation.Nutrient[cell] > 0f, "사체 자리의 토양 영양이 늘지 않았습니다.");
        Assert.True(sim.World.Vegetation.TotalNutrientAdded > 0);

        for (int day = 0; day < 40 && sim.World.Carcasses.Count > 0; day++) TestData.Run(sim, sim.World.Config.TicksPerDay);
        Assert.Empty(sim.World.Carcasses);
        WorldState w = sim.World;
        Assert.Equal(w.CarcassMassCreated, w.CarcassMassDecomposed, 0);
        Assert.Equal(w.CarcassMassDecomposed * CarcassRules.NutrientFraction, w.Vegetation.TotalNutrientAdded, 0);
    }

    [Fact]
    public void SoilNutrient_SpeedsRegrowth_AndIsConsumed()
    {
        SimulationCore a = DaySim(), b = DaySim();
        VegetationGrid va = a.World.Vegetation, vb = b.World.Vegetation;
        int cell = Enumerable.Range(0, va.Capacity.Length).First(i => va.Capacity[i] > 1f && a.World.Terrain.Moisture[i] > 0.5f);
        float start = va.Capacity[cell] * 0.1f;
        va.Biomass[cell] = vb.Biomass[cell] = start;
        vb.AddNutrient(cell, 20f);
        var noon = new EnvironmentState { Light = 1f, TemperatureC = 22f };
        for (int i = 0; i < 10; i++)
        {
            va.Update(a.World.Terrain, a.World.Biomes, a.World.Forage, a.World.Config, noon, 0.02f);
            vb.Update(b.World.Terrain, b.World.Biomes, b.World.Forage, b.World.Config, noon, 0.02f);
        }
        float plain = va.Biomass[cell] - start, fed = vb.Biomass[cell] - start;
        Assert.True(plain > 0f);
        Assert.True(fed > plain * 1.4f, $"영양이 있어도 더 자라지 않았습니다: 증가량 {plain:0.000} vs {fed:0.000}");
        Assert.True(vb.Nutrient[cell] < 20f);
        Assert.True(vb.TotalNutrientUsed > 0);
        Assert.True(vb.Biomass[cell] <= vb.Capacity[cell] * 1.0001f);
        Assert.Equal(vb.TotalNutrientAdded, vb.SumNutrient() + vb.TotalNutrientUsed + vb.TotalNutrientLeached, 3);
    }

    [Fact]
    public void Predator_IgnoresCarcassAcrossDeepWater()
    {
        SimulationCore sim = DaySim();
        TerrainGrid t = sim.World.Terrain;
        int rexIndex = SpeciesIndex(sim, TestData.Predator);
        SpeciesDefinition rexSp = sim.World.Species[rexIndex];
        // 깊은 물을 사이에 둔 양쪽 마른 땅을 찾는다.
        for (int i = 0; i < t.CellCount; i += 37)
        {
            if (t.WaterDepth(i) < TerrainGrid.WadeDepth + 0.3f) continue;
            Vec2 center = t.CellCenter(i);
            foreach (Vec2 dir in new[] { new Vec2(1, 0), new Vec2(0, 1), new Vec2(0.7f, 0.7f) })
            {
                Vec2 a = center - dir * 25f, b = center + dir * 25f;
                if (!t.IsDryLandAt(a) || !t.IsDryLandAt(b) || t.IsLineWalkable(a, b)) continue;
                var rex = new Creature { Id = 999, SpeciesIndex = rexIndex, Position = a };
                sim.World.Carcasses.Add(new Carcass { Id = 5000, SpeciesIndex = 0, Position = b, MeatKg = 1000, RemainsKg = 2000, InitialMassKg = 2000, InitialMeatKg = 1000 });
                Assert.Null(Perception.FindCarcass(rex, rexSp, sim.World));
                sim.World.Carcasses[0].Position = a + new Vec2(5f, 0f);
                Assert.NotNull(Perception.FindCarcass(rex, rexSp, sim.World));
                return;
            }
        }
        Assert.Fail("깊은 물 양쪽의 마른 땅을 찾지 못했습니다.");
    }

    [Fact]
    public void Predation_IsDeterministic_AndSurvivesSaveLoad()
    {
        SimulationCore Build()
        {
            SimulationCore sim = TestData.Data.CreateSimulation(TestData.Scenario("predator_introduction"));
            foreach (Creature c in sim.Creatures.Where(c => c.SpeciesIndex == 1)) c.Energy = 0.3f;
            return sim;
        }
        SimulationCore a = Build(), b = Build();
        int kills = 0;
        for (int i = 0; i < 18000; i++)
        {
            a.Step();
            b.Step();
            if (i % 3000 == 2999) Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        }
        kills = a.Stats.TotalDeathsByCause(DeathCause.Predation);
        Assert.True(kills > 0, "3일 동안 사냥이 한 번도 성공하지 않았습니다.");
        Assert.NotEmpty(a.World.Carcasses);

        LoadResult loaded = SaveSerializer.Deserialize(SaveSerializer.Serialize(a, "포식"), TestData.Data);
        Assert.True(loaded.Ok, string.Join(" / ", loaded.Errors));
        Assert.Empty(loaded.Warnings);
        SimulationCore c = loaded.Sim!;
        Assert.Equal(a.ComputeStateHash(), c.ComputeStateHash());
        Assert.Equal(a.World.Carcasses.Count, c.World.Carcasses.Count);
        Assert.Equal(a.World.Vegetation.Nutrient, c.World.Vegetation.Nutrient);
        for (int i = 0; i < 6000; i++)
        {
            a.Step();
            c.Step();
        }
        Assert.Equal(a.ComputeStateHash(), c.ComputeStateHash());
    }

    [Fact]
    public void PredatorIntroduction_ReducesHerbivores_ComparedToNoPredators()
    {
        ScenarioDefinition scenario = TestData.Scenario("predator_introduction");
        Assert.Equal("scientific", scenario.Mode);
        SimulationCore with = TestData.Data.CreateSimulation(scenario);
        SimulationCore without = TestData.Data.CreateSimulation(scenario.World);
        without.Enqueue(new SpawnCreaturesCommand(TestData.Herbivore, 40, new Vec2(256, 256), 512 * 0.45f));
        without.ApplyPendingCommands();
        int herb = SpeciesIndex(with, TestData.Herbivore), rex = SpeciesIndex(with, TestData.Predator);
        Assert.Equal(40, with.Creatures.Count(c => c.SpeciesIndex == herb));
        Assert.Equal(2, with.Creatures.Count(c => c.SpeciesIndex == rex));

        for (int day = 0; day < 30; day++)
        {
            TestData.Run(with, with.World.Config.TicksPerDay);
            TestData.Run(without, without.World.Config.TicksPerDay);
        }
        int herbWith = with.Creatures.Count(c => c.SpeciesIndex == herb);
        int herbWithout = without.Creatures.Count(c => c.SpeciesIndex == herb);
        Assert.True(herbWith < herbWithout, $"포식자가 있어도 초식동물 수가 줄지 않았습니다: {herbWith} vs {herbWithout}");
        Assert.True(with.Stats.DeathsOf(herb, DeathCause.Predation) > 0);
        Assert.Equal(0, without.Stats.TotalDeathsByCause(DeathCause.Predation));
        Assert.True(with.Creatures.Count(c => c.SpeciesIndex == rex) > 0, "30일 안에 포식자가 모두 죽었습니다.");
        Assert.True(with.Stats.Samples[^1].Counts[herb] == herbWith);
    }
}
