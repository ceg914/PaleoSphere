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

/// <summary>Phase 3-1: 나이에 따른 성장, 생애 단계, 몸집의 영향, 노화와 수명.</summary>
public class GrowthTests
{
    private static SpeciesDefinition Species(string id)
    {
        Assert.True(TestData.Data.Species.TryGetIndex(id, out int i));
        return TestData.Data.Species[i];
    }

    private static Creature Place(SimulationCore sim, string species, Vec2 at)
    {
        int before = sim.NextCreatureId;
        sim.Enqueue(new SpawnCreaturesCommand(species, 1, at, 0.5f));
        sim.ApplyPendingCommands();
        Assert.True(sim.TryGetCreature(before, out Creature? c), "배치 실패: " + species);
        return c!;
    }

    private static Vec2 DryLand(SimulationCore sim)
    {
        TerrainGrid t = sim.World.Terrain;
        for (float z = 40; z < t.WorldSize - 40; z += 4)
            for (float x = 40; x < t.WorldSize - 40; x += 4)
            {
                bool ok = true;
                for (float d = -24; d <= 24 && ok; d += 4)
                    ok = t.IsDryLandAt(new Vec2(x + d, z)) && t.IsDryLandAt(new Vec2(x, z + d));
                if (ok) return new Vec2(x, z);
            }
        throw new InvalidOperationException("마른 땅을 찾지 못했습니다.");
    }

    [Fact]
    public void LengthFraction_StartsAtHatchSize_ReachesOneAtAdulthood_AndNeverShrinks()
    {
        SpeciesDefinition sp = Species(TestData.Herbivore);
        Assert.Equal(sp.HatchLengthFraction, LifeHistory.LengthFraction(sp, 0f), 5);
        Assert.Equal(1f, LifeHistory.LengthFraction(sp, sp.AdultAgeDays), 5);
        Assert.Equal(1f, LifeHistory.LengthFraction(sp, sp.MaxAgeDays));
        float prev = 0f;
        for (int i = 0; i <= 200; i++)
        {
            float l = LifeHistory.LengthFraction(sp, sp.AdultAgeDays * 1.5f * i / 200f);
            Assert.True(l >= prev - 1e-6f, $"{i}: {l} < {prev}");
            Assert.InRange(l, sp.HatchLengthFraction - 1e-6f, 1f);
            prev = l;
        }
    }

    [Fact]
    public void MassGrowth_IsFastestInTheMiddle()
    {
        // 몸무게(길이³)로 보면 S자 곡선: 가장 빨리 느는 때가 처음이나 끝이 아니다.
        SpeciesDefinition sp = Species(TestData.Herbivore);
        int steps = 100;
        int peak = 0;
        float best = 0f;
        for (int i = 0; i < steps; i++)
        {
            float a = LifeHistory.LengthFraction(sp, sp.AdultAgeDays * i / steps);
            float b = LifeHistory.LengthFraction(sp, sp.AdultAgeDays * (i + 1) / steps);
            float rate = b * b * b - a * a * a;
            if (rate > best) { best = rate; peak = i; }
        }
        Assert.InRange(peak, 20, 80);
    }

    [Fact]
    public void Stages_FollowAge()
    {
        SpeciesDefinition sp = Species(TestData.Herbivore);
        var c = new Creature { LifespanDays = sp.MaxAgeDays, TraitBodySize = 1f };
        c.AgeDays = sp.SubadultAgeDays * 0.5f;
        Assert.Equal(LifeStage.Juvenile, LifeHistory.StageOf(c, sp));
        c.AgeDays = (sp.SubadultAgeDays + sp.AdultAgeDays) * 0.5f;
        Assert.Equal(LifeStage.Subadult, LifeHistory.StageOf(c, sp));
        c.AgeDays = sp.AdultAgeDays + 1f;
        Assert.Equal(LifeStage.Adult, LifeHistory.StageOf(c, sp));
        c.AgeDays = sp.MaxAgeDays * sp.SenescenceFraction + 1f;
        Assert.Equal(LifeStage.Senior, LifeHistory.StageOf(c, sp));
    }

    [Fact]
    public void Juveniles_AreSmaller_EatLess_AndMoveSlower()
    {
        SpeciesDefinition sp = Species(TestData.Herbivore);
        var adult = new Creature { AgeDays = sp.AdultAgeDays * 2f, LifespanDays = sp.MaxAgeDays, TraitBodySize = 1f };
        var young = new Creature { AgeDays = 0f, LifespanDays = sp.MaxAgeDays, TraitBodySize = 1f };
        Assert.Equal(1f, LifeHistory.BodyScale(adult, sp));
        Assert.Equal(sp.HatchLengthFraction, LifeHistory.BodyScale(young, sp), 5);
        Assert.True(LifeHistory.MassKg(young, sp) < sp.BodyMassKg * 0.01f);
        // 먹이량은 몸무게의 3/4 제곱: 몸무게 비보다는 크고 길이 비보다는 작다.
        float food = LifeHistory.FoodScale(young, sp);
        float mass = LifeHistory.MassKg(young, sp) / sp.BodyMassKg;
        Assert.True(food > mass && food < sp.HatchLengthFraction, $"food {food}, mass {mass}");
        Assert.True(LifeHistory.SpeedFactor(young, sp) < LifeHistory.SpeedFactor(adult, sp));
        Assert.Equal(1f, LifeHistory.SpeedFactor(adult, sp));
    }

    [Fact]
    public void Seniors_SlowDown_AndHealLess()
    {
        SpeciesDefinition sp = Species(TestData.Herbivore);
        var c = new Creature { LifespanDays = sp.MaxAgeDays, TraitBodySize = 1f, AgeDays = sp.AdultAgeDays * 2f };
        Assert.Equal(1f, LifeHistory.RegenFactor(c, sp));
        float start = LifeHistory.SenescenceAgeDays(c, sp);
        c.AgeDays = start + (c.LifespanDays - start) * 0.5f;
        Assert.Equal(0.5f, LifeHistory.SeniorProgress(c, sp), 3);
        Assert.True(LifeHistory.RegenFactor(c, sp) < 1f);
        Assert.True(LifeHistory.SpeedFactor(c, sp) < 1f);
    }

    [Fact]
    public void Lifespan_IsRolledPerCreature_WithinVariation()
    {
        SimulationCore sim = TestData.NewSim(seed: 5, creatures: 40);
        SpeciesDefinition sp = sim.World.Species[0];
        float min = sp.MaxAgeDays * (1f - sp.LifespanVariation);
        Assert.All(sim.Creatures, c => Assert.InRange(c.LifespanDays, min, sp.MaxAgeDays));
        Assert.True(sim.Creatures.Select(c => c.LifespanDays).Distinct().Count() > 30, "수명이 개체마다 달라야 합니다.");
        // 배치 직후는 모두 성체다(노년 전).
        Assert.All(sim.Creatures, c => Assert.Equal(LifeStage.Adult, c.LifeStage));

        SimulationCore again = TestData.NewSim(seed: 5, creatures: 40);
        Assert.Equal(sim.Creatures.Select(c => c.LifespanDays), again.Creatures.Select(c => c.LifespanDays));
    }

    [Fact]
    public void Creature_DiesOfOldAge_AtLifespan_AndLeavesCarcass()
    {
        SimulationCore sim = TestData.NewSim(seed: 9);
        Creature c = Place(sim, TestData.Herbivore, DryLand(sim));
        c.AgeDays = c.LifespanDays - 0.02f;
        c.Energy = 1f; c.Hydration = 1f; c.Health = 1f;
        int ticksPerDay = sim.World.Config.TicksPerDay;
        TestData.Run(sim, ticksPerDay / 10);
        Assert.False(c.Alive);
        Assert.Equal(DeathCause.OldAge, c.DeathCause);
        Assert.Equal(1, sim.Stats.TotalDeathsByCause(DeathCause.OldAge));
        Carcass k = Assert.Single(sim.World.Carcasses);
        Assert.Equal(c.Id, k.Id);
    }

    [Fact]
    public void Juveniles_Grow_ThroughStages_InTheWorld()
    {
        SimulationCore sim = TestData.NewSim(seed: 11);
        SpeciesDefinition sp = sim.World.Species[0];
        Creature c = Place(sim, TestData.Herbivore, DryLand(sim));
        c.AgeDays = 0.5f;
        TestData.Run(sim, 10);
        Assert.Equal(LifeStage.Juvenile, c.LifeStage);
        float small = LifeHistory.BodyScale(c, sp);

        int ticks = (int)((sp.SubadultAgeDays + 1f) * sim.World.Config.TicksPerDay);
        TestData.Run(sim, ticks);
        Assert.True(c.Alive, "어린 개체가 풍부한 세계에서 살아남아야 합니다: " + c.DeathCause);
        Assert.Equal(LifeStage.Subadult, c.LifeStage);
        Assert.True(LifeHistory.BodyScale(c, sp) > small * 1.5f);

        // 화면용 스냅샷도 자란 크기를 따른다.
        var snap = new SimSnapshot();
        sim.FillSnapshot(snap);
        CreatureSnapshot s = snap.Creatures.Take(snap.Count).Single(x => x.Id == c.Id);
        Assert.Equal(LifeHistory.BodyScale(c, sp), s.BodySize);
    }

    [Fact]
    public void Predators_Prefer_YoungPrey_AndKillThemMoreOften()
    {
        SimulationCore sim = TestData.NewSim(seed: 13);
        Vec2 at = DryLand(sim);
        Creature rex = Place(sim, TestData.Predator, at);
        Creature adult = Place(sim, TestData.Herbivore, at + new Vec2(20f, 0f));
        Creature young = Place(sim, TestData.Herbivore, at + new Vec2(-20f, 0f));
        adult.Position = at + new Vec2(20f, 0f);
        young.Position = at + new Vec2(-20f, 0f);
        rex.Position = at;
        adult.AgeDays = sim.World.Species[adult.SpeciesIndex].AdultAgeDays * 2f;
        young.AgeDays = 1f;
        adult.Health = young.Health = 1f;
        adult.Fatigue = young.Fatigue = 0f;
        SpeciesDefinition rexSp = sim.World.Species[rex.SpeciesIndex];
        SpeciesDefinition triSp = sim.World.Species[adult.SpeciesIndex];
        Assert.Same(young, Perception.FindPrey(rex, rexSp, sim.World));

        int Kills(float preyAge)
        {
            int kills = 0;
            for (int i = 0; i < 400; i++)
            {
                var hunter = new Creature { Id = 1000, AgeDays = rexSp.AdultAgeDays * 2f, LifespanDays = rexSp.MaxAgeDays, TraitBodySize = 1f, Health = 1f, Rng = Pcg32.Create(77, (ulong)i) };
                var prey = new Creature { Id = 2000 + i, AgeDays = preyAge, LifespanDays = triSp.MaxAgeDays, TraitBodySize = 1f, Health = 1f, Alive = true };
                BehaviorExecutor.ResolveAttack(hunter, rexSp, prey, triSp, sim.World, 0);
                if (!prey.Alive) kills++;
            }
            sim.World.DeathsPending = false;
            return kills;
        }
        int adultKills = Kills(triSp.AdultAgeDays * 2f);
        int youngKills = Kills(1f);
        Assert.True(youngKills > adultKills * 1.5f, $"새끼 {youngKills}, 성체 {adultKills}");
    }

    [Fact]
    public void AgesAndLifespans_SurviveSaveLoad_AndRunsMatch()
    {
        SimulationCore sim = TestData.NewSim(seed: 17, creatures: 12);
        SpeciesDefinition sp = sim.World.Species[0];
        for (int i = 0; i < sim.CreatureCount; i++)
        {
            Creature c = sim.Creatures[i];
            c.AgeDays = i % 3 == 0 ? 1f + i : i % 3 == 1 ? sp.AdultAgeDays + i : c.LifespanDays * 0.95f;
        }
        TestData.Run(sim, 600);
        byte[] file = SaveSerializer.Serialize(sim, "growth");
        LoadResult r = SaveSerializer.Deserialize(file, TestData.Data);
        Assert.True(r.Ok, string.Join(" / ", r.Errors));
        SimulationCore loaded = r.Sim!;
        Assert.Equal(sim.Creatures.Select(c => (c.AgeDays, c.LifespanDays, c.LifeStage)),
                     loaded.Creatures.Select(c => (c.AgeDays, c.LifespanDays, c.LifeStage)));
        Assert.Contains(loaded.Creatures, c => c.LifeStage == LifeStage.Juvenile);
        Assert.Contains(loaded.Creatures, c => c.LifeStage == LifeStage.Senior);
        TestData.Run(sim, 3000);
        TestData.Run(loaded, 3000);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
    }
}
