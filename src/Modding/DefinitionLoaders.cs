using System.Collections.Generic;
using System.Text.Json;
using PaleoSphere.Core;
using PaleoSphere.Creatures;
using PaleoSphere.Ecology;
using PaleoSphere.Simulation;
using PaleoSphere.World;

namespace PaleoSphere.Modding;

/// <summary>
/// data/ JSON을 정의 객체로 바꾼다. 형식·범위가 맞지 않으면 null을 돌려주고 이유를 report에 남긴다.
/// 허용 범위는 data/schemas/*.schema.json과 같게 유지한다.
/// </summary>
public static class DefinitionLoaders
{
    private static readonly float[] Gray = { 0.5f, 0.5f, 0.5f };

    public static BiomeDefinition? ParseBiome(string text, string source, ValidationReport report)
    {
        int before = report.Errors.Count;
        using JsonDocument? doc = JsonGuard.Parse(text, source, report);
        if (doc == null) return null;
        var f = new JsonFields(doc.RootElement, source, report);
        f.RequireSchemaVersion();
        string id = f.Id("id");
        f.Names("name", out string ko, out string en);
        float[] color = f.Color("color", Gray);
        float[] bare = f.Color("bare_color", Gray);
        var biome = new BiomeDefinition
        {
            Id = id,
            NameKo = ko,
            NameEn = en,
            Description = f.Str("description", 600, required: false),
            Evidence = f.Evidence(),
            VegetationCapacityKgM2 = f.Num("vegetation_capacity_kg_m2", 0f, 20f),
            GrowthFactor = f.Num("growth_factor", 0f, 10f, 1f),
            MoveSpeedFactor = f.Num("move_speed_factor", 0.1f, 2f, 1f),
            ColorR = color[0], ColorG = color[1], ColorB = color[2],
            BareR = bare[0], BareG = bare[1], BareB = bare[2],
        };
        return report.Errors.Count == before ? biome : null;
    }

    public static PlantDefinition? ParsePlant(string text, string source, ValidationReport report)
    {
        int before = report.Errors.Count;
        using JsonDocument? doc = JsonGuard.Parse(text, source, report);
        if (doc == null) return null;
        var f = new JsonFields(doc.RootElement, source, report);
        f.RequireSchemaVersion();
        string id = f.Id("id");
        f.Names("name", out string ko, out string en);
        var plant = new PlantDefinition
        {
            Id = id,
            NameKo = ko,
            NameEn = en,
            Description = f.Str("description", 600, required: false),
            Evidence = f.Evidence(),
            GrowthRatePerDay = f.Num("growth_rate_per_day", 0f, 20f),
            SeedFraction = f.Num("seed_fraction", 0f, 0.5f),
            DiebackRatePerDay = f.Num("dieback_rate_per_day", 0f, 10f),
            ResidualFraction = f.Num("residual_fraction", 0f, 0.9f),
            MinMoisture = f.Num("min_moisture", 0f, 1f),
            OptimalMoisture = f.Num("optimal_moisture", 0f, 1f),
            MinTemperatureC = f.Num("min_temperature_c", -40f, 60f),
            OptimalLowC = f.Num("optimal_low_c", -40f, 60f),
            OptimalHighC = f.Num("optimal_high_c", -40f, 60f),
            MaxTemperatureC = f.Num("max_temperature_c", -40f, 70f),
        };
        if (report.Errors.Count != before) return null;
        if (!(plant.MinMoisture < plant.OptimalMoisture))
            report.Error(source, "min_moisture는 optimal_moisture보다 작아야 합니다.");
        if (!(plant.MinTemperatureC < plant.OptimalLowC && plant.OptimalLowC <= plant.OptimalHighC && plant.OptimalHighC < plant.MaxTemperatureC))
            report.Error(source, "온도 값은 min < optimal_low ≤ optimal_high < max 순서여야 합니다.");
        return report.Errors.Count == before ? plant : null;
    }

    public static SpeciesDefinition? ParseSpecies(string text, string source, ValidationReport report)
    {
        int before = report.Errors.Count;
        using JsonDocument? doc = JsonGuard.Parse(text, source, report);
        if (doc == null) return null;
        var f = new JsonFields(doc.RootElement, source, report);
        f.RequireSchemaVersion();
        string id = f.Id("id");
        f.Names("name", out string ko, out string en);

        string dietText = f.Str("diet", 16);
        Diet diet = Diet.Herbivore;
        switch (dietText)
        {
            case "herbivore": diet = Diet.Herbivore; break;
            case "carnivore": diet = Diet.Carnivore; break;
            case "omnivore": diet = Diet.Omnivore; break;
            default:
                if (dietText.Length > 0) report.Error(source, $"'diet'는 herbivore, carnivore, omnivore 중 하나여야 합니다: \"{dietText}\"");
                break;
        }

        string period = "", range = "";
        EvidenceLevel periodEv = EvidenceLevel.Simplified, rangeEv = EvidenceLevel.Simplified;
        if (f.TryObj("period", out JsonFields p)) { period = p.Str("text", 200); periodEv = p.Evidence(); }
        if (f.TryObj("range", out JsonFields r)) { range = r.Str("text", 200); rangeEv = r.Evidence(); }

        float length = 1f, mass = 1f;
        EvidenceLevel bodyEv = EvidenceLevel.Inferred;
        if (f.TryObj("body", out JsonFields body))
        {
            length = body.Num("length_m", 0.1f, 60f);
            mass = body.Num("mass_kg", 0.01f, 150000f);
            bodyEv = body.Evidence();
        }

        float walk = 1f, run = 2f, turn = 60f, perception = 40f;
        if (f.TryObj("locomotion", out JsonFields loco))
        {
            loco.Evidence();
            walk = loco.Num("walk_speed_mps", 0.05f, 30f);
            run = loco.Num("run_speed_mps", 0.05f, 40f);
            turn = loco.Num("turn_rate_deg_per_sec", 5f, 720f, 60f);
            perception = loco.Num("perception_radius_m", 5f, 300f, 40f);
            if (run < walk) report.Error(source, "run_speed_mps는 walk_speed_mps 이상이어야 합니다.");
        }

        float dailyFood = 1f, reserve = 1f, intake = 1f, hydReserve = 1f, drinkHours = 1f;
        float awake = 16f, rest = 6f, starve = 4f, dehydrate = 1.5f, regen = 0.25f;
        if (f.TryObj("physiology", out JsonFields ph))
        {
            ph.Evidence();
            dailyFood = ph.Num("daily_food_kg", 0.001f, 5000f);
            reserve = ph.Num("energy_reserve_days", 0.2f, 200f);
            intake = ph.Num("intake_kg_per_hour", 0.001f, 2000f);
            hydReserve = ph.Num("hydration_reserve_days", 0.1f, 100f);
            drinkHours = ph.Num("drink_hours_to_full", 0.01f, 24f);
            awake = ph.Num("awake_hours", 1f, 200f);
            rest = ph.Num("rest_hours", 0.5f, 100f);
            starve = ph.Num("starve_health_days", 0.1f, 100f);
            dehydrate = ph.Num("dehydration_health_days", 0.05f, 100f);
            regen = ph.Num("health_regen_per_day", 0f, 10f);
        }

        float adult = 0f, maxAge = 100f, hatchLength = 0.1f, subadult = -1f, senescence = 0.8f, lifespanVariation = 0.15f;
        if (f.TryObj("life_history", out JsonFields life))
        {
            life.Evidence();
            adult = life.Num("adult_age_days", 0f, 100000f);
            maxAge = life.Num("max_age_days", 1f, 1000000f);
            if (maxAge <= adult) report.Error(source, "max_age_days는 adult_age_days보다 커야 합니다.");
            hatchLength = life.Num("hatch_length_fraction", 0.02f, 1f, 0.1f);
            subadult = life.Num("subadult_age_days", 0f, 100000f, -1f);
            senescence = life.Num("senescence_fraction", 0.3f, 1f, 0.8f);
            lifespanVariation = life.Num("lifespan_variation", 0f, 0.5f, 0.15f);
            if (subadult > adult) report.Error(source, "subadult_age_days는 adult_age_days보다 클 수 없습니다.");
            // 노년이 성체가 되기 전에 시작하면 생애 단계가 꼬인다(가장 짧은 수명 기준으로 검사).
            if (maxAge * (1f - lifespanVariation) * senescence < adult)
                report.Error(source, "가장 짧은 수명에서도 노년(수명 × senescence_fraction)이 adult_age_days 뒤에 와야 합니다.");
        }
        if (subadult < 0f) subadult = adult * 0.35f;

        var biomes = new List<string>();
        if (f.TryObj("placement", out JsonFields placement, required: false))
        {
            foreach (JsonElement e in placement.Array("biomes", 32))
            {
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 and <= 64 } s) biomes.Add(s);
                else report.Error(source, "'placement.biomes'의 값은 생물군계 id 문자열이어야 합니다.");
            }
        }

        string shape = "quadruped";
        float[] color = Gray;
        if (f.TryObj("proxy", out JsonFields proxy, required: false))
        {
            proxy.Evidence();
            shape = proxy.Str("shape", 32, required: false, fallback: "quadruped");
            color = proxy.Color("color", Gray);
        }

        float attackSuccess = 0f, defense = 0f, maxChase = 40f, smell = 120f;
        if (f.TryObj("predation", out JsonFields pred, required: false))
        {
            pred.Evidence();
            attackSuccess = pred.Num("attack_success", 0f, 1f, 0f);
            defense = pred.Num("defense", 0f, 1f, 0f);
            maxChase = pred.Num("max_chase_seconds", 1f, 600f, 40f);
            smell = pred.Num("smell_radius_m", 0f, 1000f, 120f);
            if (attackSuccess > 0f && diet == Diet.Herbivore)
                report.Error(source, "초식동물에는 'predation.attack_success'를 줄 수 없습니다.");
        }

        SpeciesModel? model = null;
        if (f.TryObj("model", out JsonFields mf, required: false))
            model = ParseModel(mf, doc.RootElement.GetProperty("model"), source, report);

        var facts = new List<FactEntry>();
        List<JsonElement> factItems = f.Array("facts", 64);
        for (int i = 0; i < factItems.Count; i++)
        {
            if (factItems[i].ValueKind != JsonValueKind.Object)
            {
                report.Error(source, $"'facts[{i}]'은(는) 객체여야 합니다.");
                continue;
            }
            JsonFields fact = f.Child(factItems[i], "facts", i);
            facts.Add(new FactEntry(fact.Str("topic", 80), fact.Str("text", 600), fact.Evidence()));
        }

        var sources = new List<SourceEntry>();
        List<JsonElement> sourceItems = f.Array("sources", 64);
        for (int i = 0; i < sourceItems.Count; i++)
        {
            if (sourceItems[i].ValueKind != JsonValueKind.Object)
            {
                report.Error(source, $"'sources[{i}]'은(는) 객체여야 합니다.");
                continue;
            }
            JsonFields src = f.Child(sourceItems[i], "sources", i);
            sources.Add(new SourceEntry(src.Str("title", 300), src.Str("note", 600, required: false)));
        }

        if (report.Errors.Count != before) return null;
        return new SpeciesDefinition
        {
            Id = id,
            ScientificName = f.Str("scientific_name", 120),
            NameKo = ko,
            NameEn = en,
            Diet = diet,
            Period = period,
            PeriodEvidence = periodEv,
            Range = range,
            RangeEvidence = rangeEv,
            BodyLengthM = length,
            BodyMassKg = mass,
            BodyEvidence = bodyEv,
            WalkSpeedMps = walk,
            RunSpeedMps = run,
            TurnRateDegPerSec = turn,
            PerceptionRadiusM = perception,
            DailyFoodKg = dailyFood,
            EnergyReserveDays = reserve,
            IntakeKgPerHour = intake,
            HydrationReserveDays = hydReserve,
            DrinkHoursToFull = drinkHours,
            AwakeHours = awake,
            RestHours = rest,
            StarveHealthDays = starve,
            DehydrationHealthDays = dehydrate,
            HealthRegenPerDay = regen,
            AdultAgeDays = adult,
            MaxAgeDays = maxAge,
            HatchLengthFraction = hatchLength,
            SubadultAgeDays = subadult,
            SenescenceFraction = senescence,
            LifespanVariation = lifespanVariation,
            PlacementBiomes = biomes,
            ProxyShape = shape,
            ProxyColorR = color[0],
            ProxyColorG = color[1],
            ProxyColorB = color[2],
            Model = model,
            AttackSuccess = attackSuccess,
            Defense = defense,
            MaxChaseSeconds = maxChase,
            SmellRadiusM = smell,
            Facts = facts,
            Sources = sources,
        };
    }

    /// <summary>허용하는 모델 파일 확장자.</summary>
    public static readonly string[] ModelExtensions = { ".fbx", ".glb", ".gltf", ".tscn", ".scn" };

    public static readonly string[] ModelAnimationKeys = { "idle", "walk", "run", "death", "attack" };

    private static SpeciesModel? ParseModel(JsonFields m, JsonElement element, string source, ValidationReport report)
    {
        int before = report.Errors.Count;
        string path = m.Str("path", 200);
        // 데이터 파일이 임의 경로를 읽지 못하게 res://assets/ 아래로 한정한다.
        bool extOk = false;
        foreach (string ext in ModelExtensions)
            if (path.EndsWith(ext, System.StringComparison.OrdinalIgnoreCase)) extOk = true;
        if (path.Length > 0 && (!path.StartsWith("res://assets/", System.StringComparison.Ordinal) || path.Contains("..") || path.Contains('\\') || !extOk))
            report.Error(source, $"'model.path'는 res://assets/ 아래의 {string.Join(", ", ModelExtensions)} 파일이어야 합니다: \"{path}\"");

        var animations = new Dictionary<string, string>(System.StringComparer.Ordinal);
        if (m.TryObj("animations", out JsonFields anims))
        {
            foreach (string key in ModelAnimationKeys)
            {
                string name = anims.Str(key, 100, required: key is "idle" or "walk" or "run");
                if (name.Length > 0) animations[key] = name;
            }
        }
        if (element.TryGetProperty("animations", out JsonElement animEl) && animEl.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in animEl.EnumerateObject())
                if (System.Array.IndexOf(ModelAnimationKeys, prop.Name) < 0)
                    report.Warn(source, $"'model.animations.{prop.Name}'은(는) 쓰이지 않는 동작 이름입니다.");
        }

        var model = new SpeciesModel
        {
            Path = path,
            Source = m.Str("source", 300),
            License = m.Str("license", 100),
            Evidence = m.Evidence(),
            LengthUnits = m.Num("length_units", 0.001f, 100000f),
            WalkUnitsPerSecond = m.Num("walk_units_per_second", 0.001f, 100000f),
            RunUnitsPerSecond = m.Num("run_units_per_second", 0.001f, 100000f),
            Animations = animations,
        };
        return report.Errors.Count == before ? model : null;
    }

    public static WorldConfig? ParseWorldConfig(JsonElement element, string source, ValidationReport report, string path = "world")
    {
        int before = report.Errors.Count;
        var w = new JsonFields(element, source, report, path);
        var d = new WorldConfig();

        ulong seed = d.Seed;
        if (element.TryGetProperty("seed", out JsonElement seedEl))
        {
            // 시드는 64비트 정수라 문자열 표기도 받는다(JSON 숫자는 2^53을 넘으면 정밀도를 잃을 수 있다).
            bool ok = seedEl.ValueKind == JsonValueKind.String
                ? ulong.TryParse(seedEl.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out seed)
                : seedEl.ValueKind == JsonValueKind.Number && seedEl.TryGetUInt64(out seed);
            if (!ok) report.Error(source, $"'{path}.seed'는 0 이상의 64비트 정수(숫자 또는 문자열)여야 합니다.");
        }

        var cfg = new WorldConfig
        {
            Seed = seed,
            SizeCells = w.Int("size_cells", 32, 1024, d.SizeCells),
            CellSize = w.Num("cell_size", 1f, 8f, d.CellSize),
            Relief = w.Num("relief", 0f, 80f, d.Relief),
            WaterFraction = w.Num("water_fraction", 0f, 0.5f, d.WaterFraction),
            River = w.Bool("river", d.River),
            InitialVegetation = w.Num("initial_vegetation", 0f, 1f, d.InitialVegetation),
            VegetationCapacityScale = w.Num("vegetation_capacity_scale", 0f, 5f, d.VegetationCapacityScale),
            VegetationGrowthScale = w.Num("vegetation_growth_scale", 0f, 5f, d.VegetationGrowthScale),
            MoistureBias = w.Num("moisture_bias", -0.6f, 0.6f, d.MoistureBias),
            BaseTemperatureC = w.Num("base_temperature_c", -10f, 45f, d.BaseTemperatureC),
            SeasonalAmplitudeC = w.Num("seasonal_amplitude_c", 0f, 25f, d.SeasonalAmplitudeC),
            DiurnalAmplitudeC = w.Num("diurnal_amplitude_c", 0f, 20f, d.DiurnalAmplitudeC),
            TicksPerDay = w.Int("ticks_per_day", 600, 864000, d.TicksPerDay),
            DaysPerSeason = w.Int("days_per_season", 1, 365, d.DaysPerSeason),
            StartHour = w.Num("start_hour", 0f, 23.99f, d.StartHour),
        };
        return report.Errors.Count == before ? cfg : null;
    }

    public static ScenarioDefinition? ParseScenario(string text, string source, ValidationReport report)
    {
        int before = report.Errors.Count;
        using JsonDocument? doc = JsonGuard.Parse(text, source, report);
        if (doc == null) return null;
        var f = new JsonFields(doc.RootElement, source, report);
        f.RequireSchemaVersion();
        string id = f.Id("id");
        f.Names("name", out string ko, out string en);
        string mode = f.Str("mode", 16, required: false, fallback: "sandbox");
        if (mode != "sandbox" && mode != "scientific")
            report.Error(source, $"'mode'는 sandbox 또는 scientific이어야 합니다: \"{mode}\"");

        WorldConfig? world = null;
        if (doc.RootElement.TryGetProperty("world", out JsonElement worldEl) && worldEl.ValueKind == JsonValueKind.Object)
            world = ParseWorldConfig(worldEl, source, report);
        else
            report.Error(source, "'world' 객체가 없습니다.");

        var pops = new List<ScenarioPopulation>();
        List<JsonElement> items = f.Array("populations", 64);
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].ValueKind != JsonValueKind.Object)
            {
                report.Error(source, $"'populations[{i}]'은(는) 객체여야 합니다.");
                continue;
            }
            JsonFields pop = f.Child(items[i], "populations", i);
            pops.Add(new ScenarioPopulation(pop.Id("species"), pop.Int("count", 1, SimulationCore.MaxSpawnPerCommand)));
        }

        if (report.Errors.Count != before || world == null) return null;
        return new ScenarioDefinition
        {
            Id = id,
            NameKo = ko,
            NameEn = en,
            Description = f.Str("description", 600, required: false),
            Mode = mode,
            World = world,
            Populations = pops,
        };
    }
}
