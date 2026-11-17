using System;
using PaleoSphere.Core;
using PaleoSphere.Creatures;
using PaleoSphere.Ecology;
using PaleoSphere.Simulation;

namespace PaleoSphere.AI;

/// <summary>
/// 고른 행동을 실제 동작으로 옮기는 상태 머신. 섭식·음수의 자원 변화도 여기서 일어난다.
/// 생리 갱신 주기마다 한 번 호출된다.
/// </summary>
public static class BehaviorExecutor
{
    public const float ArriveDistance = 1.5f;
    public const int ForageRetargetTicks = 100;
    public const int ExploreRetargetTicks = 600;
    public const int RelocateRetargetTicks = 900;

    /// <summary>먹기 시작하는 기준: 닿는 범위의 먹이가 한 번 뜯는 양의 몇 배인지.</summary>
    public const float GoodPatchFactor = 8f;
    public const float StarvingEnergy = 0.25f;

    // --- 사냥 (게임적 단순화) ---
    /// <summary>공격 사이 간격(틱). 0.1초 단위.</summary>
    public const int AttackIntervalTicks = 15;

    /// <summary>추격을 포기한 뒤 다시 사냥에 나서기까지(틱).</summary>
    public const int HuntCooldownTicks = 1200;

    /// <summary>이 거리 안에서는 달려들고, 밖에서는 걸어서 다가간다(m).</summary>
    public const float ChargeDistanceM = 45f;

    /// <summary>이 피로를 넘으면 추격을 포기한다.</summary>
    public const float ChaseFatigueLimit = 0.85f;

    /// <summary>사체에 다가가는 데 이보다 오래 걸리면 그 사체를 포기한다(틱).</summary>
    public const int ScavengeApproachTicks = 1200;

    /// <summary>공격 실패 한 번에 사냥감이 입는 상처(건강 감소량의 평균).</summary>
    public const float WoundPerFailedAttack = 0.12f;

    /// <summary>행동을 바꾸고 로그를 남긴다. 같은 행동이면 아무것도 하지 않는다.</summary>
    public static void SwitchBehavior(Creature c, BehaviorKind next, long tick)
    {
        if (c.Behavior == next && c.Log.Count > 0) return;

        if (c.Log.Count > 0)
        {
            BehaviorLogEntry last = c.Log[^1];
            if (last.Result == BehaviorResult.Ongoing)
                last.Result = UtilityBrain.IsCompleted(c) ? BehaviorResult.Completed : BehaviorResult.Interrupted;
            last.ResultAmount = c.BoutAmount;
            last.EndTick = tick;
            c.Log[^1] = last;
        }

        c.Behavior = next;
        c.BehaviorStartTick = tick;
        c.BoutAmount = 0f;
        c.HasTarget = false;
        c.NextRetargetTick = 0;
        c.StuckTicks = 0;
        c.TargetEntityId = -1;
        c.ChaseStartTick = -1;

        c.Log.Add(new BehaviorLogEntry
        {
            Tick = tick,
            Behavior = next,
            Reason = c.LastReason,
            Score = c.LastScores[(int)next],
            Energy = c.Energy,
            Hydration = c.Hydration,
            Fatigue = c.Fatigue,
            Fear = c.Fear,
            Result = BehaviorResult.Ongoing,
            EndTick = -1,
        });
        if (c.Log.Count > Creature.MaxLogEntries) c.Log.RemoveAt(0);
    }

    private static void NoteIssue(Creature c, BehaviorResult issue)
    {
        if (c.Log.Count == 0) return;
        BehaviorLogEntry last = c.Log[^1];
        last.Result = issue;
        c.Log[^1] = last;
    }

    private static void ClearIssue(Creature c)
    {
        if (c.Log.Count == 0) return;
        BehaviorLogEntry last = c.Log[^1];
        if (last.Result == BehaviorResult.Ongoing) return;
        last.Result = BehaviorResult.Ongoing;
        c.Log[^1] = last;
    }

    public static void Update(Creature c, SpeciesDefinition sp, WorldState w, float hoursDt)
    {
        long tick = w.Clock.Tick;
        var terrain = w.Terrain;
        int cell = terrain.CellIndexAt(c.Position);

        if (c.HasTarget && Vec2.DistanceSquared(c.Position, c.Target) < ArriveDistance * ArriveDistance)
            c.HasTarget = false;

        switch (c.Behavior)
        {
            case BehaviorKind.Rest:
                c.HasTarget = false;
                c.Activity = ActivityKind.Rest;
                c.BoutAmount += hoursDt;
                break;

            case BehaviorKind.Drink:
                c.HasTarget = false;
                if (terrain.Drinkable[cell])
                {
                    c.Activity = ActivityKind.Drink;
                    float before = c.Hydration;
                    c.Hydration = MathUtil.Clamp01(c.Hydration + hoursDt / sp.DrinkHoursToFull);
                    c.BoutAmount += c.Hydration - before;
                    ClearIssue(c);
                }
                else if (!float.IsPositiveInfinity(terrain.WaterPathDistance[cell]))
                {
                    c.Activity = ActivityKind.Walk;
                }
                else
                {
                    c.Activity = ActivityKind.Stand;
                    NoteIssue(c, BehaviorResult.NoWaterReachable);
                }
                break;

            case BehaviorKind.Forage:
                if (sp.Diet == Diet.Carnivore) UpdateScavenge(c, sp, w, hoursDt, tick);
                else UpdateForage(c, sp, w, cell, hoursDt, tick);
                break;

            case BehaviorKind.Hunt:
                UpdateHunt(c, sp, w, tick);
                break;

            case BehaviorKind.Explore:
                if (!c.HasTarget || tick >= c.NextRetargetTick)
                {
                    c.HasTarget = Perception.TryPickWanderTarget(c, w, 20f, 70f, out c.Target);
                    c.NextRetargetTick = tick + ExploreRetargetTicks;
                }
                c.Activity = c.HasTarget ? ActivityKind.Walk : ActivityKind.Stand;
                break;

            case BehaviorKind.Flee:
            {
                Vec2 away = c.Position - c.ThreatPosition;
                Vec2 dir = away.LengthSquared > 1e-4f ? away.Normalized() : Vec2.FromHeading(c.Heading);
                float margin = terrain.CellSize * 2f;
                float size = terrain.WorldSize;
                Vec2 p = c.Position + dir * 40f;
                c.Target = new Vec2(MathUtil.Clamp(p.X, margin, size - margin), MathUtil.Clamp(p.Z, margin, size - margin));
                c.HasTarget = true;
                c.Activity = ActivityKind.Run;
                break;
            }

            default:
                c.HasTarget = false;
                c.Activity = ActivityKind.Stand;
                break;
        }
    }

    // ---------------------------------------------------------------- 사냥

    /// <summary>
    /// 사냥: 사냥감을 고르고, 멀면 걸어서 다가가다가 가까워지면 달려들어 공격한다.
    /// 추격 시간이 다하거나 지치면 포기하고 한동안 사냥 의욕이 줄어든다.
    /// </summary>
    private static void UpdateHunt(Creature c, SpeciesDefinition sp, WorldState w, long tick)
    {
        Creature? prey = null;
        if (c.TargetEntityId >= 0 && w.CreaturesById.TryGetValue(c.TargetEntityId, out Creature? current) && current.Alive
            && Vec2.Distance(c.Position, current.Position) <= sp.PerceptionRadiusM * 1.5f)
        {
            prey = current;
        }

        if (prey == null)
        {
            c.TargetEntityId = -1;
            c.ChaseStartTick = -1;
            if (tick >= c.NextRetargetTick || !c.HasTarget)
            {
                prey = Perception.FindPrey(c, sp, w);
                c.NextRetargetTick = tick + ForageRetargetTicks;
            }
            if (prey == null)
            {
                // 보이는 사냥감이 없으면 넓게 돌아다니며 찾는다.
                if (!c.HasTarget)
                {
                    c.HasTarget = Perception.TryPickWanderTarget(c, w, 40f, 120f, out c.Target);
                    c.NextRetargetTick = tick + ExploreRetargetTicks;
                }
                c.Activity = c.HasTarget ? ActivityKind.Walk : ActivityKind.Stand;
                NoteIssue(c, BehaviorResult.NoPreyFound);
                return;
            }
            c.TargetEntityId = prey.Id;
            ClearIssue(c);
        }

        SpeciesDefinition preySp = w.Species[prey.SpeciesIndex];
        float dist = Vec2.Distance(c.Position, prey.Position);
        float reach = (sp.BodyRadiusM * c.TraitBodySize + preySp.BodyRadiusM * prey.TraitBodySize) * 1.15f + 1f;
        c.Target = prey.Position;
        c.HasTarget = true;

        if (dist <= reach)
        {
            c.Activity = ActivityKind.Attack;
            if (tick >= c.NextAttackTick)
            {
                c.NextAttackTick = tick + AttackIntervalTicks;
                ResolveAttack(c, sp, prey, preySp, w, tick);
            }
            return;
        }

        if (dist <= ChargeDistanceM)
        {
            if (c.ChaseStartTick < 0) c.ChaseStartTick = tick;
            long chaseLimit = (long)(sp.MaxChaseSeconds / SimClock.TickSeconds);
            if (tick - c.ChaseStartTick > chaseLimit || c.Fatigue > ChaseFatigueLimit)
            {
                GiveUpHunt(c, tick);
                NoteIssue(c, BehaviorResult.ChaseFailed);
                return;
            }
            c.Activity = ActivityKind.Run;
        }
        else
        {
            c.Activity = ActivityKind.Walk;
        }
    }

    private static void GiveUpHunt(Creature c, long tick)
    {
        c.TargetEntityId = -1;
        c.ChaseStartTick = -1;
        c.HasTarget = false;
        c.HuntCooldownUntilTick = tick + HuntCooldownTicks;
        c.Activity = ActivityKind.Stand;
    }

    /// <summary>
    /// 공격 한 번. 성공 확률 = 기본 확률 × 사냥감의 약함 × 몸집 비 × (1 − 방어력/2).
    /// 실패하면 사냥감이 다치고, 방어력이 있는 사냥감은 반격해 공격자를 다치게 할 수 있다.
    /// 난수는 공격자의 난수 흐름만 쓴다.
    /// </summary>
    public static void ResolveAttack(Creature c, SpeciesDefinition sp, Creature prey, SpeciesDefinition preySp, WorldState w, long tick)
    {
        float vulnerability = 1.6f - prey.Health;
        if (prey.Activity == ActivityKind.Run) vulnerability *= 0.75f;
        if (prey.Fatigue > 0.7f) vulnerability *= 1.3f;
        float massRatio = sp.BodyMassKg * c.TraitBodySize / MathF.Max(1f, preySp.BodyMassKg * prey.TraitBodySize);
        float size = MathUtil.Clamp(MathF.Sqrt(massRatio), 0.5f, 1.5f);
        float chance = MathUtil.Clamp01(sp.AttackSuccess * vulnerability * size * (1f - 0.5f * preySp.Defense));
        c.Fatigue = MathUtil.Clamp01(c.Fatigue + 0.02f);

        if (c.Rng.NextFloat() < chance)
        {
            Kill(c, prey, w);
            return;
        }

        prey.Health -= WoundPerFailedAttack * (0.5f + c.Rng.NextFloat());
        prey.Fear = 1f;
        prey.ThreatPosition = c.Position;
        prey.ThreatUntilTick = tick + 300;
        if (c.Rng.NextFloat() < preySp.Defense)
        {
            // 반격: 공격자를 다치게 하지만 바로 죽이지는 않는다(약해진 포식자는 굶주림으로 이어질 수 있다).
            c.Health = MathF.Max(0.05f, c.Health - 0.25f * preySp.Defense);
        }
        if (prey.Health <= 0f) Kill(c, prey, w);
    }

    private static void Kill(Creature c, Creature prey, WorldState w)
    {
        w.DeathsPending = true;
        prey.Health = 0f;
        prey.Alive = false;
        prey.DeathCause = DeathCause.Predation;
        prey.KilledBy = c.Id;
        c.Kills++;
        c.BoutAmount += 1f;
        c.TargetEntityId = -1;
        c.ChaseStartTick = -1;
        c.HasTarget = false;
        c.Activity = ActivityKind.Stand;
        NoteIssue(c, BehaviorResult.Kill);
    }

    /// <summary>육식동물의 섭식: 냄새로 사체를 찾아가 고기를 먹는다.</summary>
    private static void UpdateScavenge(Creature c, SpeciesDefinition sp, WorldState w, float hoursDt, long tick)
    {
        Carcass? target = c.TargetEntityId >= 0 ? w.FindCarcass(c.TargetEntityId) : null;
        if (target != null && target.MeatKg < Perception.MinMeatKg) target = null;
        if (target == null || tick >= c.NextRetargetTick)
        {
            target = Perception.FindCarcass(c, sp, w);
            int id = target?.Id ?? -1;
            // 새 사체로 바꾸면 다가가기 시작한 시각을 다시 잰다(ChaseStartTick을 빌려 쓴다).
            if (id != c.TargetEntityId) c.ChaseStartTick = tick;
            c.TargetEntityId = id;
            c.NextRetargetTick = tick + ForageRetargetTicks;
        }
        if (target == null)
        {
            if (!c.HasTarget) c.HasTarget = Perception.TryPickWanderTarget(c, w, 30f, 80f, out c.Target);
            c.Activity = c.HasTarget ? ActivityKind.Walk : ActivityKind.Stand;
            NoteIssue(c, BehaviorResult.NoFoodFound);
            return;
        }

        float reach = sp.BodyRadiusM * c.TraitBodySize + w.Species[target.SpeciesIndex].BodyRadiusM + CarcassRules.FeedReachM;
        if (Vec2.Distance(c.Position, target.Position) > reach)
        {
            if (c.ChaseStartTick >= 0 && tick - c.ChaseStartTick > ScavengeApproachTicks)
            {
                // 오래 걸려도 닿지 못하면 길이 막힌 것으로 보고 이 사체를 포기한다.
                c.IgnoredCarcassId = target.Id;
                c.TargetEntityId = -1;
                c.ChaseStartTick = -1;
                c.HasTarget = false;
                c.Activity = ActivityKind.Stand;
                NoteIssue(c, BehaviorResult.TargetBlocked);
                return;
            }
            c.Target = target.Position;
            c.HasTarget = true;
            c.Activity = ActivityKind.Walk;
            return;
        }
        c.ChaseStartTick = tick;

        float want = sp.IntakeKgPerHour * hoursDt * c.TraitBodySize;
        float take = MathF.Min(want, target.MeatKg);
        target.MeatKg -= take;
        target.RemainsKg -= take;
        w.CarcassMassEaten += take;
        c.Energy = MathUtil.Clamp01(c.Energy + take * sp.EnergyPerKg / c.TraitBodySize);
        c.BoutAmount += take;
        // 먹는 동안 사체 쪽을 바라보게 목표는 남겨 둔다(이동은 하지 않는다).
        c.Target = target.Position;
        c.HasTarget = false;
        c.Activity = ActivityKind.Eat;
        ClearIssue(c);
    }

    /// <summary>입이 닿는 범위(지금 셀과 둘레 여덟 셀)에서 먹을 수 있는 양(kg).</summary>
    private static float ReachableForage(WorldState w, int cell)
    {
        int n = w.Terrain.Size;
        int cx = cell % n, cz = cell / n;
        float total = 0f;
        for (int dz = -1; dz <= 1; dz++)
        {
            int z = cz + dz;
            if (z < 0 || z >= n) continue;
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = cx + dx;
                if (x < 0 || x >= n) continue;
                total += w.Vegetation.EdibleAt(z * n + x, w.Forage);
            }
        }
        return total;
    }

    /// <summary>닿는 범위에서 wantKg까지 뜯어 먹고 실제로 먹은 양을 돌려준다. 지금 셀부터 고정된 순서로 먹는다.</summary>
    private static float ConsumeReachable(WorldState w, int cell, float wantKg)
    {
        int n = w.Terrain.Size;
        int cx = cell % n, cz = cell / n;
        float got = w.Vegetation.Consume(cell, wantKg, w.Forage);
        for (int dz = -1; dz <= 1 && got < wantKg; dz++)
        {
            int z = cz + dz;
            if (z < 0 || z >= n) continue;
            for (int dx = -1; dx <= 1 && got < wantKg; dx++)
            {
                int x = cx + dx;
                if (x < 0 || x >= n || (dx == 0 && dz == 0)) continue;
                got += w.Vegetation.Consume(z * n + x, wantKg - got, w.Forage);
            }
        }
        return got;
    }

    private static void UpdateForage(Creature c, SpeciesDefinition sp, WorldState w, int cell, float hoursDt, long tick)
    {
        float want = sp.IntakeKgPerHour * hoursDt * c.TraitBodySize;
        float reachable = ReachableForage(w, cell);

        // 넉넉한 자리에서만 먹기 시작하고, 먹던 중이면 거의 바닥날 때까지 계속한다.
        // 굶주림이 심하면 기준을 낮춰 부스러기라도 먹는다.
        bool eating = c.Activity == ActivityKind.Eat;
        float goodCell = Perception.GoodCellKg(w, want);
        float startKg = MathF.Min(want * GoodPatchFactor, goodCell * 8f);
        if (c.Energy < StarvingEnergy) startKg = MathF.Min(startKg, want * 3f);
        float threshold = eating ? MathF.Min(want, startKg * 0.5f) : startKg;

        if (reachable >= threshold && reachable > 0f)
        {
            float got = ConsumeReachable(w, cell, want);
            c.Energy = MathUtil.Clamp01(c.Energy + got * sp.EnergyPerKg / c.TraitBodySize);
            c.BoutAmount += got;
            c.HasTarget = false;
            c.Activity = ActivityKind.Eat;
            ClearIssue(c);
            return;
        }

        if (!c.HasTarget || tick >= c.NextRetargetTick)
        {
            bool found = Perception.FindForageTarget(c, sp, w, goodCell * 0.5f, out Vec2 target, out float bestEdible);
            if (found && bestEdible >= goodCell)
            {
                c.Target = target;
                c.HasTarget = true;
                c.NextRetargetTick = tick + ForageRetargetTicks;
                ClearIssue(c);
            }
            else if (Perception.TryPickRelocationTarget(c, w, 60f, 140f, out c.Target))
            {
                // 주변이 빈약하면 부스러기를 찾아다니지 않고 멀리 옮겨 간다.
                c.HasTarget = true;
                c.NextRetargetTick = tick + RelocateRetargetTicks;
                NoteIssue(c, BehaviorResult.NoFoodFound);
            }
            else
            {
                c.Target = target;
                c.HasTarget = found;
                c.NextRetargetTick = tick + ForageRetargetTicks;
                NoteIssue(c, BehaviorResult.NoFoodFound);
            }
        }
        c.Activity = c.HasTarget ? ActivityKind.Walk : ActivityKind.Stand;
    }

    /// <summary>이동이 계속 막혔을 때 호출. 목표를 버리고 다음 갱신에서 다시 고르게 한다.</summary>
    public static void OnBlocked(Creature c)
    {
        c.HasTarget = false;
        c.NextRetargetTick = 0;
        c.StuckTicks = 0;
        NoteIssue(c, BehaviorResult.TargetBlocked);
    }
}
