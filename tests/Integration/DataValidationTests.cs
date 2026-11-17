using System;
using System.IO;
using System.Linq;
using System.Text;
using PaleoSphere.Core;
using PaleoSphere.Creatures;
using PaleoSphere.Modding;
using PaleoSphere.World;
using Xunit;

namespace PaleoSphere.Tests.Integration;

/// <summary>TEST-13(손상 JSON·비정상 수치)과 TEST-14(근거 수준 표기).</summary>
public class DataValidationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "paleosphere-data-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string RepoFile(string relative) => File.ReadAllText(Path.Combine(TestData.DataDir, relative));

    [Fact]
    public void RepositoryData_LoadsWithoutErrorsOrWarnings()
    {
        GameData data = GameData.LoadFromDirectory(TestData.DataDir);
        Assert.True(data.IsUsable);
        Assert.Empty(data.Report.Errors);
        Assert.Empty(data.Report.Warnings);
        Assert.True(data.Species.Count >= 1);
        Assert.Empty(data.Biomes.MissingRequired());
        Assert.NotNull(data.Forage);
        Assert.True(data.Scenarios.Count >= 2);
    }

    [Fact]
    public void EverySpecies_SeparatesConfirmedInferredAndSimplified()
    {
        foreach (SpeciesDefinition s in TestData.Data.Species.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.ScientificName));
            Assert.False(string.IsNullOrWhiteSpace(s.Period));
            Assert.False(string.IsNullOrWhiteSpace(s.Range));
            Assert.NotEmpty(s.Sources);
            Assert.Contains(s.Facts, f => f.Evidence == EvidenceLevel.Confirmed);
            Assert.Contains(s.Facts, f => f.Evidence == EvidenceLevel.Inferred);
            // 속도·대사 등 게임용 수치가 실제 값이 아니라는 설명이 있어야 한다.
            Assert.Contains(s.Facts, f => f.Evidence == EvidenceLevel.Simplified);
            Assert.All(s.Facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Text)));
        }
    }

    [Fact]
    public void EveryBiomeAndPlant_IsMarkedAsSimplification()
    {
        Assert.All(TestData.Data.Biomes.All, b => Assert.Equal(EvidenceLevel.Simplified, b.Evidence));
        Assert.Equal(EvidenceLevel.Simplified, TestData.Data.Forage!.Evidence);
    }

    [Fact]
    public void Registries_AreSortedById_RegardlessOfFileOrder()
    {
        var a = new BiomeRegistry(TestData.Data.Biomes.All.Reverse());
        for (int i = 0; i < a.Count; i++) Assert.Equal(TestData.Data.Biomes[i].Id, a[i].Id);
    }

    public static TheoryData<string, string> BrokenSpecies()
    {
        string good = RepoFile("species/triceratops_horridus.json");
        return new TheoryData<string, string>
        {
            { "빈 문자열", "" },
            { "구문 오류", "{ \"id\": " },
            { "배열 최상위", "[1,2,3]" },
            { "잘린 파일", good.Substring(0, good.Length / 2) },
            { "id 누락", good.Replace("\"id\": \"triceratops_horridus\",", "") },
            { "id에 금지 문자", good.Replace("\"id\": \"triceratops_horridus\"", "\"id\": \"../etc/passwd\"") },
            { "음수 속도", good.Replace("\"walk_speed_mps\": 1.3", "\"walk_speed_mps\": -3") },
            { "터무니없이 큰 수", good.Replace("\"daily_food_kg\": 60", "\"daily_food_kg\": 1e30") },
            { "숫자 자리에 문자열", good.Replace("\"mass_kg\": 8000", "\"mass_kg\": \"heavy\"") },
            { "달리기가 걷기보다 느림", good.Replace("\"run_speed_mps\": 6.0", "\"run_speed_mps\": 0.5") },
            { "잘못된 식성", good.Replace("\"herbivore\"", "\"photosynthesis\"") },
            { "잘못된 근거 수준", good.Replace("\"evidence\": \"confirmed\"", "\"evidence\": \"definitely\"") },
            { "지원하지 않는 스키마", good.Replace("\"schema_version\": 1", "\"schema_version\": 99") },
            { "수명이 성숙 나이보다 짧음", good.Replace("\"max_age_days\": 400", "\"max_age_days\": 5") },
            { "생리 항목 누락", good.Replace("\"physiology\"", "\"physio\"") },
        };
    }

    [Theory]
    [MemberData(nameof(BrokenSpecies))]
    public void BrokenSpeciesFile_IsRejectedWithReason(string label, string text)
    {
        var report = new ValidationReport();
        SpeciesDefinition? s = DefinitionLoaders.ParseSpecies(text, "test.json", report);
        Assert.True(s == null, label + ": 잘못된 정의가 통과했습니다.");
        Assert.NotEmpty(report.Errors);
        Assert.All(report.Errors, e => Assert.StartsWith("[test.json]", e));
    }

    [Fact]
    public void SpeciesModel_IsParsed()
    {
        SpeciesDefinition sp = TestData.Data.Species[0];
        Assert.NotNull(sp.Model);
        Assert.StartsWith("res://assets/", sp.Model!.Path);
        Assert.Equal("CC0 1.0", sp.Model.License);
        Assert.True(sp.Model.LengthUnits > 0f && sp.Model.WalkUnitsPerSecond > 0f && sp.Model.RunUnitsPerSecond > sp.Model.WalkUnitsPerSecond);
        foreach (string key in new[] { "idle", "walk", "run" }) Assert.True(sp.Model.Animations.ContainsKey(key), key);
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(TestData.RepoRoot, sp.Model.Path.Substring("res://".Length))), "모델 파일이 저장소에 없습니다.");
    }

    public static TheoryData<string, string> BrokenModels()
    {
        string good = RepoFile("species/triceratops_horridus.json");
        const string path = "res://assets/models/dinosaurs/Triceratops.fbx";
        return new TheoryData<string, string>
        {
            { "저장소 밖 경로", good.Replace(path, "res://assets/../../etc/passwd.fbx") },
            { "assets 밖", good.Replace(path, "res://src/App/Main.fbx") },
            { "절대 경로", good.Replace(path, "C:/models/Triceratops.fbx") },
            { "허용하지 않는 확장자", good.Replace(path, "res://assets/models/dinosaurs/Triceratops.exe") },
            { "idle 애니메이션 누락", good.Replace("\"idle\": \"Triceratops_Idle\",", "") },
            { "음수 길이", good.Replace("\"length_units\": 19.4", "\"length_units\": -1") },
            { "라이선스 누락", good.Replace("\"license\": \"CC0 1.0\",", "") },
        };
    }

    [Theory]
    [MemberData(nameof(BrokenModels))]
    public void BrokenModelSection_IsRejected(string label, string text)
    {
        var report = new ValidationReport();
        SpeciesDefinition? s = DefinitionLoaders.ParseSpecies(text, "model.json", report);
        Assert.True(s == null, label + ": 잘못된 모델 정보가 통과했습니다.");
        Assert.NotEmpty(report.Errors);
    }

    [Fact]
    public void SpeciesWithoutModel_IsStillValid()
    {
        string good = RepoFile("species/triceratops_horridus.json");
        int start = good.IndexOf("\"model\":", System.StringComparison.Ordinal);
        int end = good.IndexOf("\"facts\":", System.StringComparison.Ordinal);
        string withoutModel = good.Remove(start, end - start);
        var report = new ValidationReport();
        SpeciesDefinition? s = DefinitionLoaders.ParseSpecies(withoutModel, "nomodel.json", report);
        Assert.NotNull(s);
        Assert.Null(s!.Model);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public void DeeplyNestedJson_IsRejected()
    {
        string deep = string.Concat(Enumerable.Repeat("{\"a\":", 200)) + "1" + new string('}', 200);
        var report = new ValidationReport();
        Assert.Null(DefinitionLoaders.ParseBiome(deep, "deep.json", report));
        Assert.NotEmpty(report.Errors);
    }

    [Fact]
    public void OversizedFile_IsRejectedBeforeParsing()
    {
        string big = "{\"id\":\"" + new string('a', JsonGuard.MaxDataFileBytes + 10) + "\"}";
        var report = new ValidationReport();
        Assert.Null(DefinitionLoaders.ParseSpecies(big, "big.json", report));
        Assert.Contains("너무 큽니다", report.Errors[0]);
    }

    [Fact]
    public void BiomeAndPlant_RangeChecks()
    {
        string biome = RepoFile("biomes/forest.json");
        string plant = RepoFile("plants/low_forage.json");
        var report = new ValidationReport();
        Assert.NotNull(DefinitionLoaders.ParseBiome(biome, "b", report));
        Assert.NotNull(DefinitionLoaders.ParsePlant(plant, "p", report));
        Assert.Empty(report.Errors);

        Assert.Null(DefinitionLoaders.ParseBiome(biome.Replace("0.75", "-1"), "b", report));
        Assert.Null(DefinitionLoaders.ParseBiome(biome.Replace("[0.17, 0.40, 0.19]", "[2, 0, 0]"), "b", report));
        Assert.Null(DefinitionLoaders.ParseBiome(biome.Replace("[0.17, 0.40, 0.19]", "\"green\""), "b", report));
        Assert.Null(DefinitionLoaders.ParsePlant(plant.Replace("\"optimal_low_c\": 16", "\"optimal_low_c\": 35"), "p", report));
        Assert.Null(DefinitionLoaders.ParsePlant(plant.Replace("\"min_moisture\": 0.12", "\"min_moisture\": 0.9"), "p", report));
        Assert.True(report.Errors.Count >= 5);
    }

    [Fact]
    public void Scenario_RangeAndSeedChecks()
    {
        string scenario = RepoFile("scenarios/lush_lowland.json");
        var report = new ValidationReport();
        var ok = DefinitionLoaders.ParseScenario(scenario, "s", report);
        Assert.NotNull(ok);
        Assert.Equal(20261116UL, ok!.World.Seed);
        Assert.Equal("scientific", ok.Mode);

        Assert.NotNull(DefinitionLoaders.ParseScenario(scenario.Replace("\"seed\": \"20261116\"", "\"seed\": 18446744073709551615"), "s", report));
        Assert.Empty(report.Errors);
        Assert.Null(DefinitionLoaders.ParseScenario(scenario.Replace("\"seed\": \"20261116\"", "\"seed\": -4"), "s", report));
        Assert.Null(DefinitionLoaders.ParseScenario(scenario.Replace("\"size_cells\": 256", "\"size_cells\": 100000"), "s", report));
        Assert.Null(DefinitionLoaders.ParseScenario(scenario.Replace("\"count\": 30", "\"count\": 0"), "s", report));
        Assert.Null(DefinitionLoaders.ParseScenario(scenario.Replace("\"mode\": \"scientific\"", "\"mode\": \"chaos\""), "s", report));
        Assert.Null(DefinitionLoaders.ParseScenario(scenario.Replace("\"world\"", "\"wrld\""), "s", report));
    }

    private void CopyData()
    {
        foreach (string file in Directory.GetFiles(TestData.DataDir, "*.json", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(TestData.DataDir, file);
            if (rel.StartsWith("schemas", StringComparison.Ordinal) || rel.StartsWith("i18n", StringComparison.Ordinal)) continue;
            string target = Path.Combine(_dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    [Fact]
    public void BadFilesAreSkipped_GoodOnesStillLoad()
    {
        CopyData();
        File.WriteAllText(Path.Combine(_dir, "species", "broken.json"), "{ this is not json");
        File.WriteAllText(Path.Combine(_dir, "species", "zz_duplicate.json"), RepoFile("species/triceratops_horridus.json"));
        File.WriteAllBytes(Path.Combine(_dir, "biomes", "binary.json"), new byte[] { 0, 159, 146, 150, 255 });
        File.WriteAllText(Path.Combine(_dir, "scenarios", "ghost.json"),
            RepoFile("scenarios/lush_lowland.json").Replace("\"id\": \"lush_lowland\"", "\"id\": \"ghost\"").Replace("triceratops_horridus", "ghost_species"));

        GameData data = GameData.LoadFromDirectory(_dir);
        Assert.True(data.IsUsable);
        Assert.Equal(1, data.Species.Count);
        Assert.Equal(TestData.Data.Biomes.Count, data.Biomes.Count);
        Assert.DoesNotContain(data.Scenarios, s => s.Id == "ghost");
        Assert.True(data.Report.Errors.Count >= 4);
        Assert.Contains(data.Report.Errors, e => e.Contains("broken.json"));
        Assert.Contains(data.Report.Errors, e => e.Contains("중복"));
        Assert.Contains(data.Report.Errors, e => e.Contains("ghost_species"));
        // 남은 정의로 세계를 만들 수 있어야 한다.
        Assert.NotNull(data.CreateSimulation(TestData.SmallConfig()));
    }

    [Fact]
    public void MissingRequiredData_MakesDataUnusable_ButDoesNotThrow()
    {
        CopyData();
        File.Delete(Path.Combine(_dir, "biomes", "water.json"));
        GameData data = GameData.LoadFromDirectory(_dir);
        Assert.False(data.IsUsable);
        Assert.Contains(data.Report.Errors, e => e.Contains("water"));
        Assert.Throws<InvalidOperationException>(() => data.CreateSimulation(TestData.SmallConfig()));

        GameData empty = GameData.LoadFromDirectory(Path.Combine(_dir, "does-not-exist"));
        Assert.False(empty.IsUsable);
        Assert.NotEmpty(empty.Report.Errors);
    }

    [Fact]
    public void UnknownPlacementBiome_IsWarned()
    {
        CopyData();
        string path = Path.Combine(_dir, "species", "triceratops_horridus.json");
        // 서식(줄바꿈)에 기대지 않도록 JSON 구조로 고친다.
        var node = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        ((System.Text.Json.Nodes.JsonArray)node["placement"]!["biomes"]!).Add("moon");
        File.WriteAllText(path, node.ToJsonString(), Encoding.UTF8);
        GameData data = GameData.LoadFromDirectory(_dir);
        Assert.True(data.IsUsable);
        Assert.Contains(data.Report.Warnings, w => w.Contains("moon"));
    }
}
