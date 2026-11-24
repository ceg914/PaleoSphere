using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaleoSphere.Core;
using PaleoSphere.Creatures;
using PaleoSphere.Ecology;
using PaleoSphere.Modding;
using PaleoSphere.Simulation;
using PaleoSphere.World;

namespace PaleoSphere.Persistence;

public sealed class LoadResult
{
    public SimulationCore? Sim { get; set; }
    public SaveHeader? Header { get; set; }
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool Ok => Sim != null && Errors.Count == 0;
}

/// <summary>
/// 세이브 직렬화. 파일은 gzip으로 압축한 JSON이고, 머리말(스키마 버전·시드·체크섬·상태 해시)과 state로 이뤄진다.
/// 읽을 때는 크기·깊이·범위를 모두 검사하고, 문제가 있으면 예외 대신 오류 목록을 돌려준다.
/// </summary>
public static class SaveSerializer
{
    public const string Format = "paleosphere-save";
    public const long MaxFileBytes = 256L * 1024 * 1024;
    public const long MaxDecompressedBytes = 768L * 1024 * 1024;
    private const int MaxDepth = 32;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        MaxDepth = MaxDepth,
    };

    // ------------------------------------------------------------ 쓰기

    public static byte[] Serialize(SimulationCore sim, string displayName)
    {
        SaveStateDto dto = ToDto(sim);
        byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
        var header = new SaveHeader
        {
            SchemaVersion = SimVersion.SaveSchema,
            SimulationVersion = SimVersion.Simulation,
            WorldSeed = sim.World.Config.Seed.ToString(CultureInfo.InvariantCulture),
            SavedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            StateHash = StateHasher.ToHex(sim.ComputeStateHash()),
            DisplayName = displayName.Length > 80 ? displayName.Substring(0, 80) : displayName,
            Day = sim.Clock.Day,
            Hour = sim.Clock.TimeOfDayHours,
            CreatureCount = sim.CreatureCount,
            Tick = sim.Clock.Tick,
        };
        return BuildFile(stateBytes, header);
    }

    /// <summary>state JSON에 머리말을 씌우고 압축한다. 체크섬은 여기서 계산한다.</summary>
    internal static byte[] BuildFile(byte[] stateBytes, SaveHeader header, bool compress = true)
    {
        string sha = Convert.ToHexString(SHA256.HashData(stateBytes)).ToLowerInvariant();
        using var raw = new MemoryStream();
        Stream target = compress ? new GZipStream(raw, CompressionLevel.Fastest, leaveOpen: true) : raw;
        using (var writer = new Utf8JsonWriter(target))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("schema_version", header.SchemaVersion);
            writer.WriteString("simulation_version", header.SimulationVersion);
            writer.WriteString("world_seed", header.WorldSeed);
            writer.WriteString("saved_at_utc", header.SavedAtUtc);
            writer.WriteString("state_hash", header.StateHash);
            writer.WriteString("payload_sha256", sha);
            writer.WriteString("display_name", header.DisplayName);
            writer.WriteNumber("day", header.Day);
            writer.WriteNumber("hour", header.Hour);
            writer.WriteNumber("creature_count", header.CreatureCount);
            writer.WriteNumber("tick", header.Tick);
            writer.WritePropertyName("state");
            writer.WriteRawValue(stateBytes, skipInputValidation: true);
            writer.WriteEndObject();
        }
        if (compress) target.Dispose();
        return raw.ToArray();
    }

    /// <summary>검증을 통과한 파일에서 state JSON 바이트만 꺼낸다(테스트·진단용).</summary>
    internal static byte[]? ExtractState(byte[] fileBytes, out SaveHeader? header, out string? error)
    {
        header = Open(fileBytes, out ReadOnlyMemory<byte> state, out error);
        return header == null ? null : state.ToArray();
    }

    private static string Hex(ulong v) => v.ToString("x16", CultureInfo.InvariantCulture);

    private static string Pack(float[] values)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("리틀 엔디언 플랫폼만 지원합니다.");
        return Convert.ToBase64String(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    private static SaveStateDto ToDto(SimulationCore sim)
    {
        WorldState w = sim.World;
        WorldConfig c = w.Config;
        var dto = new SaveStateDto
        {
            Config = new WorldConfigDto
            {
                Seed = c.Seed.ToString(CultureInfo.InvariantCulture),
                SizeCells = c.SizeCells,
                CellSize = c.CellSize,
                Relief = c.Relief,
                WaterFraction = c.WaterFraction,
                River = c.River,
                InitialVegetation = c.InitialVegetation,
                VegetationCapacityScale = c.VegetationCapacityScale,
                VegetationGrowthScale = c.VegetationGrowthScale,
                MoistureBias = c.MoistureBias,
                BaseTemperatureC = c.BaseTemperatureC,
                SeasonalAmplitudeC = c.SeasonalAmplitudeC,
                DiurnalAmplitudeC = c.DiurnalAmplitudeC,
                TicksPerDay = c.TicksPerDay,
                DaysPerSeason = c.DaysPerSeason,
                StartHour = c.StartHour,
            },
            Tick = sim.Clock.Tick,
            NextCreatureId = sim.NextCreatureId,
            WorldRngState = Hex(sim.WorldRng.State),
            WorldRngInc = Hex(sim.WorldRng.Inc),
            Terrain = new TerrainDto
            {
                Size = w.Terrain.Size,
                CellSize = w.Terrain.CellSize,
                WaterLevel = w.Terrain.WaterLevel,
                Height = Pack(w.Terrain.Height),
                Biome = Convert.ToBase64String(w.Terrain.Biome),
                Moisture = Pack(w.Terrain.Moisture),
                Fertility = Pack(w.Terrain.Fertility),
            },
            Vegetation = new VegetationDto
            {
                Biomass = Pack(w.Vegetation.Biomass),
                Nutrient = Pack(w.Vegetation.Nutrient),
                TotalNutrientAdded = w.Vegetation.TotalNutrientAdded,
                TotalNutrientUsed = w.Vegetation.TotalNutrientUsed,
                TotalNutrientLeached = w.Vegetation.TotalNutrientLeached,
                InitialTotal = w.Vegetation.InitialTotal,
                TotalGrown = w.Vegetation.TotalGrown,
                TotalConsumed = w.Vegetation.TotalConsumed,
                TotalDieback = w.Vegetation.TotalDieback,
            },
            EventsTotal = sim.Events.TotalPublished,
            CarcassMassCreated = w.CarcassMassCreated,
            CarcassMassEaten = w.CarcassMassEaten,
            CarcassMassDecomposed = w.CarcassMassDecomposed,
        };
        foreach (BiomeDefinition b in w.Biomes.All) dto.Terrain.BiomeIds.Add(b.Id);
        foreach (SpeciesDefinition s in w.Species.All) dto.SpeciesIds.Add(s.Id);
        foreach (ThreatSource t in w.Threats)
            dto.Threats.Add(new ThreatDto { X = t.Position.X, Z = t.Position.Z, Radius = t.Radius, Intensity = t.Intensity, UntilTick = t.UntilTick });

        foreach (Creature cr in sim.Creatures)
        {
            var cd = new CreatureDto
            {
                Id = cr.Id,
                Species = cr.SpeciesIndex,
                X = cr.Position.X, Z = cr.Position.Z,
                PrevX = cr.PrevPosition.X, PrevZ = cr.PrevPosition.Z,
                Heading = cr.Heading, PrevHeading = cr.PrevHeading, Speed = cr.CurrentSpeed,
                AgeDays = cr.AgeDays, Sex = (int)cr.Sex, LifeStage = (int)cr.LifeStage, LifespanDays = cr.LifespanDays,
                Health = cr.Health, Energy = cr.Energy, Hydration = cr.Hydration, Fatigue = cr.Fatigue, Fear = cr.Fear,
                Behavior = (int)cr.Behavior, Activity = (int)cr.Activity, BehaviorStartTick = cr.BehaviorStartTick,
                HasTarget = cr.HasTarget, TargetX = cr.Target.X, TargetZ = cr.Target.Z,
                TargetEntityId = cr.TargetEntityId, NextRetargetTick = cr.NextRetargetTick,
                ThreatX = cr.ThreatPosition.X, ThreatZ = cr.ThreatPosition.Z, ThreatUntilTick = cr.ThreatUntilTick,
                TraitBodySize = cr.TraitBodySize, TraitMetabolism = cr.TraitMetabolism, TraitSpeed = cr.TraitSpeed,
                ParentA = cr.ParentA, ParentB = cr.ParentB,
                ChaseStartTick = cr.ChaseStartTick, NextAttackTick = cr.NextAttackTick, HuntCooldownUntilTick = cr.HuntCooldownUntilTick,
                Kills = cr.Kills, KilledBy = cr.KilledBy, IgnoredCarcassId = cr.IgnoredCarcassId,
                RngState = Hex(cr.Rng.State), RngInc = Hex(cr.Rng.Inc),
                StarveDamage = cr.StarveDamage, DehydrationDamage = cr.DehydrationDamage,
                StuckTicks = cr.StuckTicks, AvoidSide = cr.AvoidSide, BoutAmount = cr.BoutAmount,
                Scores = (float[])cr.LastScores.Clone(), Reason = (int)cr.LastReason, LastDecisionTick = cr.LastDecisionTick,
            };
            foreach (BehaviorLogEntry e in cr.Log)
            {
                cd.Log.Add(new LogDto
                {
                    Tick = e.Tick, Behavior = (int)e.Behavior, Reason = (int)e.Reason, Score = e.Score,
                    Energy = e.Energy, Hydration = e.Hydration, Fatigue = e.Fatigue, Fear = e.Fear,
                    Result = (int)e.Result, ResultAmount = e.ResultAmount, EndTick = e.EndTick,
                });
            }
            dto.Creatures.Add(cd);
        }

        PopulationStats st = sim.Stats;
        dto.Stats = new StatsDto
        {
            SampleIntervalTicks = st.SampleIntervalTicks,
            Deaths = (int[])st.Deaths.Clone(),
            Spawned = (int[])st.Spawned.Clone(),
        };
        foreach (StatSample s in st.Samples)
        {
            dto.Stats.Samples.Add(new SampleDto
            {
                Tick = s.Tick, Day = s.Day, Counts = s.Counts, VegetationKg = s.VegetationKg, CarcassMeatKg = s.CarcassMeatKg,
                MeanEnergy = s.MeanEnergy, MeanHydration = s.MeanHydration, TotalDeaths = s.TotalDeaths,
            });
        }

        foreach (Carcass k in w.Carcasses)
        {
            dto.Carcasses.Add(new CarcassDto
            {
                Id = k.Id, Species = k.SpeciesIndex, X = k.Position.X, Z = k.Position.Z, Heading = k.Heading,
                Cause = (int)k.Cause, DiedTick = k.DiedTick, MeatKg = k.MeatKg, InitialMeatKg = k.InitialMeatKg,
                RemainsKg = k.RemainsKg, InitialMassKg = k.InitialMassKg,
            });
        }

        foreach (SimEvent e in sim.Events.Recent)
        {
            dto.Events.Add(new EventDto
            {
                Tick = e.Tick, Day = e.Day, Hour = e.Hour, Type = (int)e.Type, CreatureId = e.CreatureId,
                SpeciesId = e.SpeciesId, Detail = e.Detail, X = e.Position.X, Z = e.Position.Z,
            });
        }
        return dto;
    }

    // ------------------------------------------------------------ 읽기

    private static byte[]? Decompress(byte[] fileBytes, out string? error)
    {
        error = null;
        if (fileBytes.Length > MaxFileBytes)
        {
            error = "파일이 너무 큽니다.";
            return null;
        }
        if (fileBytes.Length < 2 || fileBytes[0] != 0x1F || fileBytes[1] != 0x8B) return fileBytes;
        try
        {
            using var input = new GZipStream(new MemoryStream(fileBytes), CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > MaxDecompressedBytes)
                    {
                        error = "압축을 푼 크기가 한도를 넘습니다.";
                        return null;
                    }
                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return output.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            error = "압축 데이터가 손상되었습니다: " + e.Message;
            return null;
        }
    }

    /// <summary>머리말을 읽고 state의 바이트 범위와 체크섬을 확인한다.</summary>
    private static SaveHeader? Open(byte[] fileBytes, out ReadOnlyMemory<byte> state, out string? error)
    {
        state = default;
        byte[]? json = Decompress(fileBytes, out error);
        if (json == null) return null;

        var header = new SaveHeader();
        string format = "";
        int stateStart = -1, stateEnd = -1;
        bool hasSchema = false;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = MaxDepth });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                error = "세이브 파일 형식이 아닙니다.";
                return null;
            }
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string name = reader.GetString() ?? "";
                reader.Read();
                int start = (int)reader.TokenStartIndex;
                JsonTokenType type = reader.TokenType;
                switch (name)
                {
                    case "format" when type == JsonTokenType.String: format = reader.GetString() ?? ""; break;
                    case "schema_version" when type == JsonTokenType.Number && reader.TryGetInt32(out int v): header.SchemaVersion = v; hasSchema = true; break;
                    case "simulation_version" when type == JsonTokenType.String: header.SimulationVersion = reader.GetString() ?? ""; break;
                    case "world_seed" when type == JsonTokenType.String: header.WorldSeed = reader.GetString() ?? ""; break;
                    case "saved_at_utc" when type == JsonTokenType.String: header.SavedAtUtc = reader.GetString() ?? ""; break;
                    case "state_hash" when type == JsonTokenType.String: header.StateHash = reader.GetString() ?? ""; break;
                    case "payload_sha256" when type == JsonTokenType.String: header.PayloadSha256 = reader.GetString() ?? ""; break;
                    case "display_name" when type == JsonTokenType.String: header.DisplayName = reader.GetString() ?? ""; break;
                    case "day" when type == JsonTokenType.Number && reader.TryGetInt32(out int day): header.Day = day; break;
                    case "hour" when type == JsonTokenType.Number && reader.TryGetSingle(out float hour): header.Hour = hour; break;
                    case "creature_count" when type == JsonTokenType.Number && reader.TryGetInt32(out int n): header.CreatureCount = n; break;
                    case "tick" when type == JsonTokenType.Number && reader.TryGetInt64(out long tick): header.Tick = tick; break;
                }
                reader.Skip();
                if (name == "state" && type == JsonTokenType.StartObject)
                {
                    stateStart = start;
                    stateEnd = (int)reader.BytesConsumed;
                }
            }
        }
        catch (JsonException e)
        {
            error = "세이브 파일 JSON이 손상되었습니다: " + e.Message;
            return null;
        }

        if (format != Format)
        {
            error = "PALEOSPHERE 세이브 파일이 아닙니다.";
            return null;
        }
        if (!hasSchema || header.SchemaVersion < 1)
        {
            error = "schema_version이 없거나 잘못되었습니다.";
            return null;
        }
        if (stateStart < 0)
        {
            error = "state 항목이 없습니다.";
            return null;
        }

        state = new ReadOnlyMemory<byte>(json, stateStart, stateEnd - stateStart);
        string sha = Convert.ToHexString(SHA256.HashData(state.Span)).ToLowerInvariant();
        if (!string.Equals(sha, header.PayloadSha256, StringComparison.Ordinal))
        {
            error = "체크섬이 맞지 않습니다. 파일이 손상되었거나 수정되었습니다.";
            return null;
        }
        return header;
    }

    /// <summary>머리말만 읽는다. 형식·체크섬이 틀리면 null과 이유를 돌려준다.</summary>
    public static SaveHeader? ReadHeader(byte[] fileBytes, out string? error) => Open(fileBytes, out _, out error);

    public static LoadResult Deserialize(byte[] fileBytes, GameData data)
    {
        var result = new LoadResult();
        SaveHeader? header = Open(fileBytes, out ReadOnlyMemory<byte> state, out string? error);
        if (header == null)
        {
            result.Errors.Add(error ?? "알 수 없는 오류");
            return result;
        }
        result.Header = header;

        if (header.SchemaVersion > SimVersion.SaveSchema)
        {
            result.Errors.Add($"이 세이브는 더 새로운 버전(스키마 {header.SchemaVersion})에서 만들어졌습니다. 이 프로그램은 스키마 {SimVersion.SaveSchema}까지 읽을 수 있습니다.");
            return result;
        }
        if (!data.IsUsable || data.Forage == null)
        {
            result.Errors.Add("정의 데이터가 불완전해 세이브를 불러올 수 없습니다.");
            return result;
        }

        SaveStateDto? dto;
        try
        {
            if (header.SchemaVersion < SimVersion.SaveSchema)
            {
                if (!SaveMigrations.CanMigrate(header.SchemaVersion))
                {
                    result.Errors.Add($"스키마 {header.SchemaVersion} 세이브를 현재 스키마 {SimVersion.SaveSchema}(으)로 변환할 방법이 없습니다.");
                    return result;
                }
                JsonNode? node = JsonNode.Parse(state.Span, null, new JsonDocumentOptions { MaxDepth = MaxDepth });
                if (node is not JsonObject obj)
                {
                    result.Errors.Add("state가 객체가 아닙니다.");
                    return result;
                }
                obj = SaveMigrations.Upgrade(obj, header.SchemaVersion, result.Warnings);
                dto = obj.Deserialize<SaveStateDto>(JsonOptions);
            }
            else
            {
                dto = JsonSerializer.Deserialize<SaveStateDto>(state.Span, JsonOptions);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException or FormatException)
        {
            result.Errors.Add("state를 해석하지 못했습니다: " + e.Message);
            return result;
        }
        if (dto == null)
        {
            result.Errors.Add("state가 비어 있습니다.");
            return result;
        }

        SimulationCore? sim = Build(dto, data, result);
        if (sim == null || result.Errors.Count > 0) return result;

        if (header.SimulationVersion != SimVersion.Simulation)
        {
            result.Warnings.Add($"시뮬레이션 버전이 다릅니다(세이브 {header.SimulationVersion}, 현재 {SimVersion.Simulation}). 이어서 실행한 결과가 원래 버전과 달라질 수 있습니다.");
        }
        else if (header.StateHash.Length > 0 && StateHasher.ToHex(sim.ComputeStateHash()) != header.StateHash)
        {
            result.Warnings.Add("복원한 상태의 해시가 저장 시점과 다릅니다. 종·생물군계 정의 파일이 저장 이후 바뀌었을 수 있습니다.");
        }

        result.Sim = sim;
        return result;
    }

    // ------------------------------------------------------------ 검증과 복원

    private static bool ParseHex(string text, out ulong value) =>
        ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    private static bool Unpack(string base64, float[] target, string name, List<string> errors, float min, float max)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            errors.Add($"{name} 배열의 Base64가 잘못되었습니다.");
            return false;
        }
        if (bytes.Length != target.Length * sizeof(float))
        {
            errors.Add($"{name} 배열 길이가 맞지 않습니다(기대 {target.Length}개).");
            return false;
        }
        MemoryMarshal.Cast<byte, float>(bytes).CopyTo(target);
        for (int i = 0; i < target.Length; i++)
        {
            float v = target[i];
            if (Finite(v) && v >= min && v <= max) continue;
            errors.Add($"{name} 배열에 허용 범위를 벗어난 값이 있습니다(색인 {i}).");
            return false;
        }
        return true;
    }

    private static SimulationCore? Build(SaveStateDto dto, GameData data, LoadResult result)
    {
        List<string> errors = result.Errors;
        if (dto.Config == null || dto.Terrain == null || dto.Vegetation == null)
        {
            errors.Add("state에 config, terrain, vegetation 중 빠진 항목이 있습니다.");
            return null;
        }

        WorldConfigDto cd = dto.Config;
        if (!ulong.TryParse(cd.Seed, NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed))
        {
            errors.Add("config.seed가 잘못되었습니다.");
            return null;
        }
        var config = new WorldConfig
        {
            Seed = seed,
            SizeCells = cd.SizeCells,
            CellSize = cd.CellSize,
            Relief = cd.Relief,
            WaterFraction = cd.WaterFraction,
            River = cd.River,
            InitialVegetation = cd.InitialVegetation,
            VegetationCapacityScale = cd.VegetationCapacityScale,
            VegetationGrowthScale = cd.VegetationGrowthScale,
            MoistureBias = cd.MoistureBias,
            BaseTemperatureC = cd.BaseTemperatureC,
            SeasonalAmplitudeC = cd.SeasonalAmplitudeC,
            DiurnalAmplitudeC = cd.DiurnalAmplitudeC,
            TicksPerDay = cd.TicksPerDay,
            DaysPerSeason = cd.DaysPerSeason,
            StartHour = cd.StartHour,
        };
        List<string> configErrors = config.Validate();
        if (configErrors.Count > 0)
        {
            foreach (string e in configErrors) errors.Add("config: " + e);
            return null;
        }

        if (dto.Tick < 0 || dto.NextCreatureId < 1)
        {
            errors.Add("tick 또는 next_creature_id가 잘못되었습니다.");
            return null;
        }

        // 지형
        TerrainDto td = dto.Terrain;
        if (td.Size != config.SizeCells || td.CellSize != config.CellSize || !Finite(td.WaterLevel))
        {
            errors.Add("terrain 크기가 config와 맞지 않습니다.");
            return null;
        }
        var terrain = new TerrainGrid(td.Size, td.CellSize) { WaterLevel = td.WaterLevel };
        if (!Unpack(td.Height, terrain.Height, "terrain.height", errors, -10000f, 10000f)) return null;
        if (!Unpack(td.Moisture, terrain.Moisture, "terrain.moisture", errors, 0f, 1f)) return null;
        if (!Unpack(td.Fertility, terrain.Fertility, "terrain.fertility", errors, 0f, 1f)) return null;

        var biomeMap = new int[td.BiomeIds.Count];
        for (int i = 0; i < biomeMap.Length; i++)
        {
            if (data.Biomes.TryGetIndex(td.BiomeIds[i], out biomeMap[i])) continue;
            errors.Add($"세이브가 쓰는 생물군계 '{td.BiomeIds[i]}' 정의가 없습니다.");
            return null;
        }
        byte[] biomeBytes;
        try
        {
            biomeBytes = Convert.FromBase64String(td.Biome);
        }
        catch (FormatException)
        {
            errors.Add("terrain.biome 배열의 Base64가 잘못되었습니다.");
            return null;
        }
        if (biomeBytes.Length != terrain.CellCount)
        {
            errors.Add("terrain.biome 배열 길이가 맞지 않습니다.");
            return null;
        }
        for (int i = 0; i < biomeBytes.Length; i++)
        {
            if (biomeBytes[i] >= biomeMap.Length)
            {
                errors.Add("terrain.biome 배열에 정의되지 않은 색인이 있습니다.");
                return null;
            }
            terrain.Biome[i] = (byte)biomeMap[biomeBytes[i]];
        }
        terrain.RebuildDerived();

        // 식생
        var vegetation = new VegetationGrid(config.SizeCells);
        if (!Unpack(dto.Vegetation.Biomass, vegetation.Biomass, "vegetation.biomass", errors, 0f, 1e9f)) return null;
        vegetation.RebuildCapacity(terrain, data.Biomes, config);
        vegetation.InitialTotal = dto.Vegetation.InitialTotal;
        vegetation.TotalGrown = dto.Vegetation.TotalGrown;
        vegetation.TotalConsumed = dto.Vegetation.TotalConsumed;
        vegetation.TotalDieback = dto.Vegetation.TotalDieback;
        // 토양 영양은 스키마 2부터 있다. 비어 있으면 0이다.
        if (dto.Vegetation.Nutrient.Length > 0 && !Unpack(dto.Vegetation.Nutrient, vegetation.Nutrient, "vegetation.nutrient", errors, 0f, 1e9f)) return null;
        vegetation.TotalNutrientAdded = dto.Vegetation.TotalNutrientAdded;
        vegetation.TotalNutrientUsed = dto.Vegetation.TotalNutrientUsed;
        vegetation.TotalNutrientLeached = dto.Vegetation.TotalNutrientLeached;
        if (double.IsNaN(vegetation.InitialTotal + vegetation.TotalGrown + vegetation.TotalConsumed + vegetation.TotalDieback
            + vegetation.TotalNutrientAdded + vegetation.TotalNutrientUsed + vegetation.TotalNutrientLeached))
        {
            errors.Add("vegetation 누계 값이 잘못되었습니다.");
            return null;
        }

        // 종 색인 대응(저장 당시 색인 → 지금 색인)
        var speciesMap = new int[dto.SpeciesIds.Count];
        for (int i = 0; i < speciesMap.Length; i++)
        {
            if (data.Species.TryGetIndex(dto.SpeciesIds[i], out speciesMap[i])) continue;
            speciesMap[i] = -1;
        }

        var clock = new SimClock(config.TicksPerDay, config.DaysPerSeason, config.StartHour, dto.Tick);
        var world = new WorldState(config, clock, terrain, vegetation, data.Biomes, data.Species, data.Forage!);

        // 통계
        PopulationStats stats = BuildStats(dto.Stats, speciesMap, data.Species.Count, config, errors);
        var sim = new SimulationCore(world, stats) { NextCreatureId = dto.NextCreatureId };

        if (!ParseHex(dto.WorldRngState, out ulong rngState) || !ParseHex(dto.WorldRngInc, out ulong rngInc) || (rngInc & 1UL) == 0)
        {
            errors.Add("world_rng 상태가 잘못되었습니다.");
            return null;
        }
        sim.WorldRng = new Pcg32 { State = rngState, Inc = rngInc };

        foreach (ThreatDto t in dto.Threats)
        {
            if (!Finite(t.X) || !Finite(t.Z) || !Finite(t.Radius) || !Finite(t.Intensity)) continue;
            world.Threats.Add(new ThreatSource { Position = new Vec2(t.X, t.Z), Radius = t.Radius, Intensity = t.Intensity, UntilTick = t.UntilTick });
        }

        // 개체
        if (dto.Creatures.Count > SimulationCore.MaxCreatures)
        {
            errors.Add($"개체 수가 한도({SimulationCore.MaxCreatures})를 넘습니다.");
            return null;
        }
        var ids = new HashSet<int>();
        float size = terrain.WorldSize;
        for (int i = 0; i < dto.Creatures.Count; i++)
        {
            CreatureDto c = dto.Creatures[i];
            string where = $"creatures[{i}]";
            if (c.Id < 1 || c.Id >= dto.NextCreatureId || !ids.Add(c.Id))
            {
                errors.Add($"{where}: id {c.Id}이(가) 잘못되었거나 중복됩니다.");
                return null;
            }
            if (c.Species < 0 || c.Species >= speciesMap.Length || speciesMap[c.Species] < 0)
            {
                string name = c.Species >= 0 && c.Species < dto.SpeciesIds.Count ? dto.SpeciesIds[c.Species] : c.Species.ToString(CultureInfo.InvariantCulture);
                errors.Add($"{where}: 종 '{name}' 정의가 없습니다.");
                return null;
            }
            if (!Finite(c.X) || !Finite(c.Z) || c.X < 0 || c.Z < 0 || c.X > size || c.Z > size
                || !Finite(c.PrevX) || !Finite(c.PrevZ) || !Finite(c.Heading) || !Finite(c.PrevHeading) || !Finite(c.Speed)
                || !Finite(c.TargetX) || !Finite(c.TargetZ) || !Finite(c.ThreatX) || !Finite(c.ThreatZ))
            {
                errors.Add($"{where}: 위치 값이 세계 범위를 벗어났거나 잘못되었습니다.");
                return null;
            }
            if (!In01(c.Health) || !In01(c.Energy) || !In01(c.Hydration) || !In01(c.Fatigue) || !In01(c.Fear)
                || !Finite(c.AgeDays) || c.AgeDays < 0 || !Finite(c.LifespanDays) || c.LifespanDays < 0
                || !Finite(c.BoutAmount) || !Finite(c.StarveDamage) || !Finite(c.DehydrationDamage))
            {
                errors.Add($"{where}: 생리 값이 허용 범위를 벗어났습니다.");
                return null;
            }
            if (!InRange(c.TraitBodySize, 0.3f, 3f) || !InRange(c.TraitMetabolism, 0.3f, 3f) || !InRange(c.TraitSpeed, 0.3f, 3f))
            {
                errors.Add($"{where}: 형질 값이 허용 범위를 벗어났습니다.");
                return null;
            }
            if (c.Behavior < 0 || c.Behavior >= BehaviorKinds.Count || c.Activity < 0 || c.Activity > (int)ActivityKind.Attack
                || c.Sex < 0 || c.Sex > (int)Sex.Male || c.LifeStage < 0 || c.LifeStage > (int)LifeStage.Senior
                || c.Reason < 0 || c.Reason > (int)DecisionReason.SafetyThreat || (c.AvoidSide != 1 && c.AvoidSide != -1))
            {
                errors.Add($"{where}: 행동·상태 코드가 잘못되었습니다.");
                return null;
            }
            if (!ParseHex(c.RngState, out ulong cs) || !ParseHex(c.RngInc, out ulong ci) || (ci & 1UL) == 0)
            {
                errors.Add($"{where}: 난수 상태가 잘못되었습니다.");
                return null;
            }

            var cr = new Creature
            {
                Id = c.Id,
                SpeciesIndex = speciesMap[c.Species],
                Position = new Vec2(c.X, c.Z),
                PrevPosition = new Vec2(c.PrevX, c.PrevZ),
                Heading = c.Heading,
                PrevHeading = c.PrevHeading,
                CurrentSpeed = c.Speed,
                AgeDays = c.AgeDays,
                // 스키마 2 이전 세이브에는 수명이 없다. 종의 최대 수명으로 둔다.
                LifespanDays = c.LifespanDays > 0f ? c.LifespanDays : data.Species[speciesMap[c.Species]].MaxAgeDays,
                Sex = (Sex)c.Sex,
                LifeStage = (LifeStage)c.LifeStage,
                Health = c.Health,
                Energy = c.Energy,
                Hydration = c.Hydration,
                Fatigue = c.Fatigue,
                Fear = c.Fear,
                Behavior = (BehaviorKind)c.Behavior,
                Activity = (ActivityKind)c.Activity,
                BehaviorStartTick = c.BehaviorStartTick,
                HasTarget = c.HasTarget,
                Target = new Vec2(c.TargetX, c.TargetZ),
                TargetEntityId = c.TargetEntityId,
                NextRetargetTick = c.NextRetargetTick,
                ThreatPosition = new Vec2(c.ThreatX, c.ThreatZ),
                ThreatUntilTick = c.ThreatUntilTick,
                TraitBodySize = c.TraitBodySize,
                TraitMetabolism = c.TraitMetabolism,
                TraitSpeed = c.TraitSpeed,
                ParentA = c.ParentA,
                ParentB = c.ParentB,
                ChaseStartTick = c.ChaseStartTick,
                NextAttackTick = c.NextAttackTick,
                HuntCooldownUntilTick = c.HuntCooldownUntilTick,
                Kills = Math.Max(0, c.Kills),
                KilledBy = c.KilledBy,
                IgnoredCarcassId = c.IgnoredCarcassId,
                Rng = new Pcg32 { State = cs, Inc = ci },
                StarveDamage = c.StarveDamage,
                DehydrationDamage = c.DehydrationDamage,
                StuckTicks = Math.Max(0, c.StuckTicks),
                AvoidSide = (sbyte)c.AvoidSide,
                BoutAmount = c.BoutAmount,
                LastReason = (DecisionReason)c.Reason,
                LastDecisionTick = c.LastDecisionTick,
            };
            if (c.Scores.Length == BehaviorKinds.Count)
            {
                for (int k = 0; k < c.Scores.Length; k++) cr.LastScores[k] = Finite(c.Scores[k]) ? c.Scores[k] : 0f;
            }
            int logStart = Math.Max(0, c.Log.Count - Creature.MaxLogEntries);
            for (int k = logStart; k < c.Log.Count; k++)
            {
                LogDto l = c.Log[k];
                if (l.Behavior < 0 || l.Behavior >= BehaviorKinds.Count || l.Reason < 0 || l.Reason > (int)DecisionReason.SafetyThreat
                    || l.Result < 0 || l.Result > (int)BehaviorResult.Kill) continue;
                cr.Log.Add(new BehaviorLogEntry
                {
                    Tick = l.Tick, Behavior = (BehaviorKind)l.Behavior, Reason = (DecisionReason)l.Reason, Score = l.Score,
                    Energy = l.Energy, Hydration = l.Hydration, Fatigue = l.Fatigue, Fear = l.Fear,
                    Result = (BehaviorResult)l.Result, ResultAmount = l.ResultAmount, EndTick = l.EndTick,
                });
            }
            sim.RestoreCreature(cr);
        }
        sim.SortCreaturesById();

        // 사체
        var carcassIds = new HashSet<int>();
        for (int i = 0; i < dto.Carcasses.Count; i++)
        {
            CarcassDto k = dto.Carcasses[i];
            string where = $"carcasses[{i}]";
            if (!carcassIds.Add(k.Id) || k.Id < 1 || k.Id >= dto.NextCreatureId)
            {
                errors.Add($"{where}: id {k.Id}이(가) 잘못되었거나 중복됩니다.");
                return null;
            }
            if (k.Species < 0 || k.Species >= speciesMap.Length || speciesMap[k.Species] < 0)
            {
                errors.Add($"{where}: 종 정의가 없습니다.");
                return null;
            }
            if (!Finite(k.X) || !Finite(k.Z) || k.X < 0 || k.Z < 0 || k.X > size || k.Z > size || !Finite(k.Heading)
                || !InRange(k.InitialMassKg, 0f, 1e7f) || !InRange(k.RemainsKg, 0f, k.InitialMassKg * 1.0001f + 0.01f)
                || !InRange(k.MeatKg, 0f, k.RemainsKg + 0.01f) || !InRange(k.InitialMeatKg, 0f, k.InitialMassKg + 0.01f)
                || k.Cause < (int)DeathCause.Starvation || k.Cause > (int)DeathCause.OldAge)
            {
                errors.Add($"{where}: 값이 허용 범위를 벗어났습니다.");
                return null;
            }
            sim.RestoreCarcass(new Carcass
            {
                Id = k.Id, SpeciesIndex = speciesMap[k.Species], Position = new Vec2(k.X, k.Z), Heading = k.Heading,
                Cause = (DeathCause)k.Cause, DiedTick = k.DiedTick, MeatKg = k.MeatKg, InitialMeatKg = k.InitialMeatKg,
                RemainsKg = k.RemainsKg, InitialMassKg = k.InitialMassKg,
            });
        }
        if (!Finite((float)dto.CarcassMassCreated) || !Finite((float)dto.CarcassMassEaten) || !Finite((float)dto.CarcassMassDecomposed))
        {
            errors.Add("사체 질량 장부 값이 잘못되었습니다.");
            return null;
        }
        world.CarcassMassCreated = dto.CarcassMassCreated;
        world.CarcassMassEaten = dto.CarcassMassEaten;
        world.CarcassMassDecomposed = dto.CarcassMassDecomposed;

        // 사건 로그
        var events = new List<SimEvent>();
        foreach (EventDto e in dto.Events)
        {
            if (e.Type < 0 || e.Type > (int)SimEventType.Predation) continue;
            if (e.SpeciesId.Length > 64 || e.Detail.Length > 200 || !Finite(e.X) || !Finite(e.Z)) continue;
            events.Add(new SimEvent(e.Tick, e.Day, e.Hour, (SimEventType)e.Type, e.CreatureId, e.SpeciesId, e.Detail, new Vec2(e.X, e.Z)));
        }
        sim.Events.RestoreLog(events, dto.EventsTotal);
        return sim;
    }

    private static bool In01(float v) => Finite(v) && v >= 0f && v <= 1f;
    private static bool InRange(float v, float min, float max) => Finite(v) && v >= min && v <= max;

    private static PopulationStats BuildStats(StatsDto? dto, int[] speciesMap, int speciesCount, WorldConfig config, List<string> errors)
    {
        int defaultInterval = Math.Max(1, config.TicksPerDay / 48);
        if (dto == null) return new PopulationStats(speciesCount, defaultInterval);

        var stats = new PopulationStats(speciesCount, dto.SampleIntervalTicks > 0 ? dto.SampleIntervalTicks : defaultInterval);
        int savedSpecies = speciesMap.Length;
        int causes = BehaviorKinds.DeathCauseCount;
        if (dto.Deaths.Length == savedSpecies * causes)
        {
            for (int s = 0; s < savedSpecies; s++)
            {
                if (speciesMap[s] < 0) continue;
                for (int c = 0; c < causes; c++)
                    stats.Deaths[speciesMap[s] * causes + c] = Math.Max(0, dto.Deaths[s * causes + c]);
            }
        }
        if (dto.Spawned.Length == savedSpecies)
        {
            for (int s = 0; s < savedSpecies; s++)
                if (speciesMap[s] >= 0) stats.Spawned[speciesMap[s]] = Math.Max(0, dto.Spawned[s]);
        }
        int count = Math.Min(dto.Samples.Count, PopulationStats.MaxSamples);
        for (int i = dto.Samples.Count - count; i < dto.Samples.Count; i++)
        {
            SampleDto s = dto.Samples[i];
            if (s.Counts.Length != savedSpecies || !Finite(s.Day) || !Finite(s.VegetationKg)) continue;
            var counts = new int[speciesCount];
            for (int k = 0; k < savedSpecies; k++)
                if (speciesMap[k] >= 0) counts[speciesMap[k]] = Math.Max(0, s.Counts[k]);
            stats.Samples.Add(new StatSample
            {
                Tick = s.Tick, Day = s.Day, Counts = counts, VegetationKg = s.VegetationKg,
                CarcassMeatKg = Finite(s.CarcassMeatKg) ? Math.Max(0f, s.CarcassMeatKg) : 0f,
                MeanEnergy = s.MeanEnergy, MeanHydration = s.MeanHydration, TotalDeaths = s.TotalDeaths,
            });
        }
        return stats;
    }
}
