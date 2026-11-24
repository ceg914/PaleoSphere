using PaleoSphere.Core;
using PaleoSphere.World;

namespace PaleoSphere.Creatures;

/// <summary>
/// 에너지·수분·피로·건강의 시간 변화. 모든 계수는 게임적 단순화이며 실제 공룡의 생리 수치가 아니다.
/// </summary>
public static class Physiology
{
    /// <summary>동작별 에너지 소모 배수(서 있을 때 = 1).</summary>
    public static float ActivityEnergyFactor(ActivityKind a) => a switch
    {
        ActivityKind.Rest => 0.6f,
        ActivityKind.Walk => 1.35f,
        ActivityKind.Run => 3.0f,
        _ => 1.0f,
    };

    public static float ActivityFatigueFactor(ActivityKind a) => a switch
    {
        ActivityKind.Walk => 1.2f,
        ActivityKind.Run => 3.0f,
        _ => 1.0f,
    };

    /// <summary>
    /// hoursDt(게임 내 시간)만큼 생리 상태를 진행한다. 건강이 0 이하가 되면 Alive를 내리고 사망 원인을 정한다.
    /// </summary>
    public static void Update(Creature c, SpeciesDefinition sp, in EnvironmentState env, float hoursDt)
    {
        float daysDt = hoursDt / 24f;
        float act = ActivityEnergyFactor(c.Activity);

        c.Energy -= daysDt / sp.EnergyReserveDays * c.TraitMetabolism * act;
        if (c.Energy < 0f) c.Energy = 0f;

        float heat = 1f + (env.TemperatureC > 25f ? (env.TemperatureC - 25f) * 0.04f : 0f);
        c.Hydration -= daysDt / sp.HydrationReserveDays * heat * (0.7f + 0.3f * act);
        if (c.Hydration < 0f) c.Hydration = 0f;

        if (c.Activity == ActivityKind.Rest)
            c.Fatigue -= hoursDt / sp.RestHours;
        else
            c.Fatigue += hoursDt / sp.AwakeHours * ActivityFatigueFactor(c.Activity);
        c.Fatigue = MathUtil.Clamp01(c.Fatigue);

        if (c.Energy <= 0f)
        {
            float dmg = daysDt / sp.StarveHealthDays;
            c.Health -= dmg;
            c.StarveDamage += dmg;
        }
        if (c.Hydration <= 0f)
        {
            float dmg = daysDt / sp.DehydrationHealthDays;
            c.Health -= dmg;
            c.DehydrationDamage += dmg;
        }
        if (c.Energy > 0.4f && c.Hydration > 0.4f && c.Health < 1f)
        {
            c.Health += sp.HealthRegenPerDay * LifeHistory.RegenFactor(c, sp) * daysDt;
            if (c.Health > 1f) c.Health = 1f;
        }

        c.AgeDays += daysDt;
        c.LifeStage = LifeHistory.StageOf(c, sp);

        if (c.Health <= 0f)
        {
            c.Health = 0f;
            c.Alive = false;
            c.DeathCause = c.DehydrationDamage > c.StarveDamage ? DeathCause.Dehydration : DeathCause.Starvation;
        }
        else if (c.LifespanDays > 0f && c.AgeDays >= c.LifespanDays)
        {
            c.Health = 0f;
            c.Alive = false;
            c.DeathCause = DeathCause.OldAge;
        }
    }
}
