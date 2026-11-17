using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using PaleoSphere.App;
using PaleoSphere.Core;
using PaleoSphere.Creatures;
using PaleoSphere.Diagnostics;
using PaleoSphere.Ecology;
using PaleoSphere.Simulation;
using PaleoSphere.World;

namespace PaleoSphere.UI;

public enum ToolMode
{
    Select = 0,
    Place = 1,
    Disturb = 2,
}

/// <summary>
/// 메인 화면 UI. 위쪽 메뉴, 왼쪽 세계 탐색기, 오른쪽 검사창, 시간 제어 바, 아래쪽 도킹 패널로 이뤄진다.
/// 세계를 바꾸는 일은 직접 하지 않고 이벤트로 알리며, Main이 명령으로 바꿔 코어에 넘긴다.
/// 가운데 영역은 마우스를 통과시켜 3D 화면 클릭이 가능하다.
/// </summary>
public partial class Hud : Control
{
    private readonly GameSession _session;
    private readonly AppSettings _settings;

    private Label _status = null!;
    private Label _worldInfo = null!;
    private Label _envInfo = null!;
    private VBoxContainer _biomeBox = null!;
    private VBoxContainer _speciesBox = null!;
    private RichTextLabel _recentEvents = null!;
    private readonly List<Label> _speciesCounts = new();
    private readonly List<MiniBars> _ageBars = new();

    private Button _play = null!;
    private readonly List<Button> _speedButtons = new();
    private static readonly float[] Speeds = { 1f, 2f, 5f, 10f };
    private Label _clock = null!;
    private Label _effective = null!;
    private OptionButton _placeSpecies = null!;
    private SpinBox _placeCount = null!;
    private Button _placeButton = null!;
    private Button _disturbButton = null!;
    private Label _toolHint = null!;

    private PanelContainer _dock = null!;
    private TabContainer _tabs = null!;
    private GraphControl _populationGraph = null!;
    private GraphControl _vegetationGraph = null!;
    private GraphControl _conditionGraph = null!;
    private RichTextLabel _deaths = null!;
    private RichTextLabel _behaviorLog = null!;
    private RichTextLabel _eventLog = null!;
    private Label _performance = null!;

    private PanelContainer _left = null!;
    private PanelContainer _startPanel = null!;
    private Label _toast = null!;
    private double _toastTime;
    private double _slowTimer;
    private long _eventsShown = -1;
    private double _frameMs;

    public InspectorPanel Inspector { get; private set; } = null!;
    public ToolMode Tool { get; private set; } = ToolMode.Select;
    public string PlaceSpeciesId => _session.Data.Species.Count > 0 ? _session.Data.Species[Math.Max(0, _placeSpecies.Selected)].Id : "";
    public int PlaceCount => (int)_placeCount.Value;
    public float PlaceRadius => Mathf.Clamp(6f + Mathf.Sqrt(PlaceCount) * 5f, 8f, 120f);
    public const float DisturbRadius = 45f;

    public event Action? NewWorldPressed;
    public event Action? LoadPressed;
    public event Action? SavePressed;
    public event Action? SettingsPressed;
    public event Action? ContinuePressed;
    public event Action<int>? CodexRequested;
    public event Action<int>? LoadTestRequested;

    public Hud(GameSession session, AppSettings settings)
    {
        _session = session;
        _settings = settings;
    }

    public override void _Ready()
    {
        // 앵커만 바꾸면 현재 크기가 유지되므로 오프셋까지 0으로 맞춰 화면을 꽉 채운다.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        var root = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        root.AddChild(BuildTopBar());

        var middle = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ExpandFill };
        middle.AddThemeConstantOverride("separation", 0);
        root.AddChild(middle);
        _left = BuildExplorer();
        middle.AddChild(_left);
        middle.AddChild(new Control { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        Inspector = new InspectorPanel { CustomMinimumSize = new Vector2(_settings.RightPanelWidth, 0) };
        Inspector.CodexPressed += index => CodexRequested?.Invoke(index);
        middle.AddChild(Inspector);

        root.AddChild(BuildTimeBar());
        _dock = BuildDock();
        _dock.Visible = _settings.ShowDock;
        root.AddChild(_dock);

        _toast = new Label { HorizontalAlignment = HorizontalAlignment.Center, Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _toast.AddThemeStyleboxOverride("normal", UiTheme.Box(new Color(0.05f, 0.06f, 0.07f, 0.92f), 10));
        AddChild(_toast);

        _startPanel = BuildStartPanel();
        AddChild(_startPanel);
    }

    // ------------------------------------------------------------ 구성

    private Control BuildTopBar()
    {
        var panel = new PanelContainer();
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);
        var title = new Label { Text = "PALEOSPHERE" };
        title.AddThemeColorOverride("font_color", UiTheme.Accent);
        title.AddThemeFontSizeOverride("font_size", 17);
        row.AddChild(title);
        row.AddChild(new VSeparator());

        Button Add(string key, Action? handler)
        {
            var b = new Button { Text = Loc.T(key), FocusMode = FocusModeEnum.None };
            b.Pressed += () => handler?.Invoke();
            row.AddChild(b);
            return b;
        }
        Add("menu.new", () => NewWorldPressed?.Invoke());
        Add("menu.load", () => LoadPressed?.Invoke());
        Add("menu.save", () => SavePressed?.Invoke());
        Add("menu.settings", () => SettingsPressed?.Invoke());

        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _status = new Label();
        _status.AddThemeColorOverride("font_color", UiTheme.Muted);
        row.AddChild(_status);
        return panel;
    }

    private PanelContainer BuildExplorer()
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(_settings.LeftPanelWidth, 0) };
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        panel.AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(box);

        box.AddChild(UiTheme.Heading(Loc.T("explorer.title")));
        _worldInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_worldInfo);
        _envInfo = UiTheme.MutedLabel();
        box.AddChild(_envInfo);

        box.AddChild(new HSeparator());
        box.AddChild(UiTheme.Heading(Loc.T("explorer.biomes")));
        _biomeBox = new VBoxContainer();
        box.AddChild(_biomeBox);

        box.AddChild(new HSeparator());
        box.AddChild(UiTheme.Heading(Loc.T("explorer.creatures")));
        _speciesBox = new VBoxContainer();
        box.AddChild(_speciesBox);

        box.AddChild(new HSeparator());
        box.AddChild(UiTheme.Heading(Loc.T("explorer.events")));
        _recentEvents = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false };
        _recentEvents.AddThemeFontSizeOverride("normal_font_size", 13);
        box.AddChild(_recentEvents);
        return panel;
    }

    private Control BuildTimeBar()
    {
        var panel = new PanelContainer();
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        panel.AddChild(row);

        _play = new Button { Text = "Ⅱ", CustomMinimumSize = new Vector2(40, 0), FocusMode = FocusModeEnum.None };
        _play.Pressed += TogglePause;
        row.AddChild(_play);
        for (int i = 0; i < Speeds.Length; i++)
        {
            float speed = Speeds[i];
            var b = new Button { Text = $"{speed:0}x", ToggleMode = true, FocusMode = FocusModeEnum.None };
            b.Pressed += () => SetSpeed(speed);
            _speedButtons.Add(b);
            row.AddChild(b);
        }
        row.AddChild(new VSeparator());
        _clock = new Label { CustomMinimumSize = new Vector2(210, 0) };
        row.AddChild(_clock);
        _effective = new Label();
        _effective.AddThemeColorOverride("font_color", UiTheme.Muted);
        row.AddChild(_effective);

        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _toolHint = new Label();
        _toolHint.AddThemeColorOverride("font_color", UiTheme.Warn);
        row.AddChild(_toolHint);

        _placeSpecies = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (SpeciesDefinition sp in _session.Data.Species.All) _placeSpecies.AddItem(Loc.Name(sp.NameKo, sp.NameEn));
        row.AddChild(_placeSpecies);
        _placeCount = new SpinBox { MinValue = 1, MaxValue = 200, Value = 10, Step = 1, Suffix = Loc.T("tool.count") };
        row.AddChild(_placeCount);
        _placeButton = new Button { Text = Loc.T("tool.place"), ToggleMode = true, FocusMode = FocusModeEnum.None };
        _placeButton.Pressed += () => SetTool(_placeButton.ButtonPressed ? ToolMode.Place : ToolMode.Select);
        row.AddChild(_placeButton);
        _disturbButton = new Button { Text = Loc.T("tool.disturb"), ToggleMode = true, FocusMode = FocusModeEnum.None };
        _disturbButton.Pressed += () => SetTool(_disturbButton.ButtonPressed ? ToolMode.Disturb : ToolMode.Select);
        row.AddChild(_disturbButton);
        var terrain = new Button { Text = Loc.T("tool.terrain"), Disabled = true, TooltipText = Loc.T("tool.terrain.tip"), FocusMode = FocusModeEnum.None };
        row.AddChild(terrain);
        return panel;
    }

    private PanelContainer BuildDock()
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(0, 210) };
        _tabs = new TabContainer();
        panel.AddChild(_tabs);

        _populationGraph = new GraphControl { XLabel = Loc.T("graph.days"), EmptyText = Loc.T("graph.empty") };
        _tabs.AddChild(_populationGraph);

        var resources = new HBoxContainer();
        _vegetationGraph = new GraphControl { XLabel = Loc.T("graph.days"), EmptyText = Loc.T("graph.empty") };
        _conditionGraph = new GraphControl { XLabel = Loc.T("graph.days"), EmptyText = Loc.T("graph.empty"), FixedMax = 1f };
        resources.AddChild(_vegetationGraph);
        resources.AddChild(_conditionGraph);
        _tabs.AddChild(resources);

        _deaths = new RichTextLabel { BbcodeEnabled = true };
        _tabs.AddChild(_deaths);
        _behaviorLog = new RichTextLabel { BbcodeEnabled = true };
        _tabs.AddChild(_behaviorLog);
        _eventLog = new RichTextLabel { BbcodeEnabled = true };
        _tabs.AddChild(_eventLog);

        var perf = new VBoxContainer();
        _performance = new Label();
        perf.AddChild(_performance);
        var perfRow = new HBoxContainer();
        foreach (int n in new[] { 100, 500 })
        {
            int count = n;
            var b = new Button { Text = Loc.F("perf.add", count), FocusMode = FocusModeEnum.None };
            b.Pressed += () => LoadTestRequested?.Invoke(count);
            perfRow.AddChild(b);
        }
        perf.AddChild(perfRow);
        perf.AddChild(UiTheme.MutedLabel(Loc.T("perf.note")));
        _tabs.AddChild(perf);

        string[] titles = { "dock.population", "dock.resources", "dock.deaths", "dock.behavior", "dock.events", "dock.performance" };
        for (int i = 0; i < titles.Length; i++) _tabs.SetTabTitle(i, Loc.T(titles[i]));
        _tabs.CurrentTab = Math.Clamp(_settings.DockTab, 0, titles.Length - 1);
        _tabs.TabChanged += tab => _settings.DockTab = (int)tab;
        return panel;
    }

    private PanelContainer BuildStartPanel()
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(440, 0) };
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0.07f, 0.09f, 0.10f, 0.97f), 22, 8));
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        panel.AddChild(box);
        var title = new Label { Text = "PALEOSPHERE", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 34);
        title.AddThemeColorOverride("font_color", UiTheme.Accent);
        box.AddChild(title);
        box.AddChild(new Label { Text = Loc.T("start.subtitle"), HorizontalAlignment = HorizontalAlignment.Center });
        Label note = UiTheme.MutedLabel(Loc.T("start.note"));
        note.CustomMinimumSize = new Vector2(400, 0);
        box.AddChild(note);

        void Add(string key, Action handler)
        {
            var b = new Button { Text = Loc.T(key), CustomMinimumSize = new Vector2(0, 36) };
            b.Pressed += handler;
            box.AddChild(b);
        }
        Add("start.begin", HideStartPanel);
        Add("menu.new", () => { HideStartPanel(); NewWorldPressed?.Invoke(); });
        Add("menu.continue", () => { HideStartPanel(); ContinuePressed?.Invoke(); });
        Add("menu.load", () => { HideStartPanel(); LoadPressed?.Invoke(); });
        Add("menu.settings", () => SettingsPressed?.Invoke());
        return panel;
    }

    public void HideStartPanel() => _startPanel.Visible = false;
    public bool StartPanelVisible => _startPanel.Visible;

    // ------------------------------------------------------------ 조작

    public void TogglePause()
    {
        _session.Driver.Paused = !_session.Driver.Paused;
    }

    public void SetSpeed(float speed)
    {
        _session.Driver.TimeScale = speed;
        _session.Driver.Paused = false;
    }

    public void SetTool(ToolMode tool)
    {
        Tool = tool;
        _placeButton.SetPressedNoSignal(tool == ToolMode.Place);
        _disturbButton.SetPressedNoSignal(tool == ToolMode.Disturb);
        _toolHint.Text = tool switch
        {
            ToolMode.Place => Loc.T("tool.place.hint"),
            ToolMode.Disturb => Loc.T("tool.disturb.hint"),
            _ => "",
        };
    }

    public void ToggleDock()
    {
        _dock.Visible = !_dock.Visible;
        _settings.ShowDock = _dock.Visible;
    }

    public void SelectDockTab(int tab) => _tabs.CurrentTab = Math.Clamp(tab, 0, _tabs.GetTabCount() - 1);

    public void Toast(string text, double seconds = 3.5)
    {
        _toast.Text = text;
        _toast.Visible = true;
        _toast.ResetSize();
        _toast.Position = new Vector2((Size.X - _toast.Size.X) * 0.5f, 54);
        _toastTime = seconds;
    }

    /// <summary>세계가 바뀌었을 때 탐색기의 고정 정보를 다시 만든다.</summary>
    public void OnWorldReplaced()
    {
        SimulationCore? sim = _session.Sim;
        if (sim == null) return;
        TerrainGrid t = sim.World.Terrain;

        foreach (Node child in _biomeBox.GetChildren()) child.QueueFree();
        var counts = new int[sim.World.Biomes.Count];
        for (int i = 0; i < t.CellCount; i++) counts[t.Biome[i]]++;
        for (int b = 0; b < counts.Length; b++)
        {
            if (counts[b] == 0) continue;
            BiomeDefinition def = sim.World.Biomes[b];
            var row = new HBoxContainer { TooltipText = def.Description + $"\n[{Loc.Evidence(def.Evidence)}]" };
            row.AddChild(new ColorRect { Color = new Color(def.ColorR, def.ColorG, def.ColorB), CustomMinimumSize = new Vector2(14, 14), SizeFlagsVertical = SizeFlags.ShrinkCenter });
            row.AddChild(new Label { Text = Loc.Name(def.NameKo, def.NameEn), SizeFlagsHorizontal = SizeFlags.ExpandFill });
            row.AddChild(UiTheme.SmallLabel($"{100.0 * counts[b] / t.CellCount:0.0}%"));
            _biomeBox.AddChild(row);
        }

        foreach (Node child in _speciesBox.GetChildren()) child.QueueFree();
        _speciesCounts.Clear();
        _ageBars.Clear();
        for (int s = 0; s < sim.World.Species.Count; s++)
        {
            SpeciesDefinition sp = sim.World.Species[s];
            int index = s;
            var row = new HBoxContainer();
            row.AddChild(new ColorRect { Color = new Color(sp.ProxyColorR, sp.ProxyColorG, sp.ProxyColorB), CustomMinimumSize = new Vector2(14, 14), SizeFlagsVertical = SizeFlags.ShrinkCenter });
            row.AddChild(new Label { Text = Loc.Name(sp.NameKo, sp.NameEn), SizeFlagsHorizontal = SizeFlags.ExpandFill });
            var count = new Label();
            _speciesCounts.Add(count);
            row.AddChild(count);
            var codex = new Button { Text = Loc.T("explorer.codex"), FocusMode = FocusModeEnum.None };
            codex.Pressed += () => CodexRequested?.Invoke(index);
            row.AddChild(codex);
            _speciesBox.AddChild(row);
            _speciesBox.AddChild(UiTheme.MutedLabel(Loc.T("explorer.age")));
            var bars = new MiniBars { BarColor = UiTheme.Series[s % UiTheme.Series.Length] };
            _ageBars.Add(bars);
            _speciesBox.AddChild(bars);
        }
        _eventsShown = -1;
        _slowTimer = 1;
    }

    // ------------------------------------------------------------ 갱신

    public void Refresh(double delta, int selectedId, bool hadSelection, bool following, Vec2? cursorPoint)
    {
        SimulationCore? sim = _session.Sim;
        if (sim == null) return;
        _frameMs += (delta * 1000.0 - _frameMs) * 0.1;

        if (_toastTime > 0)
        {
            _toastTime -= delta;
            if (_toastTime <= 0) _toast.Visible = false;
        }

        // 가벼운 항목은 매 프레임.
        SimClock clock = sim.Clock;
        float hour = clock.TimeOfDayHours;
        _clock.Text = Loc.F("time.day", clock.Day, (int)hour, (int)(hour * 60f) % 60, Loc.T("season." + (int)clock.Season));
        FixedTimestepDriver driver = _session.Driver;
        _play.Text = driver.Paused ? "▶" : "Ⅱ";
        _effective.Text = driver.Paused ? Loc.T("time.paused") : Loc.F("time.effective", driver.EffectiveSpeed);
        bool slow = !driver.Paused && driver.EffectiveSpeed > 0 && driver.EffectiveSpeed < driver.TimeScale * 0.9;
        _effective.AddThemeColorOverride("font_color", slow ? UiTheme.Warn : UiTheme.Muted);
        for (int i = 0; i < Speeds.Length; i++)
            _speedButtons[i].SetPressedNoSignal(!driver.Paused && Mathf.IsEqualApprox(driver.TimeScale, Speeds[i]));
        _status.Text = Loc.F("status.line", (int)Engine.GetFramesPerSecond(), sim.Profiler.AverageTickMs, sim.CreatureCount);

        // 나머지는 0.25초마다.
        _slowTimer += delta;
        if (_slowTimer < 0.25) return;
        _slowTimer = 0;

        Inspector.ShowCreature(sim, selectedId >= 0 ? sim.GetDetails(selectedId) : null, hadSelection, following);
        Inspector.ShowTerrain(sim, cursorPoint);

        _worldInfo.Text = $"{_session.WorldName}\n" + Loc.F("explorer.seed", sim.World.Config.Seed, sim.World.Terrain.WorldSize);
        _envInfo.Text = Loc.F("explorer.env", Loc.T("season." + (int)clock.Season), sim.World.Env.TemperatureC, sim.World.Env.Light * 100f);

        var perSpecies = new int[sim.World.Species.Count];
        foreach (Creature c in sim.Creatures) perSpecies[c.SpeciesIndex]++;
        for (int s = 0; s < _speciesCounts.Count && s < perSpecies.Length; s++)
        {
            _speciesCounts[s].Text = perSpecies[s].ToString();
            _ageBars[s].SetValues(PopulationStats.AgeHistogram(sim.Creatures, s, sim.World.Species[s].MaxAgeDays, 10));
        }

        if (sim.Events.TotalPublished != _eventsShown)
        {
            _eventsShown = sim.Events.TotalPublished;
            _recentEvents.Text = FormatEvents(sim, 7);
            _eventLog.Text = FormatEvents(sim, 200);
        }

        if (!_dock.Visible) return;
        switch (_tabs.CurrentTab)
        {
            case 0: UpdatePopulationGraph(sim); break;
            case 1: UpdateResourceGraphs(sim); break;
            case 2: _deaths.Text = FormatDeaths(sim, perSpecies); break;
            case 3:
                CreatureDetails? d = selectedId >= 0 ? sim.GetDetails(selectedId) : null;
                _behaviorLog.Text = d != null ? InspectorPanel.FormatLog(d.Log, clock, Creature.MaxLogEntries) : Loc.T("behaviorlog.none");
                break;
            case 5:
                TickProfiler p = sim.Profiler;
                _performance.Text = Loc.F("perf.text", (int)Engine.GetFramesPerSecond(), _frameMs, p.AverageTickMs, p.MaxTickMs, clock.Tick,
                    p.SectionAverageMs(TickProfiler.Section.Vegetation), p.SectionAverageMs(TickProfiler.Section.Brain), p.SectionAverageMs(TickProfiler.Section.Movement),
                    sim.CreatureCount, driver.TimeScale, driver.EffectiveSpeed, driver.DroppedSeconds, driver.MaxFrameMilliseconds);
                break;
        }
    }

    private void UpdatePopulationGraph(SimulationCore sim)
    {
        List<StatSample> samples = sim.Stats.Samples;
        for (int s = 0; s < sim.World.Species.Count; s++)
        {
            SpeciesDefinition sp = sim.World.Species[s];
            GraphControl.Series series = _populationGraph.GetSeries(s, Loc.Name(sp.NameKo, sp.NameEn), UiTheme.Series[s % UiTheme.Series.Length]);
            series.Points.Clear();
            foreach (StatSample sample in samples) series.Points.Add(new Vector2(sample.Day, sample.Counts[s]));
        }
        _populationGraph.TrimSeries(sim.World.Species.Count);
        _populationGraph.QueueRedraw();
    }

    private void UpdateResourceGraphs(SimulationCore sim)
    {
        List<StatSample> samples = sim.Stats.Samples;
        GraphControl.Series veg = _vegetationGraph.GetSeries(0, Loc.T("graph.vegetation"), UiTheme.Series[0]);
        GraphControl.Series meat = _vegetationGraph.GetSeries(1, Loc.T("graph.meat"), UiTheme.Series[3]);
        meat.Points.Clear();
        GraphControl.Series energy = _conditionGraph.GetSeries(0, Loc.T("graph.energy"), UiTheme.Series[2]);
        GraphControl.Series hydration = _conditionGraph.GetSeries(1, Loc.T("graph.hydration"), UiTheme.Series[1]);
        veg.Points.Clear();
        energy.Points.Clear();
        hydration.Points.Clear();
        foreach (StatSample s in samples)
        {
            veg.Points.Add(new Vector2(s.Day, s.VegetationKg / 1000f));
            meat.Points.Add(new Vector2(s.Day, s.CarcassMeatKg / 1000f));
            energy.Points.Add(new Vector2(s.Day, s.MeanEnergy));
            hydration.Points.Add(new Vector2(s.Day, s.MeanHydration));
        }
        _vegetationGraph.QueueRedraw();
        _conditionGraph.QueueRedraw();
    }

    /// <summary>사냥 사건 문구. Detail은 "사냥감id|사냥감종id".</summary>
    private static string PredationText(SimulationCore sim, string predatorName, in SimEvent e)
    {
        string[] parts = e.Detail.Split('|');
        string preyName = parts.Length > 1 ? parts[1] : "?";
        if (parts.Length > 1 && sim.World.Species.TryGetIndex(parts[1], out int pi))
            preyName = Loc.Name(sim.World.Species[pi].NameKo, sim.World.Species[pi].NameEn);
        return Loc.F("event.predation", predatorName, e.CreatureId, preyName, parts[0]);
    }

    private static string FormatDeaths(SimulationCore sim, int[] alive)
    {
        var sb = new StringBuilder();
        DeathCause[] causes = { DeathCause.Starvation, DeathCause.Dehydration, DeathCause.Predation, DeathCause.OldAge, DeathCause.Removed };
        for (int s = 0; s < sim.World.Species.Count; s++)
        {
            SpeciesDefinition sp = sim.World.Species[s];
            sb.Append("[b]").Append(Loc.Name(sp.NameKo, sp.NameEn)).Append("[/b]  ").Append(Loc.F("deaths.header", sim.Stats.Spawned[s], alive[s])).Append('\n');
            int max = 1;
            foreach (DeathCause c in causes) max = Math.Max(max, sim.Stats.DeathsOf(s, c));
            foreach (DeathCause c in causes)
            {
                int n = sim.Stats.DeathsOf(s, c);
                int bar = (int)Math.Round(24.0 * n / max);
                sb.Append("  ").Append(Loc.Death(BehaviorKinds.ToId(c)).PadRight(14)).Append(' ')
                    .Append("[color=#e06b5c]").Append(new string('█', bar)).Append("[/color] ").Append(n).Append('\n');
            }
        }
        return sb.ToString();
    }

    private static string FormatEvents(SimulationCore sim, int max)
    {
        var sb = new StringBuilder();
        IReadOnlyList<SimEvent> events = sim.Events.Recent;
        int shown = 0;
        for (int i = events.Count - 1; i >= 0 && shown < max; i--)
        {
            SimEvent e = events[i];
            string name = e.SpeciesId;
            if (sim.World.Species.TryGetIndex(e.SpeciesId, out int si))
                name = Loc.Name(sim.World.Species[si].NameKo, sim.World.Species[si].NameEn);
            string text = e.Type switch
            {
                SimEventType.WorldCreated => Loc.T("event.world"),
                SimEventType.CreatureSpawned => Loc.F("event.spawn", name, e.CreatureId),
                SimEventType.CreatureDied => "[color=#e06b5c]" + Loc.F("event.death", name, e.CreatureId, Loc.Death(e.Detail)) + "[/color]",
                SimEventType.CreatureRemoved => Loc.F("event.removed", name, e.CreatureId),
                SimEventType.DayStarted => "[color=#9aa8a6]" + Loc.T("event.day") + "[/color]",
                SimEventType.SeasonChanged => Loc.F("event.season", Loc.T("season." + e.Detail)),
                SimEventType.CommandRejected => "[color=#f2b84d]" + Loc.F("event.rejected", Loc.T("cmd." + e.Detail)) + "[/color]",
                SimEventType.Predation => "[color=#e06b5c]" + PredationText(sim, name, e) + "[/color]",
                _ => e.Detail,
            };
            sb.Append("[color=#9aa8a6]D").Append(e.Day).Append(' ').Append(((int)e.Hour).ToString("00")).Append(':').Append(((int)(e.Hour * 60f) % 60).ToString("00")).Append("[/color] ").Append(text).Append('\n');
            shown++;
        }
        return sb.ToString();
    }
}
