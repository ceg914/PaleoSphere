using System;
using System.Collections.Generic;
using Godot;
using PaleoSphere.Core;
using PaleoSphere.Diagnostics;
using PaleoSphere.Modding;
using PaleoSphere.Persistence;
using PaleoSphere.Presentation;
using PaleoSphere.Simulation;
using PaleoSphere.UI;
using PaleoSphere.World;

namespace PaleoSphere.App;

/// <summary>
/// 앱 진입점. 데이터를 읽고 세션·3D 화면·UI를 만든 뒤, 프레임마다
/// 입력 → 명령 → 시뮬레이션 진행 → 스냅샷 표시 순서로 돌린다.
/// 명령줄 인자(-- 뒤): --selftest, --scenario=id, --screenshot=경로, --frames=N, --warmup-ticks=N,
/// --autoplay=배속, --select, --select-activity=Eat|Drink|Rest|Walk|Run|Attack, --select-species=종id, --search-days=N, --zoom=거리, --hour=시각, --tab=N, --hide-start, --benchmark=개체수, --seconds=N, --uitest
/// </summary>
public partial class Main : Node
{
    private AppSettings _settings = new();
    private GameData? _data;
    private GameSession? _session;
    private WorldView _world = null!;
    private CreatureLayer _creatures = null!;
    private VegetationLayer _vegetation = null!;
    private CameraRig _camera = null!;
    private Hud? _hud;
    private Theme _theme = null!;

    private NewWorldDialog? _newDialog;
    private SaveLoadDialog? _saveDialog;
    private SaveLoadDialog? _loadDialog;
    private SettingsDialog? _settingsDialog;
    private CodexDialog? _codexDialog;
    private AcceptDialog? _messageDialog;

    private int _selectedId = -1;
    private bool _hadSelection;
    private Vec2? _cursorPoint;
    private bool _rightDragging;
    private string _lastSlot = "";

    private readonly Dictionary<string, string> _args = new();
    private int _frame;
    private bool _screenshotTaken;
    private double _benchElapsed;
    private int _benchFrames;

    public override void _Ready()
    {
        SimLog.Sink = (level, message) =>
        {
            if (level == LogLevel.Error) GD.PushError(message);
            else if (level == LogLevel.Warning) GD.PushWarning(message);
            else GD.Print(message);
        };
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            string a = arg.TrimStart('-');
            int eq = a.IndexOf('=');
            if (eq > 0) _args[a.Substring(0, eq)] = a.Substring(eq + 1);
            else _args[a] = "";
        }

        _settings = AppSettings.Load();
        Loc.Language = _settings.Language;
        _data = GameData.Load(new GodotDataSource());
        foreach (string w in _data.Report.Warnings) GD.PushWarning(w);
        foreach (string e in _data.Report.Errors) GD.PushError(e);

        if (_args.ContainsKey("selftest"))
        {
            bool ok = SelfTest.Run(_data, ProjectSettings.GlobalizePath("user://selftest"), s => GD.Print(s));
            GetTree().Quit(ok ? 0 : 1);
            return;
        }

        DisplayServer.WindowSetMinSize(new Vector2I(1180, 700));
        _theme = UiTheme.Build();

        if (!_data.IsUsable)
        {
            // 데이터가 깨져도 바로 꺼지지 않고 무엇이 잘못됐는지 보여 준다.
            ShowMessage(Loc.T("data.errors"), Loc.T("data.unusable") + "\n\n" + string.Join("\n", _data.Report.Errors));
            return;
        }

        _session = new GameSession(_data, ProjectSettings.GlobalizePath("user://saves"));
        _session.WorldReplaced += OnWorldReplaced;
        _session.Autosaved += (ok, message) => _hud?.Toast(ok ? Loc.T("save.auto") : Loc.F("save.failed", message));

        _world = new WorldView { Name = "WorldView" };
        AddChild(_world);
        _vegetation = new VegetationLayer { Name = "Vegetation" };
        AddChild(_vegetation);
        _creatures = new CreatureLayer { Name = "Creatures" };
        AddChild(_creatures);
        _camera = new CameraRig { Name = "CameraRig" };
        AddChild(_camera);

        BuildUi();
        ApplySettings();

        ScenarioDefinition? scenario = null;
        string wanted = _args.TryGetValue("scenario", out string? id) ? id : "lush_lowland";
        foreach (ScenarioDefinition s in _data.Scenarios)
            if (s.Id == wanted) scenario = s;
        if (scenario == null && _data.Scenarios.Count > 0) scenario = _data.Scenarios[0];
        if (scenario != null) _session.NewWorld(scenario);
        else _session.NewWorld(new WorldConfig(), new List<ScenarioPopulation>(), Loc.T("world.default"));

        if (_data.Report.Errors.Count > 0)
            ShowMessage(Loc.T("data.errors"), string.Join("\n", _data.Report.Errors));

        ApplyAutomationArgs();
    }

    // ------------------------------------------------------------ UI 구성

    private void BuildUi()
    {
        if (_session == null || _data == null) return;
        _hud?.QueueFree();
        foreach (Window? w in new Window?[] { _newDialog, _saveDialog, _loadDialog, _settingsDialog, _codexDialog })
            w?.QueueFree();

        _hud = new Hud(_session, _settings) { Name = "Hud", Theme = _theme };
        AddChild(_hud);
        _hud.NewWorldPressed += () => _newDialog?.PopupCentered();
        _hud.SavePressed += () => _saveDialog?.Open(_lastSlot.Length > 0 ? _lastSlot : _session.WorldName);
        _hud.LoadPressed += () => _loadDialog?.Open("");
        _hud.SettingsPressed += () => _settingsDialog?.PopupCentered();
        _hud.ContinuePressed += () => LoadSlot(SaveFileStore.AutosaveSlot);
        _hud.CodexRequested += index => _codexDialog?.ShowSpecies(_data.Species[index], _data.Biomes);
        _hud.LoadTestRequested += count =>
        {
            if (_session.Sim == null) return;
            float size = _session.Sim.World.Terrain.WorldSize;
            _session.Sim.Enqueue(new SpawnCreaturesCommand(_hud.PlaceSpeciesId, count, new Vec2(size / 2, size / 2), size * 0.45f));
        };
        _hud.Inspector.FollowPressed += ToggleFollow;
        _hud.Inspector.RemovePressed += () =>
        {
            if (_selectedId >= 0) _session.Sim?.Enqueue(new RemoveCreatureCommand(_selectedId));
        };

        _newDialog = new NewWorldDialog(_data) { Theme = _theme };
        _newDialog.Create += (config, populations, name) =>
        {
            try
            {
                _session.NewWorld(config, populations, name);
                _hud.HideStartPanel();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                ShowMessage(Loc.T("new.title"), e.Message);
            }
        };
        AddChild(_newDialog);

        _saveDialog = new SaveLoadDialog(_session.Store, saving: true) { Theme = _theme };
        _saveDialog.SlotChosen += SaveSlot;
        AddChild(_saveDialog);
        _loadDialog = new SaveLoadDialog(_session.Store, saving: false) { Theme = _theme };
        _loadDialog.SlotChosen += LoadSlot;
        AddChild(_loadDialog);

        _settingsDialog = new SettingsDialog(_settings) { Theme = _theme };
        _settingsDialog.Applied += () =>
        {
            bool languageChanged = Loc.Language != _settings.Language;
            ApplySettings();
            if (languageChanged)
            {
                BuildUi();
                _hud?.HideStartPanel();
                _hud?.OnWorldReplaced();
            }
        };
        AddChild(_settingsDialog);

        _codexDialog = new CodexDialog { Theme = _theme };
        AddChild(_codexDialog);
    }

    private void ApplySettings()
    {
        if (_session == null) return;
        Loc.Language = _settings.Language;
        GetTree().Root.ContentScaleFactor = _settings.UiScale;
        _session.AutosaveIntervalSeconds = _settings.AutosaveMinutes * 60.0;
        _session.Driver.MaxFrameMilliseconds = _settings.SimBudgetMs;
    }

    private void ShowMessage(string title, string text)
    {
        if (_messageDialog == null)
        {
            _messageDialog = new AcceptDialog { Theme = _theme, MinSize = new Vector2I(520, 160) };
            _messageDialog.GetLabel().AutowrapMode = TextServer.AutowrapMode.WordSmart;
            AddChild(_messageDialog);
        }
        _messageDialog.Title = title;
        _messageDialog.DialogText = text;
        _messageDialog.PopupCentered();
    }

    private void SaveSlot(string slot)
    {
        if (_session == null) return;
        (bool ok, string message) = _session.Save(slot);
        if (ok) _lastSlot = slot;
        _hud?.Toast(ok ? Loc.F("save.done", slot) : Loc.F("save.failed", message), ok ? 3.5 : 8);
    }

    private void LoadSlot(string slot)
    {
        if (_session == null) return;
        LoadOutcome outcome = _session.Load(slot);
        if (!outcome.Ok)
        {
            ShowMessage(Loc.T("load.failed"), string.Join("\n", outcome.Errors));
            return;
        }
        _lastSlot = slot;
        _hud?.HideStartPanel();
        var notes = new List<string> { Loc.T("load.done") };
        if (outcome.UsedBackup) notes.Add(Loc.T("load.backup"));
        notes.AddRange(outcome.Warnings);
        if (outcome.UsedBackup || outcome.Warnings.Count > 0) ShowMessage(Loc.T("load.title"), string.Join("\n", notes));
        else _hud?.Toast(notes[0]);
    }

    private void OnWorldReplaced()
    {
        SimulationCore? sim = _session?.Sim;
        if (sim == null) return;
        _selectedId = -1;
        _hadSelection = false;
        _creatures.Clear();
        _world.Rebuild(sim);
        _vegetation.Rebuild(sim);
        _camera.SetTerrain(sim.World.Terrain);
        _hud?.OnWorldReplaced();
    }

    // ------------------------------------------------------------ 프레임

    public override void _Process(double delta)
    {
        SimulationCore? sim = _session?.Sim;
        if (_session == null || sim == null || _hud == null) return;

        if (!IsTyping())
        {
            var move = Vector2.Zero;
            if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) move.Y += 1f;
            if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) move.Y -= 1f;
            if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) move.X += 1f;
            if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) move.X -= 1f;
            if (!Input.IsKeyPressed(Key.Ctrl)) _camera.Pan(move, (float)delta);
        }

        _session.Update(delta);
        sim = _session.Sim!;

        foreach (CommandResult r in sim.DrainCommandResults())
        {
            if (r.CommandName == "disturbance" && r.Ok) continue;
            _hud.Toast(Loc.F("cmd." + r.MessageKey, r.Count), r.Ok ? 2.5 : 5);
        }

        _creatures.SelectedId = _selectedId;
        _creatures.UpdateFrame(_session.Snapshot, sim.World.Species, sim.World.Terrain, _session.Driver.Alpha, delta, _camera.Camera.GlobalPosition);
        if (_selectedId >= 0 && _creatures.SelectedPosition == null)
        {
            // 선택한 개체가 죽거나 지워졌다.
            _selectedId = -1;
            _hadSelection = true;
            _camera.Following = false;
        }
        if (_creatures.SelectedPosition is Vector3 followPoint) _camera.SetFollowPoint(followPoint);

        UpdateCursor(sim);
        _hud.Refresh(delta, _selectedId, _hadSelection, _camera.Following, _cursorPoint);
        RunAutomation(delta);
    }

    private bool IsTyping()
    {
        Control? focus = GetViewport().GuiGetFocusOwner();
        return focus is LineEdit or TextEdit || AnyDialogOpen();
    }

    private bool AnyDialogOpen()
    {
        foreach (Window? w in new Window?[] { _newDialog, _saveDialog, _loadDialog, _settingsDialog, _codexDialog, _messageDialog })
            if (w != null && w.Visible) return true;
        return false;
    }

    private void UpdateCursor(SimulationCore sim)
    {
        Vector2 mouse = GetViewport().GetMousePosition();
        Vector3 origin = _camera.Camera.ProjectRayOrigin(mouse);
        Vector3 dir = _camera.Camera.ProjectRayNormal(mouse);
        bool overUi = GetViewport().GuiGetHoveredControl() != null;
        if (!overUi && Picking.RayTerrain(sim.World.Terrain, origin, dir, out Vector3 hit, out _))
        {
            _cursorPoint = new Vec2(hit.X, hit.Z);
            switch (_hud!.Tool)
            {
                case ToolMode.Place: _world.SetHighlight(hit, _hud.PlaceRadius, new Color(0.55f, 0.90f, 0.45f)); break;
                case ToolMode.Disturb: _world.SetHighlight(hit, Hud.DisturbRadius, new Color(0.95f, 0.45f, 0.35f)); break;
                default: _world.SetHighlight(Vector3.Zero, 0f, Colors.White); break;
            }
        }
        else
        {
            _cursorPoint = null;
            _world.SetHighlight(Vector3.Zero, 0f, Colors.White);
        }
    }

    // ------------------------------------------------------------ 입력

    public override void _UnhandledInput(InputEvent @event)
    {
        SimulationCore? sim = _session?.Sim;
        if (_session == null || sim == null || _hud == null) return;

        switch (@event)
        {
            case InputEventMouseButton mb:
                if (mb.ButtonIndex == MouseButton.Right) _rightDragging = mb.Pressed;
                else if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed) _camera.Zoom(0.88f);
                else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) _camera.Zoom(1.14f);
                else if (mb.ButtonIndex == MouseButton.Left && mb.Pressed) OnWorldClick(sim, mb.Position);
                break;

            case InputEventMouseMotion mm when _rightDragging:
                _camera.Rotate(mm.Relative);
                break;

            case InputEventKey key when key.Pressed && !key.Echo:
                OnKey(key);
                break;
        }
    }

    private void OnWorldClick(SimulationCore sim, Vector2 mouse)
    {
        Vector3 origin = _camera.Camera.ProjectRayOrigin(mouse);
        Vector3 dir = _camera.Camera.ProjectRayNormal(mouse);
        bool hitTerrain = Picking.RayTerrain(sim.World.Terrain, origin, dir, out Vector3 hit, out float distance);
        switch (_hud!.Tool)
        {
            case ToolMode.Place:
                if (hitTerrain)
                    sim.Enqueue(new SpawnCreaturesCommand(_hud.PlaceSpeciesId, _hud.PlaceCount, new Vec2(hit.X, hit.Z), _hud.PlaceRadius));
                break;
            case ToolMode.Disturb:
                if (hitTerrain) sim.Enqueue(new DisturbanceCommand(new Vec2(hit.X, hit.Z), Hud.DisturbRadius, 1f));
                break;
            default:
                int id = Picking.PickCreature(_session!.Snapshot, sim.World.Species, sim.World.Terrain, origin, dir, hitTerrain ? distance + 6f : 6000f);
                _selectedId = id;
                _hadSelection = false;
                if (id < 0) _camera.Following = false;
                else _hud.HideStartPanel();
                break;
        }
    }

    private void OnKey(InputEventKey key)
    {
        if (key.CtrlPressed && key.Keycode == Key.S)
        {
            SaveSlot(_lastSlot.Length > 0 ? _lastSlot : "quicksave");
            GetViewport().SetInputAsHandled();
            return;
        }
        if (key.AltPressed && (key.Keycode == Key.Enter || key.Keycode == Key.KpEnter))
        {
            bool full = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen;
            DisplayServer.WindowSetMode(full ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
            return;
        }
        switch (key.Keycode)
        {
            case Key.Space: _hud!.TogglePause(); break;
            case Key.F: ToggleFollow(); break;
            case Key.G: _hud!.ToggleDock(); break;
            case Key.Escape:
                if (_hud!.Tool != ToolMode.Select) _hud.SetTool(ToolMode.Select);
                else if (_hud.StartPanelVisible) _hud.HideStartPanel();
                else
                {
                    _selectedId = -1;
                    _camera.Following = false;
                }
                break;
            case Key.Key1: _hud!.SetSpeed(1f); break;
            case Key.Key2: _hud!.SetSpeed(2f); break;
            case Key.Key3: _hud!.SetSpeed(5f); break;
            case Key.Key4: _hud!.SetSpeed(10f); break;
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    private void ToggleFollow()
    {
        if (_selectedId < 0 || _creatures.SelectedPosition is not Vector3 p)
        {
            _camera.Following = false;
            return;
        }
        _camera.Following = !_camera.Following;
        if (_camera.Following) _camera.FocusOn(p, zoomIn: true);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) _settings.Save();
    }

    // ------------------------------------------------------------ 자동 실행(검증·측정용 인자)

    private int ArgInt(string name, int fallback) =>
        _args.TryGetValue(name, out string? v) && int.TryParse(v, out int n) ? n : fallback;

    private void ApplyAutomationArgs()
    {
        SimulationCore? sim = _session?.Sim;
        if (_session == null || sim == null || _hud == null) return;
        if (_args.ContainsKey("hide-start") || _args.ContainsKey("screenshot") || _args.ContainsKey("benchmark")) _hud.HideStartPanel();

        int bench = ArgInt("benchmark", 0);
        if (bench > 0)
        {
            float size = sim.World.Terrain.WorldSize;
            int remaining = bench - sim.CreatureCount;
            while (remaining > 0)
            {
                int n = Math.Min(remaining, SimulationCore.MaxSpawnPerCommand);
                sim.Enqueue(new SpawnCreaturesCommand(_data!.Species[0].Id, n, new Vec2(size / 2, size / 2), size * 0.45f));
                remaining -= n;
            }
            sim.ApplyPendingCommands();
        }

        int warmup = ArgInt("warmup-ticks", 0);
        for (int i = 0; i < warmup; i++) sim.Step();

        if (_args.TryGetValue("autoplay", out string? speed) && float.TryParse(speed, out float s)) _hud.SetSpeed(s);
        if (_args.ContainsKey("tab")) _hud.SelectDockTab(ArgInt("tab", 0));
        if (!_args.ContainsKey("select-activity") && _args.TryGetValue("hour", out string? hourText) && float.TryParse(hourText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float wantedHour))
        {
            // 화면 확인용: 원하는 시각이 될 때까지 진행한다(최대 하루).
            for (int i = 0; i < sim.World.Config.TicksPerDay && Math.Abs(sim.Clock.TimeOfDayHours - wantedHour) > 0.05f; i++) sim.Step();
        }
        if (_args.TryGetValue("select-activity", out string? activityName) && Enum.TryParse(activityName, true, out PaleoSphere.Creatures.ActivityKind wantedActivity))
        {
            // 화면 확인용: 해당 동작을 하는 개체가 나올 때까지 진행한 뒤 고른다(기본 하루, --search-days로 늘림).
            // --hour를 함께 주면 밝은 낮(8~16시)에 그 동작을 하는 개체만 고른다.
            PaleoSphere.Creatures.Creature? found = null;
            bool daylightOnly = _args.ContainsKey("hour");
            long searchTicks = (long)sim.World.Config.TicksPerDay * Math.Max(1, ArgInt("search-days", 1));
            for (long i = 0; i < searchTicks && found == null; i++)
            {
                if (daylightOnly && (sim.Clock.TimeOfDayHours < 8f || sim.Clock.TimeOfDayHours > 16f)) { sim.Step(); continue; }
                _args.TryGetValue("select-species", out string? wantedSpecies);
                foreach (PaleoSphere.Creatures.Creature c in sim.Creatures)
                {
                    if (wantedSpecies != null && sim.World.Species[c.SpeciesIndex].Id != wantedSpecies) continue;
                    if (c.Activity != wantedActivity || (wantedActivity == PaleoSphere.Creatures.ActivityKind.Rest && c.Fatigue > 0.5f)) continue;
                    // 동작을 시작한 지 조금 지난 개체를 고른다.
                    if (sim.Clock.Tick - c.BehaviorStartTick < 300) continue;
                    found = c;
                    break;
                }
                if (found == null) sim.Step();
            }
            if (found != null)
            {
                _selectedId = found.Id;
                _camera.FocusOn(new Vector3(found.Position.X, sim.World.Terrain.HeightAt(found.Position), found.Position.Z), zoomIn: true);
                _camera.Following = true;
            }
        }
        if (_args.ContainsKey("select") && sim.CreatureCount > 0)
        {
            _selectedId = sim.Creatures[sim.CreatureCount / 2].Id;
            Vec2 p = sim.Creatures[sim.CreatureCount / 2].Position;
            _camera.FocusOn(new Vector3(p.X, sim.World.Terrain.HeightAt(p), p.Z), zoomIn: true);
            _camera.Following = true;
        }
        if (_args.TryGetValue("zoom", out string? zoom) && float.TryParse(zoom, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float distance))
            _camera.SetDistance(distance);
        if (_args.ContainsKey("codex")) _codexDialog?.ShowSpecies(_data!.Species[0], _data.Biomes);
        if (_args.ContainsKey("newdialog")) _newDialog?.PopupCentered();
    }

    // ---- UI 통합 점검(--uitest): 실제 입력 이벤트를 넣어 클릭 → 명령 → 시뮬레이션 → 화면 경로를 확인한다.
    private readonly List<string> _uiFailures = new();
    private int _uiCount;
    private int _uiTargetId = -1;
    private ulong _uiHash;

    private void UiCheck(bool ok, string what)
    {
        GD.Print((ok ? "  [통과] " : "  [실패] ") + what);
        if (!ok) _uiFailures.Add(what);
    }

    private Vector2 ScreenOf(SimulationCore sim, Vec2 p) =>
        _camera.Camera.UnprojectPosition(new Vector3(p.X, sim.World.Terrain.HeightAt(p) + 1.5f, p.Z));

    private static void Click(Vector2 at)
    {
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = at, GlobalPosition = at });
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = at, GlobalPosition = at });
    }

    private static void Press(Key key) =>
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });

    private void RunUiTest(SimulationCore sim)
    {
        switch (_frame)
        {
            case 5:
                GD.Print("PALEOSPHERE UI 점검 시작");
                _hud!.HideStartPanel();
                _session!.Driver.Paused = true;
                _uiTargetId = sim.Creatures[0].Id;
                Vec2 p0 = sim.Creatures[0].Position;
                _camera.FocusOn(new Vector3(p0.X, sim.World.Terrain.HeightAt(p0), p0.Z), zoomIn: true);
                break;
            case 30:
                Click(ScreenOf(sim, sim.Creatures[0].Position));
                break;
            case 36:
                UiCheck(_selectedId == _uiTargetId, $"클릭으로 개체 선택 (기대 #{_uiTargetId}, 실제 #{_selectedId})");
                UiCheck(sim.GetDetails(_selectedId) != null, "선택한 개체의 상세 정보 조회");
                Press(Key.F);
                break;
            case 40:
                UiCheck(_camera.Following, "F 키로 추적 켜기");
                Press(Key.Space);
                break;
            case 44:
                UiCheck(!_session!.Driver.Paused, "Space 키로 재생");
                _uiCount = sim.CreatureCount;
                _hud!.SetTool(ToolMode.Place);
                Click(ScreenOf(sim, sim.Creatures[0].Position));
                break;
            case 50:
                UiCheck(sim.CreatureCount == _uiCount + _hud!.PlaceCount, $"배치 도구 클릭으로 개체 추가 ({_uiCount} → {sim.CreatureCount})");
                _hud.SetTool(ToolMode.Disturb);
                Click(ScreenOf(sim, sim.Creatures[0].Position));
                break;
            case 70:
                int fleeing = 0;
                foreach (PaleoSphere.Creatures.Creature c in sim.Creatures)
                    if (c.Behavior == PaleoSphere.Creatures.BehaviorKind.Flee) fleeing++;
                UiCheck(fleeing > 0, $"교란 도구 클릭으로 도주 유발 ({fleeing}마리)");
                Press(Key.Escape);
                break;
            case 74:
                UiCheck(_hud!.Tool == ToolMode.Select, "Esc 키로 도구 끄기");
                _session!.Driver.Paused = true;
                sim.ApplyPendingCommands();
                _uiHash = sim.ComputeStateHash();
                _uiCount = sim.CreatureCount;
                SaveSlot("uitest");
                break;
            case 78:
                UiCheck(System.IO.File.Exists(_session!.Store.PathFor("uitest")), "저장: 세이브 파일 생성");
                sim.Enqueue(new RemoveCreatureCommand(sim.Creatures[0].Id));
                break;
            case 82:
                UiCheck(sim.CreatureCount == _uiCount - 1, "삭제 명령으로 개체 수 감소");
                LoadSlot("uitest");
                break;
            case 88:
                UiCheck(sim.CreatureCount == _uiCount && sim.ComputeStateHash() == _uiHash, "불러오기: 저장 시점의 상태로 복원");
                UiCheck(_session!.Driver.Paused, "불러온 뒤 일시정지 상태");
                _session.Store.Delete("uitest");
                GD.Print(_uiFailures.Count == 0 ? "UITEST PASS" : $"UITEST FAIL ({_uiFailures.Count}건)");
                GetTree().Quit(_uiFailures.Count == 0 ? 0 : 1);
                break;
        }
    }

    private async void RunAutomation(double delta)
    {
        _frame++;
        SimulationCore? sim = _session?.Sim;
        if (sim == null) return;

        if (_args.ContainsKey("uitest"))
        {
            RunUiTest(sim);
            return;
        }

        if (_args.ContainsKey("benchmark") && !_args.ContainsKey("screenshot"))
        {
            // 처음 1초는 준비 시간으로 빼고 잰다.
            if (_frame > 30)
            {
                _benchElapsed += delta;
                _benchFrames++;
            }
            if (_benchElapsed >= ArgInt("seconds", 10))
            {
                var p = sim.Profiler;
                GD.Print($"BENCHMARK 개체 {sim.CreatureCount} | {_benchElapsed:0.0}초 | 평균 {_benchFrames / _benchElapsed:0.0} FPS (프레임 {_benchElapsed / _benchFrames * 1000:0.00} ms) | 틱 평균 {p.AverageTickMs:0.000} ms, 최대 {p.MaxTickMs:0.00} ms | 실효 배속 {_session!.Driver.EffectiveSpeed:0.00}x | 렌더러 {RenderingServer.GetVideoAdapterName()}");
                GetTree().Quit();
            }
            return;
        }

        if (_screenshotTaken || !_args.TryGetValue("screenshot", out string? path) || _frame < ArgInt("frames", 90)) return;
        _screenshotTaken = true;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = GetViewport().GetTexture().GetImage();
        Error err = image.SavePng(path);
        GD.Print(err == Error.Ok ? $"SCREENSHOT {path} ({image.GetWidth()}x{image.GetHeight()})" : $"SCREENSHOT FAILED {err}");
        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }
}
