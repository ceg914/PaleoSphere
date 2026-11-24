using System.Collections.Generic;
using PaleoSphere.Core;

namespace PaleoSphere.Creatures;

/// <summary>행동 로그 한 줄: 무엇을 왜 골랐고, 그때 상태가 어땠고, 어떻게 끝났는지.</summary>
public struct BehaviorLogEntry
{
    public long Tick;
    public BehaviorKind Behavior;
    public DecisionReason Reason;
    public float Score;
    public float Energy;
    public float Hydration;
    public float Fatigue;
    public float Fear;
    public BehaviorResult Result;

    /// <summary>결과 수치. 섭식은 먹은 양(kg), 음수는 회복한 수분(0~1), 휴식은 쉰 시간(시).</summary>
    public float ResultAmount;
    public long EndTick;
}

/// <summary>
/// 개체 한 마리의 권위 상태. 시뮬레이션 코어만 값을 바꾼다.
/// 필드는 성능과 저장 편의를 위해 공개하지만 표현 계층은 SimSnapshot으로만 읽는다.
/// </summary>
public sealed class Creature
{
    public const int MaxLogEntries = 16;

    public int Id;
    public int SpeciesIndex;

    public Vec2 Position;
    public Vec2 PrevPosition;
    public float Heading;
    public float PrevHeading;
    public float CurrentSpeed;

    public float AgeDays;
    public Sex Sex;
    public LifeStage LifeStage;

    /// <summary>이 개체의 수명(일). 태어날 때(배치될 때) 정해지고, 이 나이가 되면 노화로 죽는다.</summary>
    public float LifespanDays;

    // 0~1 척도.
    public float Health = 1f;
    public float Energy = 1f;
    public float Hydration = 1f;
    public float Fatigue;
    public float Fear;

    public BehaviorKind Behavior;
    public ActivityKind Activity;
    public long BehaviorStartTick;

    public bool HasTarget;
    public Vec2 Target;
    public int TargetEntityId = -1;
    public long NextRetargetTick;

    // 사냥 상태(육식동물).
    public long ChaseStartTick = -1;
    public long NextAttackTick;
    public long HuntCooldownUntilTick;
    public int Kills;

    /// <summary>사냥당해 죽었을 때 잡은 개체의 id. 아니면 -1.</summary>
    public int KilledBy = -1;

    /// <summary>다가가지 못해 포기한 사체(다시 고르지 않는다). 없으면 -1.</summary>
    public int IgnoredCarcassId = -1;

    public Vec2 ThreatPosition;
    public long ThreatUntilTick = -1;

    public float TraitBodySize = 1f;
    public float TraitMetabolism = 1f;
    public float TraitSpeed = 1f;

    public int ParentA = -1;
    public int ParentB = -1;

    public Pcg32 Rng;

    public float StarveDamage;
    public float DehydrationDamage;
    public int StuckTicks;
    public sbyte AvoidSide = 1;

    /// <summary>현재 행동에서 쌓인 결과 수치(먹은 양 등).</summary>
    public float BoutAmount;

    public bool Alive = true;
    public DeathCause DeathCause;

    // 마지막 의사결정 기록(검사창 표시용).
    public readonly float[] LastScores = new float[BehaviorKinds.Count];
    public DecisionReason LastReason;
    public long LastDecisionTick = -1;

    public readonly List<BehaviorLogEntry> Log = new();

    public void AddToHash(ref StateHasher h)
    {
        h.Add(Id); h.Add(SpeciesIndex);
        h.Add(Position); h.Add(PrevPosition); h.Add(Heading); h.Add(PrevHeading); h.Add(CurrentSpeed);
        h.Add(AgeDays); h.Add((int)Sex); h.Add((int)LifeStage); h.Add(LifespanDays);
        h.Add(Health); h.Add(Energy); h.Add(Hydration); h.Add(Fatigue); h.Add(Fear);
        h.Add((int)Behavior); h.Add((int)Activity); h.Add(BehaviorStartTick);
        h.Add(HasTarget); h.Add(Target); h.Add(TargetEntityId); h.Add(NextRetargetTick);
        h.Add(ChaseStartTick); h.Add(NextAttackTick); h.Add(HuntCooldownUntilTick); h.Add(Kills); h.Add(KilledBy); h.Add(IgnoredCarcassId);
        h.Add(ThreatPosition); h.Add(ThreatUntilTick);
        h.Add(TraitBodySize); h.Add(TraitMetabolism); h.Add(TraitSpeed);
        h.Add(ParentA); h.Add(ParentB);
        h.Add(Rng.State); h.Add(Rng.Inc);
        h.Add(StarveDamage); h.Add(DehydrationDamage); h.Add(StuckTicks); h.Add((int)AvoidSide);
        h.Add(BoutAmount); h.Add(Alive); h.Add((int)DeathCause);
        for (int i = 0; i < LastScores.Length; i++) h.Add(LastScores[i]);
        h.Add((int)LastReason); h.Add(LastDecisionTick);
    }
}
