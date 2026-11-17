using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;
using PaleoSphere.App;
using PaleoSphere.Core;
using PaleoSphere.Creatures;
using PaleoSphere.Modding;
using PaleoSphere.Persistence;
using PaleoSphere.Simulation;
using PaleoSphere.World;

namespace PaleoSphere.UI;

/// <summary>세계 생성 마법사: 시드, 크기, 기후, 초기 식생, 초기 개체 수.</summary>
public partial class NewWorldDialog : ConfirmationDialog
{
    private readonly GameData _data;
    private OptionButton _preset = null!;
    private LineEdit _seed = null!;
    private OptionButton _size = null!;
    private SpinBox _relief = null!;
    private SpinBox _water = null!;
    private CheckBox _river = null!;
    private SpinBox _vegetation = null!;
    private OptionButton _climate = null!;
    private Label _error = null!;
    private readonly List<SpinBox> _counts = new();
    private WorldConfig _base = new();
    private static readonly int[] SizeCells = { 128, 256, 512 };

    public event Action<WorldConfig, List<ScenarioPopulation>, string>? Create;

    public NewWorldDialog(GameData data)
    {
        _data = data;
        Title = Loc.T("new.title");
        Exclusive = true;
        Unresizable = false;
        MinSize = new Vector2I(460, 480);
    }

    public override void _Ready()
    {
        GetOkButton().Text = Loc.T("new.create");
        GetCancelButton().Text = Loc.T("menu.close");
        DialogHideOnOk = false;
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 8);
        AddChild(grid);

        void Row(string key, Control control)
        {
            grid.AddChild(new Label { Text = Loc.T(key) });
            control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            control.CustomMinimumSize = new Vector2(240, 0);
            grid.AddChild(control);
        }

        _preset = new OptionButton();
        foreach (ScenarioDefinition s in _data.Scenarios) _preset.AddItem(Loc.Name(s.NameKo, s.NameEn));
        _preset.ItemSelected += index => ApplyPreset((int)index);
        Row("new.preset", _preset);

        var seedRow = new HBoxContainer();
        _seed = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MaxLength = 20 };
        var random = new Button { Text = Loc.T("new.random") };
        random.Pressed += () => _seed.Text = ((ulong)Time.GetTicksUsec() * 2654435761UL % 1000000000UL).ToString(CultureInfo.InvariantCulture);
        seedRow.AddChild(_seed);
        seedRow.AddChild(random);
        Row("new.seed", seedRow);

        _size = new OptionButton();
        foreach (int cells in SizeCells) _size.AddItem($"{cells * 2} m × {cells * 2} m");
        Row("new.size", _size);

        _relief = new SpinBox { MinValue = 0, MaxValue = 60, Step = 1 };
        Row("new.relief", _relief);
        _water = new SpinBox { MinValue = 0, MaxValue = 40, Step = 1 };
        Row("new.water", _water);
        _river = new CheckBox();
        Row("new.river", _river);
        _vegetation = new SpinBox { MinValue = 0, MaxValue = 100, Step = 5 };
        Row("new.vegetation", _vegetation);
        _climate = new OptionButton();
        _climate.AddItem(Loc.T("new.climate.dry"));
        _climate.AddItem(Loc.T("new.climate.normal"));
        _climate.AddItem(Loc.T("new.climate.wet"));
        Row("new.climate", _climate);

        foreach (SpeciesDefinition sp in _data.Species.All)
        {
            var count = new SpinBox { MinValue = 0, MaxValue = SimulationCore.MaxSpawnPerCommand, Step = 1 };
            _counts.Add(count);
            grid.AddChild(new Label { Text = $"{Loc.Name(sp.NameKo, sp.NameEn)} · {Loc.T("new.count")}" });
            grid.AddChild(count);
        }

        grid.AddChild(new Control());
        _error = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(240, 0) };
        _error.AddThemeColorOverride("font_color", UiTheme.Bad);
        grid.AddChild(_error);

        Confirmed += OnConfirmed;
        if (_data.Scenarios.Count > 0) ApplyPreset(0);
    }

    private void ApplyPreset(int index)
    {
        if (index < 0 || index >= _data.Scenarios.Count) return;
        ScenarioDefinition s = _data.Scenarios[index];
        _base = s.World.Clone();
        _seed.Text = _base.Seed.ToString(CultureInfo.InvariantCulture);
        int sizeIndex = Array.IndexOf(SizeCells, _base.SizeCells);
        _size.Selected = sizeIndex >= 0 ? sizeIndex : 1;
        _relief.Value = _base.Relief;
        _water.Value = Math.Round(_base.WaterFraction * 100);
        _river.ButtonPressed = _base.River;
        _vegetation.Value = Math.Round(_base.InitialVegetation * 100);
        _climate.Selected = _base.MoistureBias < -0.05f ? 0 : (_base.MoistureBias > 0.05f ? 2 : 1);
        for (int i = 0; i < _counts.Count; i++)
        {
            int count = 0;
            foreach (ScenarioPopulation p in s.Populations)
                if (p.SpeciesId == _data.Species[i].Id) count = p.Count;
            _counts[i].Value = count;
        }
        _error.Text = "";
    }

    private void OnConfirmed()
    {
        if (!ulong.TryParse(_seed.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed))
        {
            _error.Text = Loc.T("new.badseed");
            return;
        }
        WorldConfig cfg = _base.Clone();
        cfg.Seed = seed;
        cfg.SizeCells = SizeCells[Math.Clamp(_size.Selected, 0, SizeCells.Length - 1)];
        cfg.CellSize = 2f;
        cfg.Relief = (float)_relief.Value;
        cfg.WaterFraction = (float)_water.Value / 100f;
        cfg.River = _river.ButtonPressed;
        cfg.InitialVegetation = (float)_vegetation.Value / 100f;
        // 기후를 바꾸지 않았으면 시나리오의 습윤도를 그대로 둔다.
        int presetClimate = _base.MoistureBias < -0.05f ? 0 : (_base.MoistureBias > 0.05f ? 2 : 1);
        if (_climate.Selected != presetClimate) cfg.MoistureBias = _climate.Selected == 0 ? -0.2f : (_climate.Selected == 2 ? 0.2f : 0f);

        List<string> errors = cfg.Validate();
        if (errors.Count > 0)
        {
            _error.Text = string.Join("\n", errors);
            return;
        }
        var pops = new List<ScenarioPopulation>();
        for (int i = 0; i < _counts.Count; i++)
            if (_counts[i].Value >= 1) pops.Add(new ScenarioPopulation(_data.Species[i].Id, (int)_counts[i].Value));
        string name = _preset.Selected >= 0 && _preset.Selected < _data.Scenarios.Count
            ? Loc.Name(_data.Scenarios[_preset.Selected].NameKo, _data.Scenarios[_preset.Selected].NameEn)
            : Loc.T("world.default");
        Hide();
        Create?.Invoke(cfg, pops, name);
    }
}

/// <summary>저장·불러오기 창. 저장 목록에 머리말 정보(날짜, 개체 수)를 보여 주고, 깨진 파일은 이유를 적는다.</summary>
public partial class SaveLoadDialog : AcceptDialog
{
    private readonly SaveFileStore _store;
    private readonly bool _saving;
    private LineEdit _name = null!;
    private ItemList _list = null!;
    private Label _empty = null!;
    private List<SaveSlotInfo> _slots = new();

    public event Action<string>? SlotChosen;

    public SaveLoadDialog(SaveFileStore store, bool saving)
    {
        _store = store;
        _saving = saving;
        Title = Loc.T(saving ? "save.title" : "load.title");
        Exclusive = true;
        MinSize = new Vector2I(560, 420);
    }

    public override void _Ready()
    {
        GetOkButton().Text = Loc.T(_saving ? "menu.save" : "menu.load");
        DialogHideOnOk = false;
        AddCancelButton(Loc.T("menu.close"));
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        AddChild(box);

        if (_saving)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = Loc.T("save.name") });
            _name = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MaxLength = 40, Text = "world" };
            row.AddChild(_name);
            box.AddChild(row);
        }
        _list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(520, 260) };
        _list.ItemSelected += index =>
        {
            if (_saving && index >= 0 && index < _slots.Count) _name.Text = _slots[(int)index].Slot;
        };
        _list.ItemActivated += _ => Choose();
        box.AddChild(_list);
        _empty = UiTheme.MutedLabel(Loc.T("load.empty"));
        box.AddChild(_empty);

        var delete = new Button { Text = Loc.T("load.delete"), SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd };
        delete.Pressed += () =>
        {
            int[] selected = _list.GetSelectedItems();
            if (selected.Length == 0 || selected[0] >= _slots.Count) return;
            _store.Delete(_slots[selected[0]].Slot);
            Refresh();
        };
        box.AddChild(delete);
        Confirmed += Choose;
    }

    public void Open(string suggestedName)
    {
        Refresh();
        if (_saving && suggestedName.Length > 0) _name.Text = SaveFileStore.SanitizeSlot(suggestedName);
        PopupCentered();
    }

    private void Refresh()
    {
        _slots = _store.List();
        _list.Clear();
        foreach (SaveSlotInfo s in _slots)
        {
            string when = s.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            _list.AddItem(s.Header != null
                ? Loc.F("load.item", s.Slot, s.Header.Day, s.Header.CreatureCount, when)
                : Loc.F("load.broken", s.Slot, s.Error ?? "?"));
        }
        _empty.Visible = _slots.Count == 0;
    }

    private void Choose()
    {
        string slot;
        if (_saving)
        {
            slot = SaveFileStore.SanitizeSlot(_name.Text);
        }
        else
        {
            int[] selected = _list.GetSelectedItems();
            if (selected.Length == 0 || selected[0] >= _slots.Count) return;
            slot = _slots[selected[0]].Slot;
        }
        Hide();
        SlotChosen?.Invoke(slot);
    }
}

public partial class SettingsDialog : AcceptDialog
{
    private readonly AppSettings _settings;
    private OptionButton _language = null!;
    private SpinBox _scale = null!;
    private SpinBox _autosave = null!;
    private SpinBox _budget = null!;

    public event Action? Applied;

    public SettingsDialog(AppSettings settings)
    {
        _settings = settings;
        Title = Loc.T("settings.title");
        Exclusive = true;
        MinSize = new Vector2I(460, 260);
    }

    public override void _Ready()
    {
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 8);
        AddChild(grid);
        _language = new OptionButton();
        _language.AddItem("한국어");
        _language.AddItem("English");
        _language.Selected = _settings.Language == "en" ? 1 : 0;
        grid.AddChild(new Label { Text = Loc.T("settings.language") });
        grid.AddChild(_language);
        _scale = new SpinBox { MinValue = 0.75, MaxValue = 2.0, Step = 0.05, Value = _settings.UiScale };
        grid.AddChild(new Label { Text = Loc.T("settings.scale") });
        grid.AddChild(_scale);
        _autosave = new SpinBox { MinValue = 0, MaxValue = 120, Step = 1, Value = _settings.AutosaveMinutes };
        grid.AddChild(new Label { Text = Loc.T("settings.autosave") });
        grid.AddChild(_autosave);
        _budget = new SpinBox { MinValue = 2, MaxValue = 30, Step = 1, Value = _settings.SimBudgetMs };
        grid.AddChild(new Label { Text = Loc.T("settings.budget") });
        grid.AddChild(_budget);
        grid.AddChild(new Control());
        Label note = UiTheme.MutedLabel(Loc.T("settings.note"));
        note.CustomMinimumSize = new Vector2(240, 0);
        grid.AddChild(note);
        Confirmed += () =>
        {
            _settings.Language = _language.Selected == 1 ? "en" : "ko";
            _settings.UiScale = (float)_scale.Value;
            _settings.AutosaveMinutes = (int)_autosave.Value;
            _settings.SimBudgetMs = (float)_budget.Value;
            _settings.Save();
            Applied?.Invoke();
        };
    }
}

/// <summary>종 도감. 항목마다 근거 수준(확인·추정·단순화)을 색 딱지로 붙인다.</summary>
public partial class CodexDialog : AcceptDialog
{
    private RichTextLabel _text = null!;

    public CodexDialog()
    {
        Title = Loc.T("codex.title");
        MinSize = new Vector2I(640, 560);
    }

    public override void _Ready()
    {
        GetOkButton().Text = Loc.T("menu.close");
        _text = new RichTextLabel { BbcodeEnabled = true, CustomMinimumSize = new Vector2(600, 460), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        AddChild(_text);
    }

    private static string Badge(EvidenceLevel e) =>
        $"[color=#{UiTheme.EvidenceColor(e).ToHtml(false)}]■ {Loc.Evidence(e)}[/color]";

    public void ShowSpecies(SpeciesDefinition sp, BiomeRegistry biomes)
    {
        var sb = new StringBuilder();
        sb.Append("[font_size=22][b]").Append(Loc.Name(sp.NameKo, sp.NameEn)).Append("[/b][/font_size]  [i]").Append(sp.ScientificName).Append("[/i]\n\n");
        sb.Append("[b]").Append(Loc.T("codex.legend")).Append("[/b]  ")
            .Append(Badge(EvidenceLevel.Confirmed)).Append("   ").Append(Badge(EvidenceLevel.Inferred)).Append("   ").Append(Badge(EvidenceLevel.Simplified)).Append("\n\n");
        sb.Append("[b]").Append(Loc.T("codex.period")).Append("[/b]  ").Append(sp.Period).Append("  ").Append(Badge(sp.PeriodEvidence)).Append('\n');
        sb.Append("[b]").Append(Loc.T("codex.range")).Append("[/b]  ").Append(sp.Range).Append("  ").Append(Badge(sp.RangeEvidence)).Append('\n');
        sb.Append("[b]").Append(Loc.T("codex.body")).Append("[/b]  ").Append(Loc.F("codex.bodyline", sp.BodyLengthM, sp.BodyMassKg / 1000f)).Append("  ").Append(Badge(sp.BodyEvidence)).Append('\n');
        sb.Append("[b]").Append(Loc.T("codex.diet")).Append("[/b]  ").Append(Loc.T("diet." + sp.Diet.ToString().ToLowerInvariant())).Append('\n');
        sb.Append("[b]").Append(Loc.T("codex.sim")).Append("[/b]  ")
            .Append(Loc.F("codex.simline", sp.WalkSpeedMps, sp.RunSpeedMps, sp.DailyFoodKg, sp.HydrationReserveDays + sp.DehydrationHealthDays, sp.EnergyReserveDays + sp.StarveHealthDays))
            .Append("  ").Append(Badge(EvidenceLevel.Simplified)).Append('\n');

        var places = new List<string>();
        foreach (string id in sp.PlacementBiomes)
            if (biomes.TryGetIndex(id, out int bi)) places.Add(Loc.Name(biomes[bi].NameKo, biomes[bi].NameEn));
        if (places.Count > 0) sb.Append("[b]").Append(Loc.T("codex.placement")).Append("[/b]  ").Append(string.Join(", ", places)).Append('\n');
        if (sp.IsPredator || sp.Defense > 0f)
        {
            sb.Append("[b]").Append(Loc.T("codex.predation")).Append("[/b]  ")
                .Append(sp.IsPredator
                    ? Loc.F("codex.predline.hunter", sp.AttackSuccess * 100f, sp.Defense, sp.MaxChaseSeconds, sp.SmellRadiusM)
                    : Loc.F("codex.predline.prey", sp.Defense))
                .Append("  ").Append(Badge(EvidenceLevel.Simplified)).Append('\n');
        }

        sb.Append("\n[b]").Append(Loc.T("codex.facts")).Append("[/b]\n");
        foreach (FactEntry f in sp.Facts)
            sb.Append(Badge(f.Evidence)).Append("  [b]").Append(f.Topic).Append("[/b] — ").Append(f.Text).Append('\n');
        sb.Append("\n[b]").Append(Loc.T("codex.sources")).Append("[/b]\n");
        foreach (SourceEntry s in sp.Sources)
            sb.Append("• ").Append(s.Title).Append(s.Note.Length > 0 ? " — " + s.Note : "").Append('\n');
        _text.Text = sb.ToString();
        PopupCentered();
    }
}
