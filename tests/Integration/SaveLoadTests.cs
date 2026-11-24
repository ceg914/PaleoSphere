using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using PaleoSphere.Core;
using PaleoSphere.Creatures;
using PaleoSphere.Persistence;
using PaleoSphere.Simulation;
using Xunit;

namespace PaleoSphere.Tests.Integration;

/// <summary>TEST-07(저장 왕복), TEST-08(버전 호환), TEST-13 중 세이브 손상 처리.</summary>
public class SaveLoadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "paleosphere-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static SimulationCore Busy(int ticks = 2500)
    {
        SimulationCore sim = TestData.NewSim(seed: 31, creatures: 18);
        TestData.Run(sim, ticks);
        sim.Enqueue(new DisturbanceCommand(sim.Creatures[0].Position, 30f, 1f));
        TestData.Run(sim, 20);
        return sim;
    }

    private static SimulationCore Load(byte[] bytes)
    {
        LoadResult r = SaveSerializer.Deserialize(bytes, TestData.Data);
        Assert.True(r.Ok, string.Join(" / ", r.Errors));
        return r.Sim!;
    }

    [Fact]
    public void RoundTrip_RestoresIdenticalState()
    {
        SimulationCore original = Busy();
        LoadResult result = SaveSerializer.Deserialize(SaveSerializer.Serialize(original, "왕복"), TestData.Data);
        Assert.True(result.Ok, string.Join(" / ", result.Errors));
        Assert.Empty(result.Warnings);
        SimulationCore loaded = result.Sim!;

        Assert.Equal(original.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.Equal(original.Clock.Tick, loaded.Clock.Tick);
        Assert.Equal(original.Clock.Day, loaded.Clock.Day);
        Assert.Equal(original.NextCreatureId, loaded.NextCreatureId);
        Assert.Equal(original.WorldRng.State, loaded.WorldRng.State);
        Assert.Equal(original.CreatureCount, loaded.CreatureCount);
        for (int i = 0; i < original.CreatureCount; i++)
        {
            Creature a = original.Creatures[i], b = loaded.Creatures[i];
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Position, b.Position);
            Assert.Equal(a.Energy, b.Energy);
            Assert.Equal(a.Hydration, b.Hydration);
            Assert.Equal(a.Rng.State, b.Rng.State);
            Assert.Equal(a.Behavior, b.Behavior);
            Assert.Equal(a.Log.Count, b.Log.Count);
        }
        Assert.Equal(original.World.Vegetation.Biomass, loaded.World.Vegetation.Biomass);
        Assert.Equal(original.World.Terrain.Height, loaded.World.Terrain.Height);
        Assert.Equal(original.Stats.Samples.Count, loaded.Stats.Samples.Count);
        Assert.Equal(original.Stats.Spawned, loaded.Stats.Spawned);
        Assert.Equal(original.Events.Recent.Count, loaded.Events.Recent.Count);
        Assert.Equal(original.Events.TotalPublished, loaded.Events.TotalPublished);
        Assert.Equal(original.World.Threats.Count, loaded.World.Threats.Count);
    }

    [Fact]
    public void LoadedWorld_ContinuesExactlyLikeTheOriginal()
    {
        SimulationCore original = Busy();
        SimulationCore loaded = Load(SaveSerializer.Serialize(original, "이어하기"));
        for (int i = 0; i < 6; i++)
        {
            TestData.Run(original, 700);
            TestData.Run(loaded, 700);
            Assert.Equal(original.ComputeStateHash(), loaded.ComputeStateHash());
        }
        // 불러온 뒤의 배치도 같은 결과를 내야 한다(세계 난수 상태 복원 확인).
        var center = new Vec2(60, 60);
        original.Enqueue(new SpawnCreaturesCommand(TestData.Herbivore, 5, center, 30f));
        loaded.Enqueue(new SpawnCreaturesCommand(TestData.Herbivore, 5, center, 30f));
        TestData.Run(original, 300);
        TestData.Run(loaded, 300);
        Assert.Equal(original.ComputeStateHash(), loaded.ComputeStateHash());
    }

    [Fact]
    public void RepeatedSaveLoad_KeepsIdsValid()
    {
        SimulationCore sim = TestData.NewSim(seed: 8, creatures: 12);
        for (int round = 0; round < 8; round++)
        {
            TestData.Run(sim, 150);
            sim.Enqueue(new RemoveCreatureCommand(sim.Creatures[round % sim.CreatureCount].Id));
            sim.Enqueue(new SpawnCreaturesCommand(TestData.Herbivore, 2, new Vec2(64, 64), 40f));
            sim.ApplyPendingCommands();
            int maxBefore = sim.Creatures.Max(c => c.Id);
            sim = Load(SaveSerializer.Serialize(sim, "반복"));
            Assert.Equal(sim.CreatureCount, sim.Creatures.Select(c => c.Id).Distinct().Count());
            Assert.True(sim.NextCreatureId > maxBefore);
            foreach (Creature c in sim.Creatures) Assert.True(sim.TryGetCreature(c.Id, out _));
        }
    }

    [Fact]
    public void Header_CarriesVersionSeedAndSummary()
    {
        SimulationCore sim = Busy();
        SaveHeader? h = SaveSerializer.ReadHeader(SaveSerializer.Serialize(sim, "머리말 시험"), out string? error);
        Assert.Null(error);
        Assert.NotNull(h);
        Assert.Equal(SimVersion.SaveSchema, h!.SchemaVersion);
        Assert.Equal(SimVersion.Simulation, h.SimulationVersion);
        Assert.Equal("31", h.WorldSeed);
        Assert.Equal("머리말 시험", h.DisplayName);
        Assert.Equal(sim.CreatureCount, h.CreatureCount);
        Assert.Equal(sim.Clock.Tick, h.Tick);
        Assert.Equal(StateHasher.ToHex(sim.ComputeStateHash()), h.StateHash);
        Assert.Equal(64, h.PayloadSha256.Length);
    }

    [Fact]
    public void Store_WritesAtomically_AndKeepsThreeBackups()
    {
        var store = new SaveFileStore(_dir);
        SimulationCore sim = TestData.NewSim(creatures: 4);
        string path = "";
        for (int i = 0; i < 6; i++)
        {
            TestData.Run(sim, 50);
            path = store.Write("slot one", SaveSerializer.Serialize(sim, "v" + i));
        }
        Assert.EndsWith("slot_one.psave", path);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.True(File.Exists(path + ".bak1") && File.Exists(path + ".bak2") && File.Exists(path + ".bak3"));
        Assert.False(File.Exists(path + ".bak4"));
        Assert.Equal(4, store.CandidatePaths("slot one").Count);
        Assert.Equal("v5", SaveSerializer.ReadHeader(store.Read(path), out _)!.DisplayName);
        Assert.Equal("v4", SaveSerializer.ReadHeader(store.Read(path + ".bak1"), out _)!.DisplayName);
        Assert.Equal("v2", SaveSerializer.ReadHeader(store.Read(path + ".bak3"), out _)!.DisplayName);

        var list = store.List();
        Assert.Single(list);
        Assert.Equal("slot_one", list[0].Slot);
        Assert.NotNull(list[0].Header);
    }

    [Fact]
    public void Store_RefusesToReplaceGoodSaveWithGarbage()
    {
        var store = new SaveFileStore(_dir);
        SimulationCore sim = TestData.NewSim(creatures: 4);
        string path = store.Write("keep", SaveSerializer.Serialize(sim, "good"));
        byte[] good = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => store.Write("keep", Encoding.UTF8.GetBytes("이건 세이브가 아니다")));
        Assert.Equal(good, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Store_CorruptedMainFile_BackupStillLoads()
    {
        var store = new SaveFileStore(_dir);
        SimulationCore sim = TestData.NewSim(creatures: 4);
        store.Write("slot", SaveSerializer.Serialize(sim, "first"));
        TestData.Run(sim, 100);
        string path = store.Write("slot", SaveSerializer.Serialize(sim, "second"));
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });

        var candidates = store.CandidatePaths("slot");
        Assert.False(SaveSerializer.Deserialize(store.Read(candidates[0]), TestData.Data).Ok);
        LoadResult backup = SaveSerializer.Deserialize(store.Read(candidates[1]), TestData.Data);
        Assert.True(backup.Ok);
        Assert.Equal("first", backup.Header!.DisplayName);
        Assert.NotNull(store.List()[0].Error);
    }

    [Theory]
    [InlineData("../../evil", "evil")]
    [InlineData("C:\\Windows\\x", "CWindowsx")]
    [InlineData("  내 세계 1  ", "내_세계_1")]
    [InlineData("///", "save")]
    public void Store_SanitizesSlotNames(string input, string expected)
    {
        Assert.Equal(expected, SaveFileStore.SanitizeSlot(input));
        Assert.StartsWith(_dir, new SaveFileStore(_dir).PathFor(input));
    }

    // ------------------------------------------------------------ 손상·비정상 데이터

    public static TheoryData<byte[]> Garbage() => new()
    {
        Array.Empty<byte>(),
        new byte[] { 0x1F, 0x8B },
        new byte[] { 0x1F, 0x8B, 8, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3 },
        Encoding.UTF8.GetBytes("not json"),
        Encoding.UTF8.GetBytes("[]"),
        Encoding.UTF8.GetBytes("{}"),
        Encoding.UTF8.GetBytes("{\"format\":\"other\",\"schema_version\":1,\"state\":{}}"),
        Encoding.UTF8.GetBytes("{\"format\":\"paleosphere-save\",\"schema_version\":1}"),
        Encoding.UTF8.GetBytes("{\"format\":\"paleosphere-save\",\"schema_version\":1,\"payload_sha256\":\"00\",\"state\":{}}"),
        Encoding.UTF8.GetBytes(new string('[', 5000)),
        Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray(),
    };

    [Theory]
    [MemberData(nameof(Garbage))]
    public void Garbage_IsRejectedWithoutThrowing(byte[] bytes)
    {
        LoadResult r = SaveSerializer.Deserialize(bytes, TestData.Data);
        Assert.False(r.Ok);
        Assert.Null(r.Sim);
        Assert.NotEmpty(r.Errors);
        Assert.Null(SaveSerializer.ReadHeader(bytes, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void FlippedByte_IsCaughtByChecksum()
    {
        byte[] file = SaveSerializer.Serialize(Busy(300), "x");
        byte[] json = Gunzip(file);
        int idx = Encoding.UTF8.GetString(json).IndexOf("\"next_creature_id\":", StringComparison.Ordinal) + 19;
        json[idx] = json[idx] == (byte)'9' ? (byte)'8' : (byte)'9';
        LoadResult r = SaveSerializer.Deserialize(json, TestData.Data);
        Assert.False(r.Ok);
        Assert.Contains("체크섬", r.Errors[0]);
    }

    [Fact]
    public void TruncatedFile_IsRejected()
    {
        byte[] file = SaveSerializer.Serialize(Busy(300), "x");
        for (int cut = 1; cut < 6; cut++)
        {
            byte[] part = file.Take(file.Length * cut / 6).ToArray();
            Assert.False(SaveSerializer.Deserialize(part, TestData.Data).Ok);
        }
    }

    private static byte[] Gunzip(byte[] bytes)
    {
        using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>정상 세이브의 state를 고친 뒤 체크섬이 맞는 파일로 다시 싼다.</summary>
    private static byte[] Tamper(Action<JsonObject> edit, Action<SaveHeader>? editHeader = null)
    {
        byte[] file = SaveSerializer.Serialize(Busy(300), "tamper");
        byte[] state = SaveSerializer.ExtractState(file, out SaveHeader? header, out string? error)!;
        Assert.Null(error);
        var node = (JsonObject)JsonNode.Parse(state)!;
        edit(node);
        editHeader?.Invoke(header!);
        return SaveSerializer.BuildFile(Encoding.UTF8.GetBytes(node.ToJsonString()), header!);
    }

    private static JsonObject FirstCreature(JsonObject state) => (JsonObject)((JsonArray)state["creatures"]!)[0]!;

    public static TheoryData<string, Action<JsonObject>> BadStates() => new()
    {
        { "에너지 범위 초과", s => FirstCreature(s)["energy"] = 5.0 },
        { "음수 건강", s => FirstCreature(s)["health"] = -0.5 },
        { "세계 밖 위치", s => FirstCreature(s)["x"] = 1e9 },
        { "중복 id", s => FirstCreature(s)["id"] = ((JsonArray)s["creatures"]!)[1]!["id"]!.GetValue<int>() },
        { "0 이하 id", s => FirstCreature(s)["id"] = 0 },
        { "없는 종 색인", s => FirstCreature(s)["species"] = 99 },
        { "알 수 없는 종 id", s => ((JsonArray)s["species_ids"]!)[0] = "unknown_dino" },
        { "잘못된 행동 코드", s => FirstCreature(s)["behavior"] = 77 },
        { "잘못된 난수 상태", s => FirstCreature(s)["rng_state"] = "zz" },
        { "짝수 난수 증분", s => FirstCreature(s)["rng_inc"] = "0000000000000002" },
        { "형질 범위 초과", s => FirstCreature(s)["trait_speed"] = 50.0 },
        { "음수 틱", s => s["tick"] = -5 },
        { "지형 크기 불일치", s => s["terrain"]!["size"] = 32 },
        { "높이 배열 길이 불일치", s => s["terrain"]!["height"] = "AAAA" },
        { "Base64 아님", s => s["vegetation"]!["biomass"] = "###" },
        { "알 수 없는 생물군계", s => ((JsonArray)s["terrain"]!["biome_ids"]!)[0] = "lava" },
        { "설정 범위 초과", s => s["config"]!["cell_size"] = 500.0 },
        { "설정 누락", s => s.Remove("config") },
        { "시드 형식 오류", s => s["config"]!["seed"] = "-1" },
        { "문자열 자리에 숫자", s => s["world_rng_state"] = 12 },
    };

    [Theory]
    [MemberData(nameof(BadStates))]
    public void InvalidStateValues_AreRejectedWithMessage(string label, Action<JsonObject> edit)
    {
        LoadResult r = SaveSerializer.Deserialize(Tamper(edit), TestData.Data);
        Assert.False(r.Ok, label + ": 잘못된 값이 그대로 통과했습니다.");
        Assert.Null(r.Sim);
        Assert.NotEmpty(r.Errors);
    }

    [Fact]
    public void TooManyCreatures_IsRejected()
    {
        byte[] file = Tamper(s =>
        {
            var arr = (JsonArray)s["creatures"]!;
            string template = arr[0]!.ToJsonString();
            for (int i = 0; i < SimulationCore.MaxCreatures + 5; i++)
            {
                var copy = (JsonObject)JsonNode.Parse(template)!;
                copy["id"] = 100000 + i;
                arr.Add(copy);
            }
            s["next_creature_id"] = 900000;
        });
        LoadResult r = SaveSerializer.Deserialize(file, TestData.Data);
        Assert.False(r.Ok);
        Assert.Contains("한도", r.Errors[0]);
    }

    // ------------------------------------------------------------ 버전 호환

    [Fact]
    public void NewerSchema_GivesClearError()
    {
        byte[] file = Tamper(_ => { }, h => h.SchemaVersion = SimVersion.SaveSchema + 1);
        LoadResult r = SaveSerializer.Deserialize(file, TestData.Data);
        Assert.False(r.Ok);
        Assert.Contains("더 새로운 버전", r.Errors[0]);
        Assert.NotNull(r.Header);
    }

    [Fact]
    public void OlderSchema_WithoutMigration_GivesClearError()
    {
        byte[] file = Tamper(_ => { }, h => h.SchemaVersion = 0);
        Assert.Null(SaveSerializer.ReadHeader(file, out string? error));
        Assert.Contains("schema_version", error);
    }

    [Fact]
    public void DifferentSimulationVersion_LoadsWithWarning()
    {
        byte[] file = Tamper(_ => { }, h => h.SimulationVersion = "0.0.1");
        LoadResult r = SaveSerializer.Deserialize(file, TestData.Data);
        Assert.True(r.Ok);
        Assert.Contains(r.Warnings, w => w.Contains("시뮬레이션 버전"));
    }

    [Fact]
    public void V2Fixture_WithPredatorsAndCarcasses_Loads()
    {
        // 스키마 2(시뮬레이션 0.2.0)에서 만든 샘플: 트리케라톱스 11, 티라노사우루스 2, 사체 3.
        string path = Path.Combine(TestData.FixturesDir, "schema2_predators.psave");
        LoadResult r = SaveSerializer.Deserialize(File.ReadAllBytes(path), TestData.Data);
        Assert.True(r.Ok, string.Join(" / ", r.Errors));
        Assert.Equal(2, r.Header!.SchemaVersion);
        SimulationCore sim = r.Sim!;
        Assert.Equal(13, sim.CreatureCount);
        Assert.Equal(3, sim.World.Carcasses.Count);
        Assert.Equal(3, sim.Stats.TotalDeathsByCause(DeathCause.Predation));
        Assert.True(sim.World.Vegetation.SumNutrient() > 0);
        // 스키마 2에는 개체 수명이 없다. 읽을 때 종의 최대 수명을 받는다.
        Assert.Contains(r.Warnings, w => w.Contains("2 → 3"));
        Assert.All(sim.Creatures, c => Assert.Equal(sim.World.Species[c.SpeciesIndex].MaxAgeDays, c.LifespanDays));
        TestData.Run(sim, 300);
    }

    [Fact]
    public void V1Fixture_IsMigrated_ToCurrentSchema()
    {
        string path = Path.Combine(TestData.FixturesDir, "schema1_small.psave");
        LoadResult r = SaveSerializer.Deserialize(File.ReadAllBytes(path), TestData.Data);
        Assert.True(r.Ok, string.Join(" / ", r.Errors));
        Assert.Contains(r.Warnings, w => w.Contains("1 → 2"));
        Assert.Empty(r.Sim!.World.Carcasses);
        // 스키마 1의 행동 점수 6개는 7개(사냥 0)로 채워진다.
        Assert.All(r.Sim.Creatures, c => Assert.Equal(0f, c.LastScores[(int)BehaviorKind.Hunt]));
    }

    [Fact]
    public void V1Fixture_StillLoads()
    {
        // 스키마 1 시절에 만든 세이브 샘플. 형식을 바꾼 뒤에도 (마이그레이션을 거쳐) 읽혀야 한다.
        string path = Path.Combine(TestData.FixturesDir, "schema1_small.psave");
        Assert.True(File.Exists(path), "세이브 샘플이 없습니다: " + path);
        LoadResult r = SaveSerializer.Deserialize(File.ReadAllBytes(path), TestData.Data);
        Assert.True(r.Ok, string.Join(" / ", r.Errors));
        Assert.Equal(1, r.Header!.SchemaVersion);
        Assert.Equal(12, r.Sim!.CreatureCount);
        Assert.Equal(1500, r.Sim.Clock.Tick);
        TestData.Run(r.Sim, 200);
    }
}

/// <summary>마이그레이션 사슬 시험. 전역 등록부를 건드리므로 다른 세이브 테스트와 병렬로 돌리지 않는다.</summary>
[Collection("SaveMigrations")]
public class SaveMigrationTests
{
    [Fact]
    public void MigrationChain_UpgradesOlderState()
    {
        // 가상의 '스키마 0': tick을 tick_count라는 이름으로 저장했다고 가정한다.
        SimulationCore sim = TestData.NewSim(seed: 31, creatures: 6);
        TestData.Run(sim, 400);
        byte[] file = SaveSerializer.Serialize(sim, "old");
        byte[] state = SaveSerializer.ExtractState(file, out SaveHeader? header, out _)!;
        var node = (JsonObject)JsonNode.Parse(state)!;
        JsonNode tick = node["tick"]!.DeepClone();
        node.Remove("tick");
        node["tick_count"] = tick;
        header!.SchemaVersion = 0;

        // ReadHeader는 0을 거부하므로 사슬 자체를 직접 시험한다.
        Assert.False(SaveMigrations.CanMigrate(0));
        SaveMigrations.Register(0, old =>
        {
            JsonNode value = old["tick_count"]!.DeepClone();
            old.Remove("tick_count");
            old["tick"] = value;
            return old;
        });
        try
        {
            Assert.True(SaveMigrations.CanMigrate(0));
            var log = new System.Collections.Generic.List<string>();
            JsonObject upgraded = SaveMigrations.Upgrade(node, 0, log);
            // 0→1(이 테스트가 등록한 것)과 내장된 1→2, 2→3이 차례로 적용된다.
            Assert.Equal(SimVersion.SaveSchema, log.Count);
            header.SchemaVersion = SimVersion.SaveSchema;
            byte[] rebuilt = SaveSerializer.BuildFile(Encoding.UTF8.GetBytes(upgraded.ToJsonString()), header);
            LoadResult r = SaveSerializer.Deserialize(rebuilt, TestData.Data);
            Assert.True(r.Ok, string.Join(" / ", r.Errors));
            Assert.Equal(sim.ComputeStateHash(), r.Sim!.ComputeStateHash());
        }
        finally
        {
            SaveMigrations.Unregister(0);
        }
        Assert.False(SaveMigrations.CanMigrate(0));
    }
}
