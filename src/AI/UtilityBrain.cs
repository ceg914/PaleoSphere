using PaleoSphere.Creatures;
using PaleoSphere.Simulation;

namespace PaleoSphere.AI;

/// <summary>
/// 유틸리티 AI. 행동마다 점수를 계산해 가장 높은 것을 고르고, 점수와 이유를 개체에 남긴다.
/// 생존에 직결되는 조건은 점수와 별개인 안전 규칙으로 먼저 처리한다.
/// 점수 곡선과 임계값은 모두 게임적 단순화다(docs/SIMULATION_SPEC.md 참고).
/// </summary>
public static class UtilityBrain
{
    public const float SafetyFear = 0.75f;
    public const float SafetyHydration = 0.12f;
    public const float SafetyEnergy = 0.10f;

    // 하던 행동을 마칠 때까지 유지하는 기준(잦은 전환 방지).
    public const float ForageSatedEnergy = 0.92f;
    public const float DrinkSatedHydration = 0.92f;
    public const float RestedFatigue = 0.12f;

    /// <summary>육식동물은 에너지가 이보다 높으면 거의 사냥하지 않는다(배부른 포식자).</summary>
    public const float PredatorSatedEnergy = 0.55f;

    /// <summary>굶주린 포식자가 지쳐서 쉬기 시작하면 피로가 이 값 아래로 내려갈 때까지 쉰다.</summary>
    public const float ExhaustedRestUntil = 0.5f;

    // 동점일 때의 우선순위.
    private static readonly BehaviorKind[] Order =
    {
        BehaviorKind.Flee, BehaviorKind.Drink, BehaviorKind.Forage, BehaviorKind.Hunt, BehaviorKind.Rest, BehaviorKind.Explore, BehaviorKind.Idle,
    };

    /// <summary>
    /// c.LastScores, c.LastReason, c.LastDecisionTick을 채우고 고른 행동을 돌려준다.
    /// foodNearby: 초식동물은 먹을 식생, 육식동물은 고기가 남은 사체가 가까이 있는지.
    /// preyVisible: 육식동물의 지각 범위에 사냥감이 있는지.
    /// </summary>
    public static BehaviorKind Decide(Creature c, WorldState w, bool foodNearby, bool preyVisible = false)
    {
        SpeciesDefinition sp = w.Species[c.SpeciesIndex];
        bool carnivore = sp.Diet == Diet.Carnivore;
        var terrain = w.Terrain;
        int cell = terrain.CellIndexAt(c.Position);
        bool waterReachable = !float.IsPositiveInfinity(terrain.WaterPathDistance[cell]);
        bool threatActive = c.ThreatUntilTick >= w.Clock.Tick && c.Fear > 0.05f;
        bool night = w.Env.IsNight;

        float hunger = 1f - c.Energy;
        float thirst = 1f - c.Hydration;
        float fatigue = c.Fatigue;

        float[] s = c.LastScores;
        s[(int)BehaviorKind.Idle] = 0.10f;

        float appetite = 0.6f * hunger + 0.6f * hunger * hunger;
        if (carnivore)
        {
            // 육식동물의 섭식은 사체 먹기다. 사체가 없으면 사냥한다.
            s[(int)BehaviorKind.Forage] = foodNearby
                ? appetite + 0.15f + (c.Behavior == BehaviorKind.Forage && c.Energy < ForageSatedEnergy ? 0.20f : 0f)
                : 0f;
            float hunt = 0f;
            if (sp.IsPredator)
            {
                float hungerDrive = c.Energy < PredatorSatedEnergy ? 0.7f * hunger + 0.6f * hunger * hunger : 0.05f * hunger;
                hunt = hungerDrive * (preyVisible ? 1f : 0.55f);
                if (c.HuntCooldownUntilTick > w.Clock.Tick) hunt *= 0.3f;
                if (c.Behavior == BehaviorKind.Hunt && c.TargetEntityId >= 0) hunt += 0.25f;
            }
            s[(int)BehaviorKind.Hunt] = hunt;
        }
        else
        {
            s[(int)BehaviorKind.Forage] = appetite * (foodNearby ? 1f : 0.75f)
                + (c.Behavior == BehaviorKind.Forage && c.Energy < ForageSatedEnergy ? 0.20f : 0f);
            s[(int)BehaviorKind.Hunt] = 0f;
        }

        s[(int)BehaviorKind.Drink] = waterReachable
            ? 0.5f * thirst + 0.9f * thirst * thirst
              + (c.Behavior == BehaviorKind.Drink && c.Hydration < DrinkSatedHydration ? 0.25f : 0f)
            : 0f;

        s[(int)BehaviorKind.Rest] = terrain.IsWater(cell)
            ? 0f
            : 0.9f * fatigue * fatigue + (night ? 0.35f * (0.4f + fatigue) : 0f)
              + (c.Behavior == BehaviorKind.Rest && fatigue > RestedFatigue ? 0.25f : 0f);

        s[(int)BehaviorKind.Flee] = threatActive ? 2f * c.Fear : 0f;

        s[(int)BehaviorKind.Explore] = (0.12f + 0.08f * c.Energy * c.Hydration) * (night ? 0.5f : 1f);

        c.LastDecisionTick = w.Clock.Tick;

        if (threatActive && c.Fear >= SafetyFear)
        {
            c.LastReason = DecisionReason.SafetyThreat;
            return BehaviorKind.Flee;
        }
        if (c.Hydration <= SafetyHydration && waterReachable)
        {
            c.LastReason = DecisionReason.SafetyDehydration;
            return BehaviorKind.Drink;
        }
        if (c.Energy <= SafetyEnergy)
        {
            c.LastReason = DecisionReason.SafetyStarvation;
            if (!(carnivore && !foodNearby && sp.IsPredator)) return BehaviorKind.Forage;
            // 지쳐서 쫓을 수 없으면 먼저 쉰다. 쉬기 시작했으면 피로가 절반 아래로 내려갈 때까지 쉰다.
            // (이 규칙이 없으면 굶주린 포식자가 추격을 시작하자마자 포기하기를 되풀이하며 쉬지 못하고 굶어 죽는다.)
            bool exhausted = c.Fatigue > BehaviorExecutor.ChaseFatigueLimit
                || (c.Behavior == BehaviorKind.Rest && c.Fatigue > ExhaustedRestUntil);
            return exhausted ? BehaviorKind.Rest : BehaviorKind.Hunt;
        }

        BehaviorKind best = BehaviorKind.Idle;
        float bestScore = float.NegativeInfinity;
        foreach (BehaviorKind b in Order)
        {
            float score = s[(int)b];
            if (score > bestScore)
            {
                bestScore = score;
                best = b;
            }
        }
        c.LastReason = DecisionReason.HighestScore;
        return best;
    }

    /// <summary>행동을 바꾸는 시점에 이전 행동이 목적을 달성했는지.</summary>
    public static bool IsCompleted(Creature c)
    {
        return c.Behavior switch
        {
            BehaviorKind.Forage => c.Energy >= ForageSatedEnergy - 0.04f,
            BehaviorKind.Drink => c.Hydration >= DrinkSatedHydration - 0.04f,
            BehaviorKind.Rest => c.Fatigue <= RestedFatigue + 0.03f,
            BehaviorKind.Flee => c.Fear < 0.2f,
            // 사냥은 잡았을 때(Kill로 따로 기록) 말고는 완료가 아니다.
            BehaviorKind.Hunt => false,
            _ => true,
        };
    }
}
