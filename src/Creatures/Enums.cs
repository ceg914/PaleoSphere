namespace PaleoSphere.Creatures;

public enum Diet : byte
{
    Herbivore = 0,
    Carnivore = 1,
    Omnivore = 2,
}

/// <summary>Utility AI가 고르는 행동.</summary>
public enum BehaviorKind : byte
{
    Idle = 0,
    Forage = 1,
    Drink = 2,
    Rest = 3,
    Flee = 4,
    Explore = 5,

    /// <summary>육식동물의 사냥(사냥감 찾기·추격·공격).</summary>
    Hunt = 6,
}

/// <summary>지금 몸이 하고 있는 동작. 3D 표시와 대사량 계산에 쓴다.</summary>
public enum ActivityKind : byte
{
    Stand = 0,
    Walk = 1,
    Run = 2,
    Eat = 3,
    Drink = 4,
    Rest = 5,

    /// <summary>사냥감을 공격하는 중(제자리에서 덤빔).</summary>
    Attack = 6,
}

public enum DeathCause : byte
{
    None = 0,
    Starvation = 1,
    Dehydration = 2,
    Predation = 3,
    OldAge = 4,
    Removed = 5,
}

public enum LifeStage : byte
{
    Juvenile = 0,
    Subadult = 1,
    Adult = 2,
    Senior = 3,
}

/// <summary>성별. 첫 버전은 번식이 없어 Unknown만 쓴다.</summary>
public enum Sex : byte
{
    Unknown = 0,
    Female = 1,
    Male = 2,
}

/// <summary>행동을 고른 이유.</summary>
public enum DecisionReason : byte
{
    HighestScore = 0,
    SafetyDehydration = 1,
    SafetyStarvation = 2,
    SafetyThreat = 3,
}

/// <summary>행동이 끝난 결과.</summary>
public enum BehaviorResult : byte
{
    Ongoing = 0,
    Completed = 1,
    Interrupted = 2,
    TargetBlocked = 3,
    NoFoodFound = 4,
    NoWaterReachable = 5,

    /// <summary>사냥: 지각 범위에 사냥감이 없다.</summary>
    NoPreyFound = 6,

    /// <summary>사냥: 추격 시간이 다하거나 지쳐서 포기했다.</summary>
    ChaseFailed = 7,

    /// <summary>사냥: 사냥감을 잡았다.</summary>
    Kill = 8,
}

public static class BehaviorKinds
{
    public const int Count = 7;

    public static string ToId(BehaviorKind b) => b switch
    {
        BehaviorKind.Idle => "idle",
        BehaviorKind.Forage => "forage",
        BehaviorKind.Drink => "drink",
        BehaviorKind.Rest => "rest",
        BehaviorKind.Flee => "flee",
        BehaviorKind.Explore => "explore",
        BehaviorKind.Hunt => "hunt",
        _ => "idle",
    };

    public static string ToId(DeathCause c) => c switch
    {
        DeathCause.Starvation => "starvation",
        DeathCause.Dehydration => "dehydration",
        DeathCause.Predation => "predation",
        DeathCause.OldAge => "old_age",
        DeathCause.Removed => "removed",
        _ => "none",
    };

    public const int DeathCauseCount = 6;
}
