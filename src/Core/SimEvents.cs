using System;
using System.Collections.Generic;

namespace PaleoSphere.Core;

public enum SimEventType
{
    WorldCreated = 0,
    CreatureSpawned = 1,
    CreatureDied = 2,
    CreatureRemoved = 3,
    DayStarted = 4,
    SeasonChanged = 5,
    CommandRejected = 6,
    Notice = 7,

    /// <summary>사냥 성공. CreatureId·SpeciesId는 사냥한 쪽, Detail은 "사냥감id|사냥감종id".</summary>
    Predation = 8,
}

/// <summary>시뮬레이션에서 일어난 사건 한 건. Detail은 UI 표시용 보조 정보(사망 원인 id 등).</summary>
public readonly record struct SimEvent(
    long Tick,
    int Day,
    float Hour,
    SimEventType Type,
    int CreatureId,
    string SpeciesId,
    string Detail,
    Vec2 Position);

/// <summary>
/// 사건 발행기. 구독자는 등록 순서대로 동기 호출되므로 처리 순서가 고정된다.
/// 최근 사건은 고정 크기 로그로 보관한다.
/// </summary>
public sealed class EventBus
{
    private readonly List<Action<SimEvent>> _subscribers = new();
    private readonly List<SimEvent> _recent = new();

    public int Capacity { get; }

    public EventBus(int capacity = 400)
    {
        Capacity = Math.Max(16, capacity);
    }

    public IReadOnlyList<SimEvent> Recent => _recent;

    /// <summary>지금까지 발행된 사건 수(로그에서 밀려난 것 포함).</summary>
    public long TotalPublished { get; private set; }

    public void Subscribe(Action<SimEvent> handler) => _subscribers.Add(handler);
    public void Unsubscribe(Action<SimEvent> handler) => _subscribers.Remove(handler);

    public void Publish(in SimEvent e)
    {
        TotalPublished++;
        _recent.Add(e);
        if (_recent.Count > Capacity) _recent.RemoveRange(0, _recent.Count - Capacity);
        for (int i = 0; i < _subscribers.Count; i++) _subscribers[i](e);
    }

    public void RestoreLog(IEnumerable<SimEvent> events, long totalPublished)
    {
        _recent.Clear();
        _recent.AddRange(events);
        if (_recent.Count > Capacity) _recent.RemoveRange(0, _recent.Count - Capacity);
        TotalPublished = Math.Max(totalPublished, _recent.Count);
    }
}
