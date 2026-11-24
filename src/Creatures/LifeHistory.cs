using System;
using PaleoSphere.Core;

namespace PaleoSphere.Creatures;

/// <summary>
/// 나이에 따른 몸집·생애 단계·노화. 모두 순수 함수라 저장할 파생 값이 없다.
///
/// 몸길이 비율 L(나이)은 갓 태어났을 때 hatch_length_fraction에서 시작해 성체 나이에 1이 된다.
/// L = L0 + (1 − L0) × t(2 − t), t = 나이 / 성체 나이. 길이는 처음에 빨리 늘고 점점 느려지며,
/// 몸무게(L³)로 보면 중간에 가장 빨리 느는 S자 곡선이 된다. 공룡의 몸무게 성장 곡선이 S자였다는 연구
/// (Erickson 외 2004)의 모양만 따른 것이고, 곡선의 수치는 게임적 단순화다.
/// </summary>
public static class LifeHistory
{
    /// <summary>노년에 접어든 개체의 속도 감소(수명 끝에서 최대).</summary>
    public const float SeniorMaxSlowdown = 0.25f;

    /// <summary>노년의 건강 회복 감소(수명 끝에서 회복 0).</summary>
    public const float SeniorRegenLoss = 1f;

    /// <summary>성체 몸길이에 대한 비율(0~1).</summary>
    public static float LengthFraction(SpeciesDefinition sp, float ageDays)
    {
        if (sp.AdultAgeDays <= 0f || ageDays >= sp.AdultAgeDays) return 1f;
        float t = ageDays <= 0f ? 0f : ageDays / sp.AdultAgeDays;
        float l0 = sp.HatchLengthFraction;
        return l0 + (1f - l0) * t * (2f - t);
    }

    /// <summary>몸 크기 배율(길이 기준): 나이에 따른 비율 × 체격 형질. 반지름·입이 닿는 거리·화면 크기에 쓴다.</summary>
    public static float BodyScale(Creature c, SpeciesDefinition sp) => LengthFraction(sp, c.AgeDays) * c.TraitBodySize;

    /// <summary>지금 몸무게(kg).</summary>
    public static float MassKg(Creature c, SpeciesDefinition sp)
    {
        float s = BodyScale(c, sp);
        return sp.BodyMassKg * s * s * s;
    }

    /// <summary>
    /// 먹이량 배율. 하루 먹이와 섭취 속도가 몸무게의 3/4 제곱에 비례한다고 본다(성체 = 체격 형질).
    /// L^2.25 = L² × L^(1/4). 네제곱근은 제곱근 두 번으로 구한다(기기마다 같은 결과).
    /// </summary>
    public static float FoodScale(Creature c, SpeciesDefinition sp)
    {
        float l = LengthFraction(sp, c.AgeDays);
        if (l >= 1f) return c.TraitBodySize;
        return c.TraitBodySize * l * l * MathF.Sqrt(MathF.Sqrt(l));
    }

    /// <summary>노년에 들어서는 나이(일).</summary>
    public static float SenescenceAgeDays(Creature c, SpeciesDefinition sp) => c.LifespanDays * sp.SenescenceFraction;

    /// <summary>노년이 얼마나 진행됐는지(0 = 노년 전, 1 = 수명 끝).</summary>
    public static float SeniorProgress(Creature c, SpeciesDefinition sp)
    {
        float start = SenescenceAgeDays(c, sp);
        float span = c.LifespanDays - start;
        if (c.AgeDays <= start || span <= 0f) return 0f;
        return MathUtil.Clamp01((c.AgeDays - start) / span);
    }

    public static LifeStage StageOf(Creature c, SpeciesDefinition sp)
    {
        if (c.AgeDays < sp.SubadultAgeDays) return LifeStage.Juvenile;
        if (c.AgeDays < sp.AdultAgeDays) return LifeStage.Subadult;
        if (c.AgeDays < SenescenceAgeDays(c, sp)) return LifeStage.Adult;
        return LifeStage.Senior;
    }

    /// <summary>
    /// 이동 속도 배율. 어린 개체는 다리가 짧아 느리고(갓 태어나면 성체의 65%), 노년에는 점점 느려진다.
    /// </summary>
    public static float SpeedFactor(Creature c, SpeciesDefinition sp)
    {
        float l = LengthFraction(sp, c.AgeDays);
        float young = 0.65f + 0.35f * l;
        return young * (1f - SeniorMaxSlowdown * SeniorProgress(c, sp));
    }

    /// <summary>건강 회복 배율. 노년에는 수명 끝으로 갈수록 0에 가까워진다.</summary>
    public static float RegenFactor(Creature c, SpeciesDefinition sp) => 1f - SeniorRegenLoss * SeniorProgress(c, sp);

    /// <summary>
    /// 개체의 수명(일)을 정한다. 최대 수명에서 lifespan_variation 안쪽으로 짧아진다.
    /// u는 개체 난수에서 뽑은 0~1 값.
    /// </summary>
    public static float RollLifespan(SpeciesDefinition sp, float u) => sp.MaxAgeDays * (1f - sp.LifespanVariation * u);
}
