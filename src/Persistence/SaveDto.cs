using System.Collections.Generic;

namespace PaleoSphere.Persistence;

// 세이브 파일의 state 부분. 실행 중 클래스와 분리해 두어 내부 구조를 바꿔도 파일 형식이 흔들리지 않게 한다.
// 속성 이름은 snake_case로 기록된다(SaveSerializer.JsonOptions).

public sealed class SaveStateDto
{
    public WorldConfigDto? Config { get; set; }
    public long Tick { get; set; }
    public int NextCreatureId { get; set; }
    public string WorldRngState { get; set; } = "";
    public string WorldRngInc { get; set; } = "";
    public TerrainDto? Terrain { get; set; }
    public VegetationDto? Vegetation { get; set; }
    public List<ThreatDto> Threats { get; set; } = new();

    /// <summary>사체(스키마 2부터).</summary>
    public List<CarcassDto> Carcasses { get; set; } = new();
    public double CarcassMassCreated { get; set; }
    public double CarcassMassEaten { get; set; }
    public double CarcassMassDecomposed { get; set; }
    public List<string> SpeciesIds { get; set; } = new();
    public List<CreatureDto> Creatures { get; set; } = new();
    public StatsDto? Stats { get; set; }
    public List<EventDto> Events { get; set; } = new();
    public long EventsTotal { get; set; }

    /// <summary>사용자가 지형에 가한 수정 내역. 지형 편집기(Phase 4) 전까지는 항상 비어 있다.</summary>
    public List<string> UserEdits { get; set; } = new();
}

public sealed class WorldConfigDto
{
    public string Seed { get; set; } = "0";
    public int SizeCells { get; set; }
    public float CellSize { get; set; }
    public float Relief { get; set; }
    public float WaterFraction { get; set; }
    public bool River { get; set; }
    public float InitialVegetation { get; set; }
    public float VegetationCapacityScale { get; set; }
    public float VegetationGrowthScale { get; set; }
    public float MoistureBias { get; set; }
    public float BaseTemperatureC { get; set; }
    public float SeasonalAmplitudeC { get; set; }
    public float DiurnalAmplitudeC { get; set; }
    public int TicksPerDay { get; set; }
    public int DaysPerSeason { get; set; }
    public float StartHour { get; set; }
}

public sealed class TerrainDto
{
    public int Size { get; set; }
    public float CellSize { get; set; }
    public float WaterLevel { get; set; }
    public List<string> BiomeIds { get; set; } = new();

    // float32/byte 배열을 리틀 엔디언 그대로 Base64로 담는다(비트 단위로 같은 값 복원).
    public string Height { get; set; } = "";
    public string Biome { get; set; } = "";
    public string Moisture { get; set; } = "";
    public string Fertility { get; set; } = "";
}

public sealed class VegetationDto
{
    public string Biomass { get; set; } = "";

    /// <summary>토양 영양 배열(스키마 2부터). 비어 있으면 0으로 본다.</summary>
    public string Nutrient { get; set; } = "";
    public double TotalNutrientAdded { get; set; }
    public double TotalNutrientUsed { get; set; }
    public double TotalNutrientLeached { get; set; }
    public double InitialTotal { get; set; }
    public double TotalGrown { get; set; }
    public double TotalConsumed { get; set; }
    public double TotalDieback { get; set; }
}

public sealed class CarcassDto
{
    public int Id { get; set; }
    public int Species { get; set; }
    public float X { get; set; }
    public float Z { get; set; }
    public float Heading { get; set; }
    public int Cause { get; set; }
    public long DiedTick { get; set; }
    public float MeatKg { get; set; }
    public float InitialMeatKg { get; set; }
    public float RemainsKg { get; set; }
    public float InitialMassKg { get; set; }
}

public sealed class ThreatDto
{
    public float X { get; set; }
    public float Z { get; set; }
    public float Radius { get; set; }
    public float Intensity { get; set; }
    public long UntilTick { get; set; }
}

public sealed class CreatureDto
{
    public int Id { get; set; }
    public int Species { get; set; }
    public float X { get; set; }
    public float Z { get; set; }
    public float PrevX { get; set; }
    public float PrevZ { get; set; }
    public float Heading { get; set; }
    public float PrevHeading { get; set; }
    public float Speed { get; set; }
    public float AgeDays { get; set; }
    public int Sex { get; set; }
    public int LifeStage { get; set; }
    public float Health { get; set; }
    public float Energy { get; set; }
    public float Hydration { get; set; }
    public float Fatigue { get; set; }
    public float Fear { get; set; }
    public int Behavior { get; set; }
    public int Activity { get; set; }
    public long BehaviorStartTick { get; set; }
    public bool HasTarget { get; set; }
    public float TargetX { get; set; }
    public float TargetZ { get; set; }
    public int TargetEntityId { get; set; }
    public long NextRetargetTick { get; set; }
    public float ThreatX { get; set; }
    public float ThreatZ { get; set; }
    public long ThreatUntilTick { get; set; }
    public float TraitBodySize { get; set; }
    public float TraitMetabolism { get; set; }
    public float TraitSpeed { get; set; }
    public int ParentA { get; set; }
    public int ParentB { get; set; }

    // 사냥 상태(스키마 2부터). 없으면 기본값.
    public long ChaseStartTick { get; set; } = -1;
    public long NextAttackTick { get; set; }
    public long HuntCooldownUntilTick { get; set; }
    public int Kills { get; set; }
    public int KilledBy { get; set; } = -1;
    public int IgnoredCarcassId { get; set; } = -1;
    public string RngState { get; set; } = "";
    public string RngInc { get; set; } = "";
    public float StarveDamage { get; set; }
    public float DehydrationDamage { get; set; }
    public int StuckTicks { get; set; }
    public int AvoidSide { get; set; }
    public float BoutAmount { get; set; }
    public float[] Scores { get; set; } = System.Array.Empty<float>();
    public int Reason { get; set; }
    public long LastDecisionTick { get; set; }
    public List<LogDto> Log { get; set; } = new();
}

public sealed class LogDto
{
    public long Tick { get; set; }
    public int Behavior { get; set; }
    public int Reason { get; set; }
    public float Score { get; set; }
    public float Energy { get; set; }
    public float Hydration { get; set; }
    public float Fatigue { get; set; }
    public float Fear { get; set; }
    public int Result { get; set; }
    public float ResultAmount { get; set; }
    public long EndTick { get; set; }
}

public sealed class StatsDto
{
    public int SampleIntervalTicks { get; set; }
    public int[] Deaths { get; set; } = System.Array.Empty<int>();
    public int[] Spawned { get; set; } = System.Array.Empty<int>();
    public List<SampleDto> Samples { get; set; } = new();
}

public sealed class SampleDto
{
    public long Tick { get; set; }
    public float Day { get; set; }
    public int[] Counts { get; set; } = System.Array.Empty<int>();
    public float VegetationKg { get; set; }
    public float CarcassMeatKg { get; set; }
    public float MeanEnergy { get; set; }
    public float MeanHydration { get; set; }
    public int TotalDeaths { get; set; }
}

public sealed class EventDto
{
    public long Tick { get; set; }
    public int Day { get; set; }
    public float Hour { get; set; }
    public int Type { get; set; }
    public int CreatureId { get; set; }
    public string SpeciesId { get; set; } = "";
    public string Detail { get; set; } = "";
    public float X { get; set; }
    public float Z { get; set; }
}

/// <summary>세이브 파일 머리말. state를 풀지 않고도 목록에 보여 줄 수 있는 정보.</summary>
public sealed class SaveHeader
{
    public int SchemaVersion { get; set; }
    public string SimulationVersion { get; set; } = "";
    public string WorldSeed { get; set; } = "";
    public string SavedAtUtc { get; set; } = "";
    public string StateHash { get; set; } = "";
    public string PayloadSha256 { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Day { get; set; }
    public float Hour { get; set; }
    public int CreatureCount { get; set; }
    public long Tick { get; set; }
}
