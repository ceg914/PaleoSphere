using PaleoSphere.AI;
using PaleoSphere.Core;
using PaleoSphere.Creatures;
using PaleoSphere.Simulation;
using PaleoSphere.World;
using Xunit;

namespace PaleoSphere.Tests.Unit;

/// <summary>TEST-04: 입력이 바뀌면 AI가 알맞은 행동을 고르고 이유를 남긴다.</summary>
public class BrainTests
{
    private static SimulationCore DaySim()
    {
        var cfg = new WorldConfig { Seed = 20261116, StartHour = 12f };
        return TestData.NewSim(config: cfg, creatures: 1);
    }

    private static Creature Calm(SimulationCore sim)
    {
        Creature c = sim.Creatures[0];
        c.Energy = 0.95f;
        c.Hydration = 0.95f;
        c.Fatigue = 0.05f;
        c.Fear = 0f;
        c.Behavior = BehaviorKind.Idle;
        return c;
    }

    [Fact]
    public void Satisfied_Explores()
    {
        SimulationCore sim = DaySim();
        Creature c = Calm(sim);
        Assert.Equal(BehaviorKind.Explore, UtilityBrain.Decide(c, sim.World, true));
        Assert.Equal(DecisionReason.HighestScore, c.LastReason);
        Assert.Equal(sim.Clock.Tick, c.LastDecisionTick);
    }

    [Fact]
    public void Hungry_Forages()
    {
        SimulationCore sim = DaySim();
        Creature c = Calm(sim);
        c.Energy = 0.4f;
        Assert.Equal(BehaviorKind.Forage, UtilityBrain.Decide(c, sim.World, true));
        Assert.True(c.LastScores[(int)BehaviorKind.Forage] > c.LastScores[(int)BehaviorKind.Explore]);
    }

    [Fact]
    public void Thirsty_Drinks_AndThirstBeatsMildHunger()
    {
        SimulationCore sim = DaySim();
        Creature c = Calm(sim);
        c.Hydration = 0.3f;
        c.Energy = 0.7f;
        Assert.Equal(BehaviorKind.Drink, UtilityBrain.Decide(c, sim.World, true));
    }

    [Fact]
    public void Tired_Rests_AndNightMakesRestMoreAttractive()
    {
        SimulationCore day = DaySim();
        Creature c = Calm(day);
        c.Fatigue = 0.9f;
        Assert.Equal(BehaviorKind.Rest, UtilityBrain.Decide(c, day.World, true));
        float dayScore = c.LastScores[(int)BehaviorKind.Rest];

        SimulationCore night = TestData.NewSim(config: new WorldConfig { Seed = 20261116, StartHour = 23f }, creatures: 1);
        Creature n = Calm(night);
        n.Fatigue = 0.9f;
        UtilityBrain.Decide(n, night.World, true);
        Assert.True(n.LastScores[(int)BehaviorKind.Rest] > dayScore);
    }

    [Fact]
    public void SafetyRule_ThreatOverridesEverything()
    {
        SimulationCore sim = DaySim();
        Creature c = Calm(sim);
        c.Energy = 0.05f;
        c.Hydration = 0.05f;
        c.Fear = 0.9f;
        c.ThreatUntilTick = sim.Clock.Tick + 100;
        Assert.Equal(BehaviorKind.Flee, UtilityBrain.Decide(c, sim.World, true));
        Assert.Equal(DecisionReason.SafetyThreat, c.LastReason);
    }

    [Fact]
    public void SafetyRule_DehydrationInterruptsRest()
    {
        SimulationCore sim = DaySim();
        Creature c = Calm(sim);
        c.Behavior = BehaviorKind.Rest;
        c.Fatigue = 1f;
        c.Hydration = 0.1f;
        Assert.Equal(BehaviorKind.Drink, UtilityBrain.Decide(c, sim.World, true));
        Assert.Equal(DecisionReason.SafetyDehydration, c.LastReason);
    }

    [Fact]
    public void SafetyRule_StarvationForcesForage()
    {
        SimulationCore sim = DaySim();
        Creature c = Calm(sim);
        c.Behavior = BehaviorKind.Rest;
        c.Fatigue = 1f;
        c.Energy = 0.05f;
        Assert.Equal(BehaviorKind.Forage, UtilityBrain.Decide(c, sim.World, false));
        Assert.Equal(DecisionReason.SafetyStarvation, c.LastReason);
    }

    [Fact]
    public void SafetyRule_ExhaustedStarvingPredator_RestsBeforeHunting()
    {
        // 0.2.0의 버그: 지친 굶주린 포식자가 사냥을 강제받고 바로 포기하기를 되풀이하며 쉬지 못했다.
        SimulationCore sim = DaySim();
        sim.Enqueue(new SpawnCreaturesCommand(TestData.Predator, 1, sim.Creatures[0].Position, 3f));
        sim.ApplyPendingCommands();
        Creature rex = sim.Creatures[^1];
        Assert.True(sim.World.Species[rex.SpeciesIndex].IsPredator);
        rex.Energy = 0.05f; rex.Hydration = 0.95f; rex.Fear = 0f;
        rex.Behavior = BehaviorKind.Hunt;
        rex.Fatigue = 0.95f;
        Assert.Equal(BehaviorKind.Rest, UtilityBrain.Decide(rex, sim.World, false));
        Assert.Equal(DecisionReason.SafetyStarvation, rex.LastReason);
        // 쉬기 시작했으면 피로가 절반 아래로 내려갈 때까지 쉰다.
        rex.Behavior = BehaviorKind.Rest;
        rex.Fatigue = 0.6f;
        Assert.Equal(BehaviorKind.Rest, UtilityBrain.Decide(rex, sim.World, false));
        rex.Fatigue = 0.4f;
        Assert.Equal(BehaviorKind.Hunt, UtilityBrain.Decide(rex, sim.World, false));
        // 사냥하던 중이면 피로가 한계 아래일 때 계속 사냥한다.
        rex.Behavior = BehaviorKind.Hunt;
        rex.Fatigue = 0.7f;
        Assert.Equal(BehaviorKind.Hunt, UtilityBrain.Decide(rex, sim.World, false));
    }

    [Fact]
    public void ExpiredThreat_DoesNotCauseFlee()
    {
        SimulationCore sim = DaySim();
        Creature c = Calm(sim);
        c.Fear = 0.9f;
        c.ThreatUntilTick = sim.Clock.Tick - 1;
        Assert.NotEqual(BehaviorKind.Flee, UtilityBrain.Decide(c, sim.World, true));
        Assert.Equal(0f, c.LastScores[(int)BehaviorKind.Flee]);
    }

    [Fact]
    public void NoReachableWater_DrinkScoresZero()
    {
        var cfg = new WorldConfig { Seed = 3, SizeCells = 64, WaterFraction = 0f, River = false, StartHour = 12f };
        SimulationCore sim = TestData.NewSim(config: cfg, creatures: 1);
        Creature c = Calm(sim);
        c.Hydration = 0.05f;
        BehaviorKind chosen = UtilityBrain.Decide(c, sim.World, true);
        Assert.NotEqual(BehaviorKind.Drink, chosen);
        Assert.Equal(0f, c.LastScores[(int)BehaviorKind.Drink]);
    }

    [Fact]
    public void Commitment_KeepsEatingUntilSated()
    {
        SimulationCore sim = DaySim();
        Creature c = Calm(sim);
        c.Energy = 0.85f;
        c.Behavior = BehaviorKind.Forage;
        Assert.Equal(BehaviorKind.Forage, UtilityBrain.Decide(c, sim.World, true));
        c.Energy = 0.95f;
        Assert.NotEqual(BehaviorKind.Forage, UtilityBrain.Decide(c, sim.World, true));
    }

    [Fact]
    public void Scores_AreFinite_AndChoiceIsTheMaximum_ForRandomStates()
    {
        SimulationCore sim = DaySim();
        Creature c = sim.Creatures[0];
        Pcg32 rng = Pcg32.Create(99, 1);
        for (int i = 0; i < 2000; i++)
        {
            c.Energy = rng.NextFloat();
            c.Hydration = rng.NextFloat();
            c.Fatigue = rng.NextFloat();
            c.Fear = rng.NextFloat();
            c.ThreatUntilTick = rng.NextFloat() < 0.5f ? sim.Clock.Tick + 10 : -1;
            c.Behavior = (BehaviorKind)rng.NextInt(BehaviorKinds.Count);
            BehaviorKind chosen = UtilityBrain.Decide(c, sim.World, rng.NextFloat() < 0.5f);
            float max = float.NegativeInfinity;
            foreach (float s in c.LastScores)
            {
                Assert.True(MathUtil.IsFinite(s) && s >= 0f);
                if (s > max) max = s;
            }
            if (c.LastReason == DecisionReason.HighestScore)
                Assert.Equal(max, c.LastScores[(int)chosen]);
        }
    }

    [Fact]
    public void BehaviorLog_RecordsReasonStateAndResult()
    {
        SimulationCore sim = DaySim();
        Creature c = Calm(sim);
        c.Log.Clear();
        c.Hydration = 0.3f;
        BehaviorKind first = UtilityBrain.Decide(c, sim.World, true);
        BehaviorExecutor.SwitchBehavior(c, first, sim.Clock.Tick);
        Assert.Single(c.Log);
        Assert.Equal(BehaviorKind.Drink, c.Log[0].Behavior);
        Assert.Equal(0.3f, c.Log[0].Hydration);
        Assert.Equal(BehaviorResult.Ongoing, c.Log[0].Result);
        Assert.True(c.Log[0].Score > 0f);

        c.Hydration = 0.99f;
        c.BoutAmount = 0.69f;
        BehaviorKind second = UtilityBrain.Decide(c, sim.World, true);
        BehaviorExecutor.SwitchBehavior(c, second, sim.Clock.Tick + 50);
        Assert.Equal(2, c.Log.Count);
        Assert.Equal(BehaviorResult.Completed, c.Log[0].Result);
        Assert.Equal(0.69f, c.Log[0].ResultAmount);
        Assert.Equal(sim.Clock.Tick + 50, c.Log[0].EndTick);
    }

    [Fact]
    public void BehaviorLog_IsBounded()
    {
        SimulationCore sim = DaySim();
        Creature c = Calm(sim);
        for (int i = 0; i < 100; i++)
            BehaviorExecutor.SwitchBehavior(c, i % 2 == 0 ? BehaviorKind.Explore : BehaviorKind.Idle, i);
        Assert.Equal(Creature.MaxLogEntries, c.Log.Count);
    }
}
