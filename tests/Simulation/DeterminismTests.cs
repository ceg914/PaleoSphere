using PaleoSphere.Core;
using PaleoSphere.Simulation;
using PaleoSphere.World;
using Xunit;

namespace PaleoSphere.Tests.Simulation;

/// <summary>TEST-01: 같은 버전·시드·설정이면 상태 해시가 같다.</summary>
public class DeterminismTests
{
    /// <summary>
    /// 기준 실행의 상태 해시. 시뮬레이션 규칙이나 data/ 수치를 바꾸면 달라진다.
    /// 의도한 변경이면 SimVersion.Simulation을 올리고 이 값을 갱신한다.
    /// 이 값은 개발 환경(Linux), GitHub의 Ubuntu 러너, Windows 러너에서 같게 나온다.
    /// 어느 한 곳에서만 실패하면 코어에 플랫폼 수학 함수(MathF.Sin 등)가 섞여 들어간 것이다. DetMath를 쓴다.
    /// </summary>
    private const string GoldenHash = "dddb57be64aae1c6";

    private static SimulationCore Reference()
    {
        SimulationCore sim = TestData.NewSim(seed: 4242, creatures: 20);
        TestData.Run(sim, 3000);
        return sim;
    }

    [Fact]
    public void SameSeed_SameHash()
    {
        Assert.Equal(Reference().ComputeStateHash(), Reference().ComputeStateHash());
    }

    [Fact]
    public void GoldenHash_Matches()
    {
        Assert.Equal(GoldenHash, StateHasher.ToHex(Reference().ComputeStateHash()));
    }

    [Fact]
    public void DifferentSeed_DifferentHash()
    {
        SimulationCore a = TestData.NewSim(seed: 1, creatures: 10);
        SimulationCore b = TestData.NewSim(seed: 2, creatures: 10);
        TestData.Run(a, 500);
        TestData.Run(b, 500);
        Assert.NotEqual(a.ComputeStateHash(), b.ComputeStateHash());
    }

    [Fact]
    public void HashChangesEveryTick_WhenCreaturesExist()
    {
        SimulationCore sim = TestData.NewSim(creatures: 5);
        ulong before = sim.ComputeStateHash();
        sim.Step();
        Assert.NotEqual(before, sim.ComputeStateHash());
    }

    [Fact]
    public void ReadingState_DoesNotChangeIt()
    {
        SimulationCore a = TestData.NewSim(seed: 5, creatures: 15);
        SimulationCore b = TestData.NewSim(seed: 5, creatures: 15);
        var snapshot = new SimSnapshot();
        for (int i = 0; i < 1500; i++)
        {
            a.Step();
            b.Step();
            // b만 매 틱 스냅샷·상세·해시를 읽는다(렌더러와 UI가 하는 일).
            b.FillSnapshot(snapshot);
            if (b.CreatureCount > 0) b.GetDetails(b.Creatures[0].Id);
            b.ComputeStateHash();
            b.DrainCommandResults();
        }
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Equal(b.CreatureCount, snapshot.Count);
    }

    [Theory]
    [InlineData(1.0 / 144.0)]
    [InlineData(1.0 / 60.0)]
    [InlineData(1.0 / 24.0)]
    [InlineData(0.25)]
    public void FrameRate_DoesNotAffectResult(double frameSeconds)
    {
        // 프레임 길이가 달라도 같은 틱 수에 이르면 상태가 같아야 한다.
        const int targetTicks = 1200;
        SimulationCore reference = TestData.NewSim(seed: 77, creatures: 12);
        TestData.Run(reference, targetTicks);

        SimulationCore sim = TestData.NewSim(seed: 77, creatures: 12);
        var driver = new FixedTimestepDriver { TimeScale = 5f, MaxFrameMilliseconds = double.PositiveInfinity, MaxStepsPerFrame = 1 };
        int guard = 0;
        while (sim.Clock.Tick < targetTicks && guard++ < 1_000_000) driver.Advance(frameSeconds, sim);
        Assert.Equal(targetTicks, sim.Clock.Tick);
        Assert.Equal(reference.ComputeStateHash(), sim.ComputeStateHash());
    }

    [Fact]
    public void Driver_PausedOrZeroScale_DoesNotStep()
    {
        SimulationCore sim = TestData.NewSim(creatures: 3);
        var driver = new FixedTimestepDriver { Paused = true };
        Assert.Equal(0, driver.Advance(10.0, sim));
        driver.Paused = false;
        driver.TimeScale = 0f;
        Assert.Equal(0, driver.Advance(10.0, sim));
        Assert.Equal(0, sim.Clock.Tick);
    }

    [Fact]
    public void Driver_RunsExpectedTicksAndReportsEffectiveSpeed()
    {
        SimulationCore sim = TestData.NewSim(creatures: 3);
        var driver = new FixedTimestepDriver { TimeScale = 2f, MaxFrameMilliseconds = double.PositiveInfinity };
        int steps = 0;
        for (int i = 0; i < 120; i++) steps += driver.Advance(1.0 / 60.0, sim);
        Assert.InRange(steps, 39, 41);
        Assert.InRange(driver.EffectiveSpeed, 1.9, 2.1);
        Assert.InRange(driver.Alpha, 0f, 1f);
    }

    [Fact]
    public void Driver_DropsBacklogInsteadOfSpiralling()
    {
        SimulationCore sim = TestData.NewSim(creatures: 3);
        var driver = new FixedTimestepDriver { TimeScale = 10f, MaxStepsPerFrame = 5, MaxFrameMilliseconds = double.PositiveInfinity };
        int steps = driver.Advance(1.0, sim);
        Assert.Equal(5, steps);
        Assert.True(driver.DroppedSeconds > 0);
        Assert.Equal(5, driver.Advance(1.0, sim));
    }

    [Fact]
    public void CommandsAtSameTicks_GiveSameResult()
    {
        SimulationCore Build()
        {
            SimulationCore sim = TestData.NewSim(seed: 9, config: new WorldConfig { Seed = 9, SizeCells = 96 });
            TestData.Run(sim, 100);
            sim.Enqueue(new SpawnCreaturesCommand(TestData.Herbivore, 8, new Vec2(90, 90), 30f));
            TestData.Run(sim, 300);
            sim.Enqueue(new DisturbanceCommand(new Vec2(90, 90), 60f, 1f));
            sim.Enqueue(new SpawnCreaturesCommand(TestData.Herbivore, 4, new Vec2(120, 60), 20f));
            TestData.Run(sim, 400);
            if (sim.CreatureCount > 0) sim.Enqueue(new RemoveCreatureCommand(sim.Creatures[0].Id));
            TestData.Run(sim, 200);
            return sim;
        }
        Assert.Equal(Build().ComputeStateHash(), Build().ComputeStateHash());
    }
}
