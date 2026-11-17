using PaleoSphere.Core;
using PaleoSphere.Creatures;

namespace PaleoSphere.Ecology;

/// <summary>
/// 사체. 죽은 개체가 남기고, 육식동물이 고기를 먹으며, 남은 몸은 분해되어 토양 영양으로 돌아간다.
/// id는 죽은 개체의 id를 그대로 쓴다(개체 id는 다시 쓰지 않으므로 겹치지 않는다).
/// </summary>
public sealed class Carcass
{
    public int Id;
    public int SpeciesIndex;
    public Vec2 Position;
    public float Heading;
    public DeathCause Cause;
    public long DiedTick;

    /// <summary>먹을 수 있는 고기(kg). 시간이 지나면 상해서 줄어든다.</summary>
    public float MeatKg;
    public float InitialMeatKg;

    /// <summary>아직 분해되지 않은 몸 전체(kg). 고기를 포함한다.</summary>
    public float RemainsKg;
    public float InitialMassKg;

    /// <summary>남은 정도(0~1). 화면에서 사체가 가라앉는 정도로 쓴다.</summary>
    public float RemainingFraction => InitialMassKg > 0f ? RemainsKg / InitialMassKg : 0f;

    public void AddToHash(ref StateHasher h)
    {
        h.Add(Id); h.Add(SpeciesIndex); h.Add(Position); h.Add(Heading); h.Add((int)Cause); h.Add(DiedTick);
        h.Add(MeatKg); h.Add(InitialMeatKg); h.Add(RemainsKg); h.Add(InitialMassKg);
    }
}

/// <summary>
/// 사체 규칙. 수치는 모두 게임적 단순화다(실제 부패 속도나 영양 순환을 재현하지 않는다).
/// </summary>
public static class CarcassRules
{
    /// <summary>몸무게 가운데 먹을 수 있는 고기의 비율.</summary>
    public const float EdibleFraction = 0.5f;

    /// <summary>고기가 상해서 먹을 수 없게 되는 비율(/일).</summary>
    public const float SpoilRatePerDay = 0.1f;

    /// <summary>남은 몸이 분해되는 비율(/일).</summary>
    public const float DecomposeRatePerDay = 0.12f;

    /// <summary>분해된 질량 가운데 토양 영양으로 돌아가는 비율.</summary>
    public const float NutrientFraction = 0.01f;

    /// <summary>처음 질량의 이 비율 아래로 남으면 사체를 지운다.</summary>
    public const float RemoveBelowFraction = 0.03f;

    /// <summary>입이 닿는 거리(몸 반지름에 더한다, m).</summary>
    public const float FeedReachM = 2.5f;
}
