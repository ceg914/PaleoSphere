using System;
using System.Collections.Generic;
using PaleoSphere.Core;
using PaleoSphere.Creatures;

namespace PaleoSphere.Simulation;

/// <summary>렌더러가 읽는 개체 한 마리의 복사본. 이전·현재 위치를 함께 담아 보간에 쓴다.</summary>
public struct CreatureSnapshot
{
    public int Id;
    public int SpeciesIndex;
    public Vec2 PrevPosition;
    public Vec2 Position;
    public float PrevHeading;
    public float Heading;
    public float Speed;
    public float BodySize;
    public float Health;
    public float Energy;
    public float Hydration;
    public ActivityKind Activity;
    public BehaviorKind Behavior;
}

/// <summary>사체 한 구의 복사본.</summary>
public struct CarcassSnapshot
{
    public int Id;
    public int SpeciesIndex;
    public Vec2 Position;
    public float Heading;
    public long DiedTick;

    /// <summary>남은 정도(0~1).</summary>
    public float Remaining;
    public float MeatKg;
}

/// <summary>
/// 표현 계층에 넘기는 읽기 전용 스냅샷. 코어 상태의 복사본이라 UI가 값을 바꿔도 시뮬레이션에 영향이 없다.
/// </summary>
public sealed class SimSnapshot
{
    public long Tick;
    public int Count;
    public CreatureSnapshot[] Creatures = Array.Empty<CreatureSnapshot>();
    public int CarcassCount;
    public CarcassSnapshot[] Carcasses = Array.Empty<CarcassSnapshot>();

    public bool TryFind(int id, out CreatureSnapshot snapshot)
    {
        for (int i = 0; i < Count; i++)
        {
            if (Creatures[i].Id != id) continue;
            snapshot = Creatures[i];
            return true;
        }
        snapshot = default;
        return false;
    }
}

/// <summary>검사창용 상세 정보(복사본).</summary>
public sealed class CreatureDetails
{
    public int Id;
    public int SpeciesIndex;
    public Vec2 Position;
    public float AgeDays;
    public Sex Sex;
    public LifeStage LifeStage;
    public float Health, Energy, Hydration, Fatigue, Fear;
    public BehaviorKind Behavior;
    public ActivityKind Activity;
    public long BehaviorStartTick;
    public float BoutAmount;
    public float TraitBodySize, TraitMetabolism, TraitSpeed;
    public int ParentA, ParentB;
    public float[] Scores = new float[BehaviorKinds.Count];
    public DecisionReason Reason;
    public long LastDecisionTick;

    /// <summary>쫓는 사냥감이나 찾아가는 사체의 id(-1이면 없음).</summary>
    public int TargetEntityId = -1;
    public int Kills;
    public List<BehaviorLogEntry> Log = new();
}
