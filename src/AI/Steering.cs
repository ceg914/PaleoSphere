using System;
using System.Collections.Generic;
using PaleoSphere.Core;
using PaleoSphere.Creatures;
using PaleoSphere.Simulation;

namespace PaleoSphere.AI;

/// <summary>
/// 이동 처리. 1단계에서 모든 개체의 희망 속도를 '이전 틱 위치'만 보고 계산하고,
/// 2단계에서 한꺼번에 적분한다. 처리 순서가 결과에 영향을 주지 않는다.
/// 물·급경사 셀은 방향을 틀어 피하고, 계속 막히면 목표를 버린다.
/// </summary>
public sealed class Steering
{
    private static readonly float[] AvoidAngles = { 0.44f, 0.87f, 1.40f, 2.0f };
    private const int BlockedTicksLimit = 30;

    /// <summary>격자 칸의 최소 크기(m). 칸이 너무 작으면 칸 배열을 비우는 비용이 커진다.</summary>
    private const float MinHashCellM = 4f;

    private float _hashCell = 8f;
    private int _dim;
    private int[] _head = Array.Empty<int>();
    private int[] _next = Array.Empty<int>();
    private Vec2[] _velocity = Array.Empty<Vec2>();
    // 이번 틱 시작 시점의 위치와 몸 반지름. 분리 계산이 개체 객체를 오가지 않고 배열만 읽게 한다.
    private Vec2[] _pos = Array.Empty<Vec2>();
    private float[] _radius = Array.Empty<float>();

    public void Step(List<Creature> creatures, WorldState w)
    {
        int count = creatures.Count;
        if (count == 0) return;
        if (_velocity.Length < count)
        {
            _velocity = new Vec2[Math.Max(count, _velocity.Length * 2)];
            _next = new int[_velocity.Length];
            _pos = new Vec2[_velocity.Length];
            _radius = new float[_velocity.Length];
        }

        BuildHash(creatures, w);
        var terrain = w.Terrain;

        for (int i = 0; i < count; i++)
        {
            Creature c = creatures[i];
            SpeciesDefinition sp = w.Species[c.SpeciesIndex];
            Vec2 dir = Vec2.Zero;
            float speed = 0f;

            if (c.Activity == ActivityKind.Walk || c.Activity == ActivityKind.Run)
            {
                if (c.Behavior == BehaviorKind.Drink)
                {
                    terrain.TryGetDirectionToWater(c.Position, out dir);
                }
                else if (c.HasTarget)
                {
                    Vec2 to = c.Target - c.Position;
                    if (to.LengthSquared > 0.04f) dir = to.Normalized();
                }

                if (dir.LengthSquared > 0f)
                {
                    int cell = terrain.CellIndexAt(c.Position);
                    float terrainFactor = w.Biomes[terrain.Biome[cell]].MoveSpeedFactor;
                    speed = (c.Activity == ActivityKind.Run ? sp.RunSpeedMps : sp.WalkSpeedMps)
                            * c.TraitSpeed * (0.6f + 0.4f * c.Health) * (1f - 0.3f * c.Fatigue) * terrainFactor;
                }
            }

            Vec2 push = Separation(creatures[i].Id, i);
            bool stationary = speed <= 0f;
            Vec2 v = dir * speed + push * (stationary ? 0.4f : 1.5f);
            float cap = MathF.Max(speed, 0.6f);
            float len = v.Length;
            if (len > cap) v = v * (cap / len);
            _velocity[i] = v;
        }

        float dt = SimClock.TickSeconds;
        float margin = terrain.CellSize;
        float size = terrain.WorldSize;

        for (int i = 0; i < count; i++)
        {
            Creature c = creatures[i];
            SpeciesDefinition sp = w.Species[c.SpeciesIndex];
            c.PrevPosition = c.Position;
            c.PrevHeading = c.Heading;

            Vec2 v = _velocity[i];
            if (c.Activity == ActivityKind.Attack && c.HasTarget)
            {
                // 공격 중에는 제자리에서 사냥감 쪽으로 몸을 돌린다.
                Vec2 to = c.Target - c.Position;
                if (to.LengthSquared > 0.01f)
                {
                    float maxTurn = sp.TurnRateDegPerSec * (MathUtil.Pi / 180f) * dt;
                    c.Heading = MathUtil.MoveTowardAngle(c.Heading, to.Heading, maxTurn);
                }
            }
            if (v.LengthSquared < 1e-6f)
            {
                c.CurrentSpeed = 0f;
                continue;
            }

            Vec2 step = v * dt;
            Vec2 next = Clamp(c.Position + step, margin, size);
            bool wantsToMove = c.Activity == ActivityKind.Walk || c.Activity == ActivityKind.Run;
            bool onBadCell = !terrain.Walkable[terrain.CellIndexAt(c.Position)];

            if (!onBadCell && !terrain.IsWalkableAt(next))
            {
                bool found = false;
                for (int a = 0; a < AvoidAngles.Length && !found; a++)
                {
                    for (int s = 0; s < 2; s++)
                    {
                        int side = s == 0 ? c.AvoidSide : -c.AvoidSide;
                        Vec2 cand = Clamp(c.Position + step.Rotated(side * AvoidAngles[a]), margin, size);
                        if (!terrain.IsWalkableAt(cand)) continue;
                        next = cand;
                        c.AvoidSide = (sbyte)side;
                        found = true;
                        if (a >= 2 && wantsToMove) c.StuckTicks++;
                        break;
                    }
                }
                if (!found)
                {
                    next = c.Position;
                    if (wantsToMove) c.StuckTicks += 2;
                }
            }
            else if (c.StuckTicks > 0)
            {
                c.StuckTicks--;
            }

            Vec2 moved = next - c.Position;
            c.Position = next;
            c.CurrentSpeed = moved.Length / dt;
            if (wantsToMove && moved.LengthSquared > 1e-6f)
            {
                float maxTurn = sp.TurnRateDegPerSec * (MathUtil.Pi / 180f) * dt;
                c.Heading = MathUtil.MoveTowardAngle(c.Heading, moved.Heading, maxTurn);
            }

            if (c.StuckTicks > BlockedTicksLimit) BehaviorExecutor.OnBlocked(c);
        }
    }

    private static Vec2 Clamp(Vec2 p, float margin, float size) =>
        new(MathUtil.Clamp(p.X, margin, size - margin), MathUtil.Clamp(p.Z, margin, size - margin));

    /// <summary>
    /// 공간 격자를 만든다. 칸 크기는 지금 세계에 있는 개체 가운데 가장 큰 몸 지름이라,
    /// 겹칠 수 있는 두 개체는 항상 이웃한 칸(3×3) 안에 있다.
    /// 정의만 있고 세계에 없는 종의 몸집은 칸 크기에 영향을 주지 않는다.
    /// </summary>
    private void BuildHash(List<Creature> creatures, WorldState w)
    {
        int count = creatures.Count;
        float maxRadius = MinHashCellM * 0.5f;
        for (int i = 0; i < count; i++)
        {
            Creature c = creatures[i];
            float r = w.Species[c.SpeciesIndex].BodyRadiusM * c.TraitBodySize;
            _radius[i] = r;
            _pos[i] = c.Position;
            if (r > maxRadius) maxRadius = r;
        }
        _hashCell = maxRadius * 2f;
        _dim = Math.Max(1, (int)MathF.Ceiling(w.Terrain.WorldSize / _hashCell));
        int cells = _dim * _dim;
        if (_head.Length < cells) _head = new int[cells];
        Array.Fill(_head, -1, 0, cells);
        for (int i = 0; i < count; i++)
        {
            int h = HashIndex(_pos[i]);
            _next[i] = _head[h];
            _head[h] = i;
        }
    }

    private int HashIndex(Vec2 p)
    {
        int hx = MathUtil.Clamp((int)(p.X / _hashCell), 0, _dim - 1);
        int hz = MathUtil.Clamp((int)(p.Z / _hashCell), 0, _dim - 1);
        return hz * _dim + hx;
    }

    /// <summary>몸이 겹치는 이웃에서 멀어지는 방향(겹친 정도에 비례).</summary>
    private Vec2 Separation(int id, int i)
    {
        Vec2 pi = _pos[i];
        float ri = _radius[i];
        int hx = MathUtil.Clamp((int)(pi.X / _hashCell), 0, _dim - 1);
        int hz = MathUtil.Clamp((int)(pi.Z / _hashCell), 0, _dim - 1);
        Vec2 push = Vec2.Zero;
        for (int dz = -1; dz <= 1; dz++)
        {
            int z = hz + dz;
            if (z < 0 || z >= _dim) continue;
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = hx + dx;
                if (x < 0 || x >= _dim) continue;
                for (int j = _head[z * _dim + x]; j >= 0; j = _next[j])
                {
                    if (j == i) continue;
                    float r = ri + _radius[j];
                    Vec2 d = pi - _pos[j];
                    float distSq = d.LengthSquared;
                    if (distSq >= r * r) continue;
                    float dist = MathF.Sqrt(distSq);
                    // 완전히 겹치면 id로 정해지는 방향으로 민다(난수 없이 결정론 유지).
                    Vec2 n = dist > 1e-3f ? d / dist : Vec2.FromHeading(id * 137 % 360 * (MathUtil.TwoPi / 360f));
                    push += n * ((r - dist) / r);
                }
            }
        }
        return push;
    }
}
