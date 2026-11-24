using System.Collections.Generic;
using PaleoSphere.Core;
using PaleoSphere.Creatures;
using PaleoSphere.Ecology;
using PaleoSphere.Simulation;

namespace PaleoSphere.AI;

/// <summary>먹이 탐지. 주변 셀을 고리 모양으로 표본 조사한다(전체 탐색 대신 비용을 고정).</summary>
public static class Perception
{
    private static readonly float[] Rings = { 5f, 10f, 17f, 26f, 37f, 50f };
    private const int Directions = 8;

    /// <summary>
    /// '갈 만한 셀'의 기준(kg): 한 번 뜯는 양의 두 배와 평균 수용량의 30% 중 작은 쪽.
    /// 척박한 세계에서도 기준이 닿을 수 있는 수준으로 내려간다.
    /// </summary>
    public static float GoodCellKg(WorldState w, float biteKg) =>
        System.MathF.Min(biteKg * 2f, w.Vegetation.MeanLandCapacity * 0.3f);

    /// <summary>현재 위치나 가까운 곳에 먹을 만한 식생이 있는지(난수 사용 없음).</summary>
    public static bool IsFoodNearby(Creature c, WorldState w, float minEdibleKg)
    {
        var terrain = w.Terrain;
        if (w.Vegetation.EdibleAt(terrain.CellIndexAt(c.Position), w.Forage) >= minEdibleKg) return true;
        for (int ring = 1; ring <= 2; ring++)
        {
            float r = ring == 1 ? 8f : 20f;
            for (int k = 0; k < Directions; k++)
            {
                Vec2 p = c.Position + Vec2.FromHeading(k * MathUtil.TwoPi / Directions) * r;
                if (!terrain.IsWalkableAt(p)) continue;
                if (w.Vegetation.EdibleAt(terrain.CellIndexAt(p), w.Forage) < minEdibleKg) continue;
                if (terrain.IsLineWalkable(c.Position, p)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 지각 반경 안에서 '먹을 수 있는 양 ÷ 거리'가 가장 좋은 지점을 고른다.
    /// 개체마다 조사 방향을 난수로 돌려 모두가 같은 셀로 몰리지 않게 한다.
    /// </summary>
    public static bool FindForageTarget(Creature c, SpeciesDefinition sp, WorldState w, float minEdibleKg, out Vec2 target, out float bestEdible)
    {
        var terrain = w.Terrain;
        float rot = c.Rng.NextFloat() * (MathUtil.TwoPi / Directions);
        float bestScore = 0f;
        target = c.Position;
        bestEdible = 0f;
        bool found = false;
        for (int ri = 0; ri < Rings.Length; ri++)
        {
            float r = Rings[ri];
            if (r > sp.PerceptionRadiusM) break;
            for (int k = 0; k < Directions; k++)
            {
                float angle = rot + k * (MathUtil.TwoPi / Directions) + ri * 0.39f;
                Vec2 p = c.Position + Vec2.FromHeading(angle) * r;
                if (!terrain.IsWalkableAt(p)) continue;
                int cell = terrain.CellIndexAt(p);
                float edible = w.Vegetation.EdibleAt(cell, w.Forage);
                if (edible < minEdibleKg) continue;
                float score = edible / (1f + r / 15f);
                if (score > bestScore && terrain.IsLineWalkable(c.Position, p))
                {
                    bestScore = score;
                    bestEdible = edible;
                    target = terrain.CellCenter(cell);
                    found = true;
                }
            }
        }
        return found;
    }

    /// <summary>minDist~maxDist 떨어진 걸을 수 있는 임의 지점. 몇 번 시도해 못 찾으면 false.</summary>
    public static bool TryPickWanderTarget(Creature c, WorldState w, float minDist, float maxDist, out Vec2 target)
    {
        var terrain = w.Terrain;
        float margin = terrain.CellSize * 2f;
        float size = terrain.WorldSize;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            float angle = c.Rng.NextFloat() * MathUtil.TwoPi;
            float dist = c.Rng.Range(minDist, maxDist);
            Vec2 p = c.Position + Vec2.FromHeading(angle) * dist;
            if (p.X < margin || p.Z < margin || p.X > size - margin || p.Z > size - margin) continue;
            if (!terrain.IsLineWalkable(c.Position, p)) continue;
            target = p;
            return true;
        }
        target = c.Position;
        return false;
    }

    /// <summary>
    /// 사냥감 고르기: 지각 범위 안의 다른 종 초식동물 가운데 '가깝고 약한' 개체.
    /// 점수가 같으면 id가 작은 쪽(목록 순서)을 고른다. 난수를 쓰지 않는다.
    /// </summary>
    public static Creature? FindPrey(Creature c, SpeciesDefinition sp, WorldState w)
    {
        Creature? best = null;
        float bestScore = 0f;
        float range = sp.PerceptionRadiusM;
        float rangeSq = range * range;
        IReadOnlyList<Creature> all = w.CreatureList;
        for (int i = 0; i < all.Count; i++)
        {
            Creature o = all[i];
            if (!o.Alive || o.Id == c.Id || o.SpeciesIndex == c.SpeciesIndex) continue;
            if (w.Species[o.SpeciesIndex].Diet == Diet.Carnivore) continue;
            float dSq = Vec2.DistanceSquared(c.Position, o.Position);
            if (dSq > rangeSq) continue;
            float d = System.MathF.Sqrt(dSq);
            SpeciesDefinition os = w.Species[o.SpeciesIndex];
            // 어리거나 늙은 개체를 노린다(작을수록, 노년이 깊을수록 점수가 오른다).
            float weakness = 1.3f - 0.6f * o.Health + 0.3f * o.Fatigue
                + 0.5f * (1f - LifeHistory.LengthFraction(os, o.AgeDays)) + 0.3f * LifeHistory.SeniorProgress(o, os);
            float score = weakness / (1f + d / 20f);
            // 깊은 물 건너편처럼 곧장 갈 수 없는 사냥감은 고르지 않는다.
            if (score > bestScore && w.Terrain.IsLineWalkable(c.Position, o.Position))
            {
                bestScore = score;
                best = o;
            }
        }
        return best;
    }

    /// <summary>냄새로 찾는 사체: 고기가 남은 것 가운데 '가깝고 많은' 것.</summary>
    public static Carcass? FindCarcass(Creature c, SpeciesDefinition sp, WorldState w)
    {
        Carcass? best = null;
        float bestScore = 0f;
        float rangeSq = sp.SmellRadiusM * sp.SmellRadiusM;
        for (int i = 0; i < w.Carcasses.Count; i++)
        {
            Carcass k = w.Carcasses[i];
            if (k.MeatKg < MinMeatKg || k.Id == c.IgnoredCarcassId) continue;
            float dSq = Vec2.DistanceSquared(c.Position, k.Position);
            if (dSq > rangeSq) continue;
            // 고기 양의 네제곱근(제곱근은 IEEE가 결과를 정해 두어 기기마다 같다. Pow는 그렇지 않다).
            float score = System.MathF.Sqrt(System.MathF.Sqrt(k.MeatKg)) / (1f + System.MathF.Sqrt(dSq) / 30f);
            if (score > bestScore && (dSq < 100f || w.Terrain.IsLineWalkable(c.Position, k.Position)))
            {
                bestScore = score;
                best = k;
            }
        }
        return best;
    }

    /// <summary>먹을 고기가 남았다고 보는 최소량(kg).</summary>
    public const float MinMeatKg = 0.5f;

    /// <summary>
    /// 먹이가 빈약한 곳을 떠날 때의 먼 목적지. 지금 향하던 쪽을 우선해 같은 자리를 맴돌지 않게 한다.
    /// </summary>
    public static bool TryPickRelocationTarget(Creature c, WorldState w, float minDist, float maxDist, out Vec2 target)
    {
        var terrain = w.Terrain;
        float margin = terrain.CellSize * 4f;
        float size = terrain.WorldSize;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            float angle = attempt < 3
                ? c.Heading + c.Rng.Range(-0.9f, 0.9f)
                : c.Rng.NextFloat() * MathUtil.TwoPi;
            float dist = c.Rng.Range(minDist, maxDist);
            Vec2 p = c.Position + Vec2.FromHeading(angle) * dist;
            if (p.X < margin || p.Z < margin || p.X > size - margin || p.Z > size - margin) continue;
            if (!terrain.IsDryLandAt(p) || !terrain.IsLineWalkable(c.Position, p)) continue;
            target = p;
            return true;
        }
        target = c.Position;
        return false;
    }
}
