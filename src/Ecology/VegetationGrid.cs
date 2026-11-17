using System;
using PaleoSphere.Core;
using PaleoSphere.World;

namespace PaleoSphere.Ecology;

/// <summary>
/// 셀별 식생 바이오매스(kg). 성장·고사·섭식이 실제 값을 바꾸고, 변화량을 누계로 남겨 보존 여부를 검증한다.
/// </summary>
public sealed class VegetationGrid
{
    /// <summary>성장 조건(0~1)이 이 값 아래면 고사가 시작된다.</summary>
    public const float StressThreshold = 0.35f;

    /// <summary>토양 영양이 이만큼(kg)이면 성장 속도 가산이 최대의 절반이 된다.</summary>
    public const float NutrientHalfSaturationKg = 5f;

    /// <summary>토양 영양에 따른 성장 속도 가산의 최대치(1이면 최대 2배).</summary>
    public const float NutrientMaxBoost = 1f;

    /// <summary>늘어난 성장 1 kg마다 쓰이는 영양(kg).</summary>
    public const float NutrientPerExtraGrowth = 0.5f;

    /// <summary>쓰이지 않은 영양이 씻겨 나가는 비율(/일).</summary>
    public const float NutrientLeachPerDay = 0.02f;

    public int Size { get; }
    public float[] Biomass { get; }
    public float[] Capacity { get; }

    /// <summary>셀별 토양 영양(kg). 사체가 분해되며 쌓이고, 식생 성장을 빠르게 하면서 줄어든다.</summary>
    public float[] Nutrient { get; }

    public double TotalNutrientAdded { get; set; }
    public double TotalNutrientUsed { get; set; }
    public double TotalNutrientLeached { get; set; }

    public double InitialTotal { get; set; }
    public double TotalGrown { get; set; }
    public double TotalConsumed { get; set; }
    public double TotalDieback { get; set; }

    /// <summary>마지막 Update에서 계산한 전체 바이오매스(kg).</summary>
    public double LastTotal { get; private set; }

    /// <summary>식생이 자랄 수 있는 셀의 평균 수용량(kg). 섭식 기준을 세계의 풍요도에 맞추는 데 쓴다.</summary>
    public float MeanLandCapacity { get; private set; }

    public VegetationGrid(int size)
    {
        Size = size;
        Biomass = new float[size * size];
        Capacity = new float[size * size];
        Nutrient = new float[size * size];
    }

    /// <summary>셀에 토양 영양을 더한다(사체 분해).</summary>
    public void AddNutrient(int cell, float kg)
    {
        if (!(kg > 0f)) return;
        Nutrient[cell] += kg;
        TotalNutrientAdded += kg;
    }

    public double SumNutrient()
    {
        double total = 0;
        for (int i = 0; i < Nutrient.Length; i++) total += Nutrient[i];
        return total;
    }

    /// <summary>생물군계·토양·설정에서 셀별 수용량을 계산한다. 지형이 바뀌면 다시 부른다.</summary>
    public void RebuildCapacity(TerrainGrid terrain, BiomeRegistry biomes, WorldConfig cfg)
    {
        float area = terrain.CellSize * terrain.CellSize;
        for (int i = 0; i < Capacity.Length; i++)
        {
            BiomeDefinition b = biomes[terrain.Biome[i]];
            float k = terrain.IsWater(i) ? 0f : b.VegetationCapacityKgM2 * area * (0.5f + 0.5f * terrain.Fertility[i]) * cfg.VegetationCapacityScale;
            Capacity[i] = k;
            if (Biomass[i] > k) Biomass[i] = k;
        }

        double sum = 0;
        int land = 0;
        for (int i = 0; i < Capacity.Length; i++)
        {
            if (Capacity[i] <= 0f) continue;
            sum += Capacity[i];
            land++;
        }
        MeanLandCapacity = land > 0 ? (float)(sum / land) : 0f;
    }

    public void InitializeBiomass(TerrainGrid terrain, WorldConfig cfg)
    {
        float cs = terrain.CellSize;
        int n = Size;
        double total = 0;
        for (int cz = 0; cz < n; cz++)
        {
            for (int cx = 0; cx < n; cx++)
            {
                int i = cz * n + cx;
                float noise = ValueNoise.Fbm(cfg.Seed + 67UL, (cx + 0.5f) * cs / 40f, (cz + 0.5f) * cs / 40f, 2);
                float b = Capacity[i] * MathUtil.Clamp01(cfg.InitialVegetation * (0.7f + 0.6f * noise));
                Biomass[i] = b;
                total += b;
            }
        }
        InitialTotal = total;
        LastTotal = total;
        TotalGrown = TotalConsumed = TotalDieback = 0;
    }

    /// <summary>뜯어 먹을 수 있는 양(kg). 잔여분은 남긴다.</summary>
    public float EdibleAt(int cell, PlantDefinition plant)
    {
        float e = Biomass[cell] - plant.ResidualFraction * Capacity[cell];
        return e > 0f ? e : 0f;
    }

    /// <summary>wantKg만큼 먹으려 시도하고 실제로 줄어든 양을 돌려준다. 음수가 되지 않는다.</summary>
    public float Consume(int cell, float wantKg, PlantDefinition plant)
    {
        if (!(wantKg > 0f)) return 0f;
        float edible = EdibleAt(cell, plant);
        float take = wantKg < edible ? wantKg : edible;
        if (take <= 0f) return 0f;
        float before = Biomass[cell];
        float after = before - take;
        if (after < 0f) after = 0f;
        Biomass[cell] = after;
        float actual = before - after;
        TotalConsumed += actual;
        return actual;
    }

    /// <summary>daysDt(게임 내 일수)만큼 성장·고사를 진행한다.</summary>
    public void Update(TerrainGrid terrain, BiomeRegistry biomes, PlantDefinition plant, WorldConfig cfg, in EnvironmentState env, float daysDt)
    {
        float temp = env.TemperatureC;
        float tempFactor;
        if (temp <= plant.MinTemperatureC || temp >= plant.MaxTemperatureC) tempFactor = 0f;
        else if (temp < plant.OptimalLowC) tempFactor = (temp - plant.MinTemperatureC) / (plant.OptimalLowC - plant.MinTemperatureC);
        else if (temp > plant.OptimalHighC) tempFactor = (plant.MaxTemperatureC - temp) / (plant.MaxTemperatureC - plant.OptimalHighC);
        else tempFactor = 1f;

        float baseRate = plant.GrowthRatePerDay * env.Light * tempFactor * cfg.VegetationGrowthScale;
        float moistSpan = MathF.Max(1e-3f, plant.OptimalMoisture - plant.MinMoisture);

        double grown = 0, died = 0, total = 0, used = 0, leached = 0;
        float leachFraction = MathF.Min(1f, NutrientLeachPerDay * daysDt);
        for (int i = 0; i < Biomass.Length; i++)
        {
            float k = Capacity[i];
            float n = Nutrient[i];
            if (n > 0f)
            {
                float lost = n * leachFraction;
                n -= lost;
                leached += lost;
            }
            if (k <= 0f)
            {
                Nutrient[i] = n;
                continue;
            }
            float b = Biomass[i];
            float m = MathUtil.Clamp01((terrain.Moisture[i] - plant.MinMoisture) / moistSpan);

            float growth = 0f;
            if (b < k && baseRate > 0f)
            {
                float rate = baseRate * m * biomes[terrain.Biome[i]].GrowthFactor;
                growth = rate * (b + plant.SeedFraction * k) * (1f - b / k) * daysDt;
                if (growth > k - b) growth = k - b;

                // 토양 영양이 있으면 더 빨리 자라고, 늘어난 만큼 영양을 쓴다.
                if (n > 0f && growth > 0f)
                {
                    float boost = NutrientMaxBoost * n / (n + NutrientHalfSaturationKg);
                    float extra = MathF.Min(growth * boost, k - b - growth);
                    float need = extra * NutrientPerExtraGrowth;
                    if (need > n)
                    {
                        extra = n / NutrientPerExtraGrowth;
                        need = n;
                    }
                    if (extra > 0f)
                    {
                        growth += extra;
                        n -= need;
                        used += need;
                    }
                }
            }
            Nutrient[i] = n < 0f ? 0f : n;

            // 고사: 수분이나 온도 조건이 최적의 35% 아래로 떨어진 만큼 비례해서 줄어든다.
            float worst = m < tempFactor ? m : tempFactor;
            float dieback = 0f;
            if (worst < StressThreshold)
            {
                float stress = (StressThreshold - worst) / StressThreshold;
                dieback = plant.DiebackRatePerDay * stress * b * daysDt;
                if (dieback > b) dieback = b;
            }

            float nb = b + growth - dieback;
            if (nb < 0f) nb = 0f;
            Biomass[i] = nb;
            grown += growth;
            died += dieback;
            total += nb;
        }
        TotalGrown += grown;
        TotalDieback += died;
        TotalNutrientUsed += used;
        TotalNutrientLeached += leached;
        LastTotal = total;
    }

    public double SumBiomass()
    {
        double total = 0;
        for (int i = 0; i < Biomass.Length; i++) total += Biomass[i];
        return total;
    }

    public double SumCapacity()
    {
        double total = 0;
        for (int i = 0; i < Capacity.Length; i++) total += Capacity[i];
        return total;
    }

    public void AddToHash(ref StateHasher h)
    {
        h.Add(Biomass.AsSpan());
        h.Add(Nutrient.AsSpan());
        h.Add(TotalGrown); h.Add(TotalConsumed); h.Add(TotalDieback); h.Add(InitialTotal);
        h.Add(TotalNutrientAdded); h.Add(TotalNutrientUsed); h.Add(TotalNutrientLeached);
    }
}
