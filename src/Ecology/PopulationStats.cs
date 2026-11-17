using System;
using System.Collections.Generic;
using PaleoSphere.Creatures;

namespace PaleoSphere.Ecology;

/// <summary>한 시점의 집계 값.</summary>
public struct StatSample
{
    public long Tick;
    public float Day;
    public int[] Counts;
    public float VegetationKg;

    /// <summary>사체에 남은 고기(kg).</summary>
    public float CarcassMeatKg;
    public float MeanEnergy;
    public float MeanHydration;
    public int TotalDeaths;
}

/// <summary>종별 개체수·식생량 시계열과 사망 원인 누계. 그래프와 내보내기의 원본이다.</summary>
public sealed class PopulationStats
{
    public const int MaxSamples = 2400;

    public int SpeciesCount { get; }
    public int SampleIntervalTicks { get; set; }
    public List<StatSample> Samples { get; } = new();

    /// <summary>[종 색인 × DeathCauseCount + 원인] 누계.</summary>
    public int[] Deaths { get; }
    public int[] Spawned { get; }

    public PopulationStats(int speciesCount, int sampleIntervalTicks)
    {
        SpeciesCount = speciesCount;
        SampleIntervalTicks = Math.Max(1, sampleIntervalTicks);
        Deaths = new int[speciesCount * BehaviorKinds.DeathCauseCount];
        Spawned = new int[speciesCount];
    }

    public void OnSpawn(int speciesIndex) => Spawned[speciesIndex]++;

    public void OnDeath(int speciesIndex, DeathCause cause) =>
        Deaths[speciesIndex * BehaviorKinds.DeathCauseCount + (int)cause]++;

    public int DeathsOf(int speciesIndex, DeathCause cause) =>
        Deaths[speciesIndex * BehaviorKinds.DeathCauseCount + (int)cause];

    public int TotalDeathsByCause(DeathCause cause)
    {
        int total = 0;
        for (int s = 0; s < SpeciesCount; s++) total += DeathsOf(s, cause);
        return total;
    }

    /// <summary>사용자가 지운 개체를 뺀 사망 수.</summary>
    public int TotalNaturalDeaths()
    {
        int total = 0;
        for (int s = 0; s < SpeciesCount; s++)
        {
            for (int c = 1; c < BehaviorKinds.DeathCauseCount; c++)
            {
                if (c == (int)DeathCause.Removed) continue;
                total += Deaths[s * BehaviorKinds.DeathCauseCount + c];
            }
        }
        return total;
    }

    public void Sample(long tick, float day, IReadOnlyList<Creature> creatures, double vegetationKg, double carcassMeatKg = 0)
    {
        var counts = new int[SpeciesCount];
        double energy = 0, hydration = 0;
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature c = creatures[i];
            counts[c.SpeciesIndex]++;
            energy += c.Energy;
            hydration += c.Hydration;
        }
        int n = creatures.Count;
        Samples.Add(new StatSample
        {
            Tick = tick,
            Day = day,
            Counts = counts,
            VegetationKg = (float)vegetationKg,
            CarcassMeatKg = (float)carcassMeatKg,
            MeanEnergy = n > 0 ? (float)(energy / n) : 0f,
            MeanHydration = n > 0 ? (float)(hydration / n) : 0f,
            TotalDeaths = TotalNaturalDeaths(),
        });

        // 표본이 너무 많아지면 절반으로 솎고 간격을 두 배로 늘린다.
        if (Samples.Count >= MaxSamples)
        {
            int write = 0;
            for (int read = 0; read < Samples.Count; read += 2) Samples[write++] = Samples[read];
            Samples.RemoveRange(write, Samples.Count - write);
            SampleIntervalTicks *= 2;
        }
    }

    /// <summary>나이 분포. 0~maxAgeDays를 bins 구간으로 나눈 개체 수.</summary>
    public static int[] AgeHistogram(IReadOnlyList<Creature> creatures, int speciesIndex, float maxAgeDays, int bins)
    {
        var hist = new int[bins];
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature c = creatures[i];
            if (c.SpeciesIndex != speciesIndex) continue;
            int b = (int)(c.AgeDays / maxAgeDays * bins);
            if (b < 0) b = 0;
            if (b >= bins) b = bins - 1;
            hist[b]++;
        }
        return hist;
    }
}
