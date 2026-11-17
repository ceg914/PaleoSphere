using System;
using System.Collections.Generic;
using PaleoSphere.Core;

namespace PaleoSphere.Creatures;

/// <summary>근거 수준이 붙은 서술 항목(도감 표시용).</summary>
public sealed record FactEntry(string Topic, string Text, EvidenceLevel Evidence);

public sealed record SourceEntry(string Title, string Note);

/// <summary>
/// 3D 모델 정보(선택). 표현 계층만 쓰며 시뮬레이션에는 영향이 없다.
/// 길이와 보폭은 tools/godot/measure_model.gd, measure_gait.gd로 잰 값이다.
/// </summary>
public sealed class SpeciesModel
{
    /// <summary>res://assets/ 아래의 모델 파일(.fbx, .glb, .gltf, .tscn, .scn).</summary>
    public string Path { get; init; } = "";
    public string Source { get; init; } = "";
    public string License { get; init; } = "";
    public EvidenceLevel Evidence { get; init; } = EvidenceLevel.Simplified;

    /// <summary>모델의 머리끝–꼬리끝 길이(모델 단위). 종의 몸길이에 맞춰 크기를 정하는 데 쓴다.</summary>
    public float LengthUnits { get; init; }

    /// <summary>걷기·달리기 애니메이션을 1배속으로 틀 때 발이 미끄러지지 않는 이동 속도(모델 단위/초).</summary>
    public float WalkUnitsPerSecond { get; init; }
    public float RunUnitsPerSecond { get; init; }

    /// <summary>동작 이름 → 애니메이션 이름(끝부분만 맞으면 된다). 키: idle, walk, run, death, attack.</summary>
    public IReadOnlyDictionary<string, string> Animations { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// 종 정의(data/species/*.json). 시대·분포·체격은 문헌에 근거하고,
/// 속도·대사·행동 수치는 시뮬레이션을 돌리기 위한 게임적 단순화다. 항목마다 근거 수준을 붙인다.
/// </summary>
public sealed class SpeciesDefinition
{
    public string Id { get; init; } = "";
    public string ScientificName { get; init; } = "";
    public string NameKo { get; init; } = "";
    public string NameEn { get; init; } = "";
    public Diet Diet { get; init; }

    public string Period { get; init; } = "";
    public EvidenceLevel PeriodEvidence { get; init; }
    public string Range { get; init; } = "";
    public EvidenceLevel RangeEvidence { get; init; }

    public float BodyLengthM { get; init; }
    public float BodyMassKg { get; init; }
    public EvidenceLevel BodyEvidence { get; init; } = EvidenceLevel.Inferred;

    // --- 이동 (게임적 단순화) ---
    public float WalkSpeedMps { get; init; }
    public float RunSpeedMps { get; init; }
    public float TurnRateDegPerSec { get; init; } = 60f;
    public float PerceptionRadiusM { get; init; } = 40f;

    // --- 생리 (게임적 단순화, 게임 내 하루·시간 기준) ---
    public float DailyFoodKg { get; init; }
    public float EnergyReserveDays { get; init; }
    public float IntakeKgPerHour { get; init; }
    public float HydrationReserveDays { get; init; }
    public float DrinkHoursToFull { get; init; }
    public float AwakeHours { get; init; }
    public float RestHours { get; init; }
    public float StarveHealthDays { get; init; }
    public float DehydrationHealthDays { get; init; }
    public float HealthRegenPerDay { get; init; }

    // --- 생활사 (게임적 단순화, 게임 내 일수) ---
    public float AdultAgeDays { get; init; }
    public float MaxAgeDays { get; init; }

    /// <summary>배치할 수 있는 생물군계 id. 비어 있으면 마른 땅 어디든.</summary>
    public IReadOnlyList<string> PlacementBiomes { get; init; } = Array.Empty<string>();

    // --- 임시 도형 프록시 (게임적 단순화) ---
    public float ProxyColorR { get; init; } = 0.5f;
    public float ProxyColorG { get; init; } = 0.5f;
    public float ProxyColorB { get; init; } = 0.5f;
    public string ProxyShape { get; init; } = "quadruped";

    /// <summary>3D 모델. 없거나 불러오지 못하면 코드로 만든 메시를 쓴다.</summary>
    public SpeciesModel? Model { get; init; }

    public IReadOnlyList<FactEntry> Facts { get; init; } = Array.Empty<FactEntry>();
    public IReadOnlyList<SourceEntry> Sources { get; init; } = Array.Empty<SourceEntry>();

    /// <summary>먹이 1kg이 채우는 에너지(0~1 척도). DailyFoodKg를 먹으면 서 있을 때의 하루 소모와 같아진다.</summary>
    public float EnergyPerKg => 1f / (DailyFoodKg * EnergyReserveDays);

    /// <summary>개체끼리 겹치지 않게 미는 반경(m).</summary>
    public float BodyRadiusM => BodyLengthM * 0.3f;
}

public sealed class SpeciesRegistry
{
    private readonly List<SpeciesDefinition> _list = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public IReadOnlyList<SpeciesDefinition> All => _list;
    public int Count => _list.Count;

    /// <summary>id 순으로 정렬해 등록한다(파일 읽기 순서와 무관한 색인).</summary>
    public SpeciesRegistry(IEnumerable<SpeciesDefinition> species)
    {
        var sorted = new List<SpeciesDefinition>(species);
        sorted.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        foreach (var s in sorted)
        {
            if (_index.ContainsKey(s.Id)) continue;
            _index[s.Id] = _list.Count;
            _list.Add(s);
        }
    }

    public bool TryGetIndex(string id, out int index) => _index.TryGetValue(id, out index);
    public SpeciesDefinition this[int index] => _list[index];
}
