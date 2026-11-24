using System.Text;
using Godot;
using PaleoSphere.Core;
using PaleoSphere.Creatures;
using PaleoSphere.Simulation;
using PaleoSphere.World;

namespace PaleoSphere.UI;

/// <summary>
/// 개체 검사창(UI-02, AI-06). 상태 막대, 지금 하는 행동, 그 행동을 고른 이유와 행동별 점수,
/// 최근 행동 로그를 보여 준다. 선택이 없으면 커서 아래 지형 정보를 보여 준다.
/// </summary>
public partial class InspectorPanel : PanelContainer
{
    private Label _title = null!;
    private Label _subtitle = null!;
    private Label _meta = null!;
    private VBoxContainer _creatureBox = null!;
    private Label _hint = null!;
    private Label _envTitle = null!;
    private Label _env = null!;
    private readonly ProgressBar[] _bars = new ProgressBar[5];
    private readonly Label[] _barValues = new Label[5];
    private readonly ProgressBar[] _scoreBars = new ProgressBar[BehaviorKinds.Count];
    private readonly Label[] _scoreLabels = new Label[BehaviorKinds.Count];
    private Label _behavior = null!;
    private Label _reason = null!;
    private Label _traits = null!;
    private Label _parents = null!;
    private RichTextLabel _log = null!;
    private Button _follow = null!;

    public event System.Action? FollowPressed;
    public event System.Action? RemovePressed;
    public event System.Action<int>? CodexPressed;

    private int _speciesIndex = -1;

    private static readonly BehaviorKind[] ScoreOrder =
    {
        BehaviorKind.Forage, BehaviorKind.Hunt, BehaviorKind.Drink, BehaviorKind.Rest, BehaviorKind.Flee, BehaviorKind.Explore, BehaviorKind.Idle,
    };
    private Label _target = null!;

    public override void _Ready()
    {
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        var root = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(root);

        root.AddChild(UiTheme.Heading(Loc.T("inspector.title")));
        _hint = UiTheme.MutedLabel(Loc.T("inspector.none"));
        root.AddChild(_hint);

        _creatureBox = new VBoxContainer { Visible = false };
        _creatureBox.AddThemeConstantOverride("separation", 5);
        root.AddChild(_creatureBox);

        _title = new Label();
        _title.AddThemeFontSizeOverride("font_size", 18);
        _creatureBox.AddChild(_title);
        _subtitle = UiTheme.MutedLabel();
        _creatureBox.AddChild(_subtitle);
        _meta = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _creatureBox.AddChild(_meta);

        string[] barKeys = { "inspector.health", "inspector.energy", "inspector.hydration", "inspector.fatigue", "inspector.fear" };
        Color[] barColors = { UiTheme.Bad, UiTheme.Accent, new Color(0.40f, 0.70f, 0.95f), UiTheme.Warn, new Color(0.75f, 0.55f, 0.90f) };
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 8);
        _creatureBox.AddChild(grid);
        for (int i = 0; i < 5; i++)
        {
            grid.AddChild(new Label { Text = Loc.T(barKeys[i]), CustomMinimumSize = new Vector2(52, 0) });
            _bars[i] = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0.001, ShowPercentage = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(120, 14), SizeFlagsVertical = SizeFlags.ShrinkCenter };
            _bars[i].AddThemeStyleboxOverride("fill", UiTheme.Box(barColors[i], 0, 3, border: false));
            grid.AddChild(_bars[i]);
            _barValues[i] = new Label { CustomMinimumSize = new Vector2(40, 0), HorizontalAlignment = HorizontalAlignment.Right };
            grid.AddChild(_barValues[i]);
        }

        _creatureBox.AddChild(new HSeparator());
        _creatureBox.AddChild(UiTheme.Heading(Loc.T("inspector.behavior")));
        _behavior = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _creatureBox.AddChild(_behavior);
        _target = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        _target.AddThemeColorOverride("font_color", UiTheme.Bad);
        _creatureBox.AddChild(_target);
        _reason = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _reason.AddThemeColorOverride("font_color", UiTheme.Warn);
        _creatureBox.AddChild(_reason);

        _creatureBox.AddChild(UiTheme.MutedLabel(Loc.T("inspector.scores")));
        var scoreGrid = new GridContainer { Columns = 3 };
        scoreGrid.AddThemeConstantOverride("h_separation", 8);
        _creatureBox.AddChild(scoreGrid);
        for (int i = 0; i < ScoreOrder.Length; i++)
        {
            scoreGrid.AddChild(new Label { Text = Loc.Behavior(ScoreOrder[i]), CustomMinimumSize = new Vector2(52, 0) });
            _scoreBars[i] = new ProgressBar { MinValue = 0, MaxValue = 1.5, Step = 0.001, ShowPercentage = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(120, 10), SizeFlagsVertical = SizeFlags.ShrinkCenter };
            scoreGrid.AddChild(_scoreBars[i]);
            _scoreLabels[i] = new Label { CustomMinimumSize = new Vector2(40, 0), HorizontalAlignment = HorizontalAlignment.Right };
            scoreGrid.AddChild(_scoreLabels[i]);
        }

        _creatureBox.AddChild(new HSeparator());
        _traits = UiTheme.MutedLabel();
        _creatureBox.AddChild(_traits);
        _parents = UiTheme.MutedLabel();
        _creatureBox.AddChild(_parents);

        _creatureBox.AddChild(UiTheme.Heading(Loc.T("inspector.log")));
        _log = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _log.AddThemeFontSizeOverride("normal_font_size", 13);
        _creatureBox.AddChild(_log);

        var buttons = new HBoxContainer();
        _creatureBox.AddChild(buttons);
        _follow = new Button { Text = Loc.T("inspector.follow"), ToggleMode = true };
        _follow.Pressed += () => FollowPressed?.Invoke();
        buttons.AddChild(_follow);
        var codex = new Button { Text = Loc.T("explorer.codex") };
        codex.Pressed += () => { if (_speciesIndex >= 0) CodexPressed?.Invoke(_speciesIndex); };
        buttons.AddChild(codex);
        var remove = new Button { Text = Loc.T("inspector.remove") };
        remove.Pressed += () => RemovePressed?.Invoke();
        buttons.AddChild(remove);

        root.AddChild(new HSeparator());
        _envTitle = UiTheme.Heading(Loc.T("inspector.env"));
        root.AddChild(_envTitle);
        _env = UiTheme.MutedLabel("—");
        root.AddChild(_env);
    }

    public void ShowTerrain(SimulationCore sim, Vec2? point)
    {
        if (point == null || !sim.World.Terrain.InBounds(point.Value))
        {
            _env.Text = "—";
            return;
        }
        TerrainGrid t = sim.World.Terrain;
        int cell = t.CellIndexAt(point.Value);
        BiomeDefinition b = sim.World.Biomes[t.Biome[cell]];
        _env.Text = Loc.F("inspector.envline", Loc.Name(b.NameKo, b.NameEn), t.Height[cell] - t.WaterLevel, t.WaterDepth(cell),
            t.Moisture[cell] * 100f, t.Fertility[cell] * 100f, sim.World.Vegetation.Biomass[cell], sim.World.Vegetation.Capacity[cell]);
    }

    public static string ReasonText(DecisionReason reason, BehaviorKind behavior, float score, float energy, float hydration, float fear) => reason switch
    {
        DecisionReason.SafetyDehydration => Loc.F("reason.dehydration", hydration * 100f),
        DecisionReason.SafetyStarvation => Loc.F("reason.starvation", energy * 100f),
        DecisionReason.SafetyThreat => Loc.F("reason.threat", fear * 100f),
        _ => Loc.F("reason.score", Loc.Behavior(behavior), score),
    };

    public static string AmountText(BehaviorKind behavior, float amount) => behavior switch
    {
        BehaviorKind.Forage => Loc.F("amount.kg", amount),
        BehaviorKind.Drink => Loc.F("amount.water", amount * 100f),
        BehaviorKind.Rest => Loc.F("amount.hours", amount),
        BehaviorKind.Hunt => Loc.F("amount.kills", amount),
        _ => "",
    };

    public static string FormatLog(System.Collections.Generic.IReadOnlyList<BehaviorLogEntry> log, SimClock clock, int maxLines)
    {
        var sb = new StringBuilder();
        int start = System.Math.Max(0, log.Count - maxLines);
        for (int i = log.Count - 1; i >= start; i--)
        {
            BehaviorLogEntry e = log[i];
            double days = (double)(e.Tick + (long)(clock.StartHour / 24f * clock.TicksPerDay)) / clock.TicksPerDay;
            int day = (int)days + 1;
            double hour = (days - System.Math.Floor(days)) * 24.0;
            string amount = AmountText(e.Behavior, e.ResultAmount);
            sb.Append("[color=#9aa8a6]D").Append(day).Append(' ').Append(((int)hour).ToString("00")).Append(':').Append(((int)(hour * 60 % 60)).ToString("00")).Append("[/color] ");
            sb.Append("[b]").Append(Loc.Behavior(e.Behavior)).Append("[/b] — ");
            sb.Append(ReasonText(e.Reason, e.Behavior, e.Score, e.Energy, e.Hydration, e.Fear));
            sb.Append(" [color=#9aa8a6]→ ").Append(Loc.Result(e.Result));
            if (amount.Length > 0 && e.ResultAmount > 0f) sb.Append(", ").Append(amount);
            sb.Append("[/color]\n");
        }
        return sb.ToString();
    }

    public void ShowCreature(SimulationCore sim, CreatureDetails? d, bool hadSelection, bool following)
    {
        if (d == null)
        {
            _creatureBox.Visible = false;
            _hint.Visible = true;
            _hint.Text = hadSelection ? Loc.T("inspector.gone") + "\n\n" + Loc.T("inspector.none") : Loc.T("inspector.none");
            _speciesIndex = -1;
            return;
        }
        _hint.Visible = false;
        _creatureBox.Visible = true;
        SpeciesDefinition sp = sim.World.Species[d.SpeciesIndex];
        _speciesIndex = d.SpeciesIndex;
        _title.Text = $"{Loc.Name(sp.NameKo, sp.NameEn)} #{d.Id}";
        _subtitle.Text = sp.ScientificName;
        _meta.Text = Loc.F("inspector.age", d.AgeDays, Loc.T("stage." + d.LifeStage.ToString().ToLowerInvariant()), Loc.T("sex." + d.Sex.ToString().ToLowerInvariant()))
            + "\n" + Loc.F("inspector.growth", d.BodyScale, d.LifespanDays);

        float[] values = { d.Health, d.Energy, d.Hydration, d.Fatigue, d.Fear };
        for (int i = 0; i < 5; i++)
        {
            _bars[i].Value = values[i];
            _barValues[i].Text = (values[i] * 100f).ToString("0") + "%";
        }

        string amount = AmountText(d.Behavior, d.BoutAmount);
        _behavior.Text = $"{Loc.Behavior(d.Behavior)} · {Loc.Activity(d.Activity)}" + (amount.Length > 0 && d.BoutAmount > 0f ? $" · {amount}" : "");
        _reason.Text = Loc.T("inspector.reason") + ": " + ReasonText(d.Reason, d.Behavior, d.Scores[(int)d.Behavior], d.Energy, d.Hydration, d.Fear);

        // 육식동물: 쫓는 사냥감이나 먹는 사체.
        string target = "";
        if (d.Behavior == BehaviorKind.Hunt && d.TargetEntityId >= 0 && sim.TryGetCreature(d.TargetEntityId, out PaleoSphere.Creatures.Creature? prey) && prey != null)
        {
            SpeciesDefinition psp = sim.World.Species[prey.SpeciesIndex];
            target = Loc.F("inspector.hunting", Loc.Name(psp.NameKo, psp.NameEn), prey.Id, Vec2.Distance(d.Position, prey.Position));
        }
        else if (d.Behavior == BehaviorKind.Forage && sp.Diet == Diet.Carnivore && d.TargetEntityId >= 0 && sim.World.FindCarcass(d.TargetEntityId) is { } carcass)
        {
            SpeciesDefinition csp = sim.World.Species[carcass.SpeciesIndex];
            target = Loc.F("inspector.scavenging", Loc.Name(csp.NameKo, csp.NameEn), carcass.Id, carcass.MeatKg);
        }
        if (sp.IsPredator) target += (target.Length > 0 ? " · " : "") + Loc.F("inspector.kills", d.Kills);
        _target.Text = target;
        _target.Visible = target.Length > 0;

        for (int i = 0; i < ScoreOrder.Length; i++)
        {
            float s = d.Scores[(int)ScoreOrder[i]];
            _scoreBars[i].Value = s;
            _scoreLabels[i].Text = s.ToString("0.00");
            bool chosen = ScoreOrder[i] == d.Behavior;
            _scoreBars[i].Modulate = chosen ? Colors.White : new Color(1, 1, 1, 0.55f);
        }

        _traits.Text = Loc.F("inspector.traits", d.TraitBodySize, d.TraitMetabolism, d.TraitSpeed) + $"  [{Loc.Evidence(EvidenceLevel.Simplified)}]";
        _parents.Text = Loc.T("inspector.parents.none");
        _log.Text = FormatLog(d.Log, sim.Clock, 8);
        _follow.SetPressedNoSignal(following);
    }
}
