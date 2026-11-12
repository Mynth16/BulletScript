using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BulletScript.Compiler;
using Godot;

namespace BulletScript.EditorTools;

// The BulletScript main-screen tab: top bar, code editor with inline widgets on the left,
// performance monitor + live preview + presets on the right.
[Tool]
public partial class EditorMain : Control
{
    const string SyncFile = "res://.bulletscript_sync";
    const string PreviewPatternName = "bs_editor_preview";
    const string NewFileTemplate =
        "SEED 1234;\n\n" +
        "BULLET orb {\n\tspeed: 2;\n\tsize: 8;\n\tlifetime: 4s;\n\tsprite: \"orb_small\";\n}\n\n" +
        "SPAWN_POINT center { x: 0; y: 0; }\n\n" +
        "SPAWN_PATTERN ring {\n\tSPAWN orb AT center {\n\t\tcount: 24;\n\t\tspread: 360deg;\n\t}\n}\n";

    // ---- UI ----
    BsCodeEdit _edit;
    InlineWidgets _widgets;
    PreviewPlayer _player;
    PerfMonitor _perf;
    PresetBar _presets;
    Label _fileTab, _status, _badge;
    Button _filePill, _exportBtn;
    MenuButton _syncMenu;
    OptionButton _patternBox;
    VBoxContainer _paramBox;
    Godot.Timer _debounce;
    FileDialog _openDlg, _newDlg, _exportDlg;
    ConfirmationDialog _rangeDlg;
    SpinBox _rMin, _rMax, _rStep;
    string _rangeKey;

    // ---- state ----
    string _path = "";
    bool _dirty, _loading;
    bool _lastCompileOk;
    string _selectedPattern;
    List<PatternInfo> _patterns = new();
    readonly Dictionary<string, string> _paramValues = new();
    double _perfAcc;
    bool _savedLowProcessor;
    bool _lowProcessorOverridden;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        BuildUi();
        VisibilityChanged += OnVisibilityChanged;

        string last = EditorConfig.LastFile;
        if (!string.IsNullOrEmpty(last) && Godot.FileAccess.FileExists(last)) OpenFile(last);
        else OpenFile(FindFirstBsFile() ?? "");
        if (string.IsNullOrEmpty(_path)) _edit.Text = NewFileTemplate;
        _widgets.Refresh();
        Recompile();
    }

    public override void _ExitTree() => RestoreLowProcessor();

    // The editor sleeps when idle ("low processor mode"); the live preview needs it to keep drawing.
    void OnVisibilityChanged()
    {
        if (IsVisibleInTree())
        {
            if (!_lowProcessorOverridden)
            {
                _savedLowProcessor = OS.LowProcessorUsageMode;
                OS.LowProcessorUsageMode = false;
                _lowProcessorOverridden = true;
            }
        }
        else RestoreLowProcessor();
    }

    void RestoreLowProcessor()
    {
        if (!_lowProcessorOverridden) return;
        OS.LowProcessorUsageMode = _savedLowProcessor;
        _lowProcessorOverridden = false;
    }

    static string FindFirstBsFile()
    {
        foreach (var dir in new[] { "res://", "res://patterns/" })
        {
            using var d = DirAccess.Open(dir);
            if (d == null) continue;
            foreach (var f in d.GetFiles())
                if (f.EndsWith(".bs")) return dir + f;
        }
        return null;
    }

    // ======================================================== UI construction

    void BuildUi()
    {
        var bg = new Panel();
        bg.AddThemeStyleboxOverride("panel", Ui.Box(Ui.Bg, null, 0, 0));
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var root = new VBoxContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        root.AddChild(BuildTopBar());

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(split);
        split.AddChild(BuildLeft());
        split.AddChild(BuildRight());

        _debounce = new Godot.Timer { OneShot = true, WaitTime = 0.25 };
        _debounce.Timeout += OnDebounce;
        AddChild(_debounce);

        BuildDialogs();
    }

    Control BuildTopBar()
    {
        var bar = new PanelContainer();
        bar.AddThemeStyleboxOverride("panel", Ui.Box(Ui.Panel, Ui.Border, 0, 10));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        bar.AddChild(row);

        var title = Ui.MakeLabel("◉  BulletScript", 15, Colors.White);
        row.AddChild(title);
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        _filePill = new Button { Text = "-", TooltipText = "Open a .bs file" };
        Ui.StyleButton(_filePill);
        _filePill.Pressed += () => _openDlg.PopupCentered(new Vector2I(700, 460));
        row.AddChild(_filePill);
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        _syncMenu = new MenuButton { Text = "Live Sync OFF", TooltipText = "When the game is run from the editor it reloads the pattern" };
        Ui.StyleButton(_syncMenu);
        var pm = _syncMenu.GetPopup();
        pm.HideOnCheckableItemSelection = false;
        pm.AddCheckItem("Sync on save   (Ctrl+S)", 0);
        pm.AddCheckItem("Sync on edit   (as you type)", 1);
        pm.SetItemChecked(0, EditorConfig.SyncOnSave);
        pm.SetItemChecked(1, EditorConfig.SyncOnEdit);
        pm.IdPressed += id =>
        {
            int i = (int)id;
            bool v = !pm.IsItemChecked(i);
            pm.SetItemChecked(i, v);
            if (i == 0) EditorConfig.SyncOnSave = v; else EditorConfig.SyncOnEdit = v;
            UpdateSyncButton();
        };
        row.AddChild(_syncMenu);

        var save = new Button { Text = "Save", TooltipText = "Ctrl+S" };
        Ui.StyleButton(save);
        save.Pressed += () => Save(EditorConfig.SyncOnSave);
        row.AddChild(save);

        _exportBtn = new Button { Text = "Export JSON", TooltipText = "Write the compiled program as JSON" };
        Ui.StyleButton(_exportBtn);
        _exportBtn.Pressed += () => _exportDlg.PopupCentered(new Vector2I(700, 460));
        row.AddChild(_exportBtn);

        var plus = new Button { Text = "+", TooltipText = "New .bs file" };
        Ui.StyleButton(plus);
        plus.Pressed += () => _newDlg.PopupCentered(new Vector2I(700, 460));
        row.AddChild(plus);

        UpdateSyncButton();
        return bar;
    }

    Control BuildLeft()
    {
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 0);

        var tabBar = new PanelContainer();
        tabBar.AddThemeStyleboxOverride("panel", Ui.Box(Ui.Bg, null, 0, 6));
        _fileTab = Ui.MakeLabel("-", 12, Ui.Text);
        var tabPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        tabPanel.AddThemeStyleboxOverride("panel", Ui.Box(Ui.Panel, Ui.Border, 4, 6));
        tabPanel.AddChild(_fileTab);
        tabBar.AddChild(tabPanel);
        left.AddChild(tabBar);

        _edit = new BsCodeEdit();
        left.AddChild(_edit);
        _edit.TextChanged += OnTextChanged;
        _edit.SaveRequested += () => Save(EditorConfig.SyncOnSave);

        _widgets = new InlineWidgets();
        _widgets.Init(_edit);
        _widgets.TextEdited += OnWidgetEdited;
        _widgets.RangeEditRequested += OpenRangeDialog;

        var statusBar = new PanelContainer();
        statusBar.AddThemeStyleboxOverride("panel", Ui.Box(Ui.Panel, Ui.Border, 0, 6));
        _status = Ui.MakeLabel("", 12, Ui.Dim);
        _status.ClipText = true;
        statusBar.AddChild(_status);
        left.AddChild(statusBar);
        return left;
    }

    Control BuildRight()
    {
        var wrap = new PanelContainer { CustomMinimumSize = new Vector2(400, 0) };
        wrap.AddThemeStyleboxOverride("panel", Ui.Box(Ui.Bg, Ui.Border, 0, 12));

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        wrap.AddChild(col);

        _perf = new PerfMonitor();
        col.AddChild(_perf);

        // pattern picker + restart
        var pr = new HBoxContainer();
        pr.AddThemeConstantOverride("separation", 6);
        pr.AddChild(Ui.MakeLabel("Pattern", 11, Ui.Dim));
        _patternBox = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _patternBox.ItemSelected += idx =>
        {
            _selectedPattern = _patterns.Count > idx ? _patterns[(int)idx].Name : null;
            Recompile();
        };
        pr.AddChild(_patternBox);
        var restart = new Button { Text = "↻", TooltipText = "Restart preview" };
        Ui.StyleButton(restart);
        restart.Pressed += () => _player.Restart();
        pr.AddChild(restart);
        col.AddChild(pr);

        _paramBox = new VBoxContainer { Visible = false };
        _paramBox.AddThemeConstantOverride("separation", 4);
        col.AddChild(_paramBox);

        // preview header + badge
        var ph = new HBoxContainer();
        ph.AddChild(Ui.MakeLabel("LIVE PREVIEW", 10, Ui.Dim));
        ph.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _badge = Ui.MakeLabel("● SYNCED", 10, Ui.Accent);
        ph.AddChild(_badge);
        col.AddChild(ph);

        var svc = new SubViewportContainer
        {
            Stretch = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 240),
        };
        var vp = new SubViewport
        {
            TransparentBg = false,
            Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            HandleInputLocally = false,
        };
        var black = new ColorRect { Color = Colors.Black };
        black.SetAnchorsPreset(LayoutPreset.FullRect);
        vp.AddChild(black);
        _player = new PreviewPlayer();
        vp.AddChild(_player);
        svc.AddChild(vp);
        col.AddChild(svc);

        _presets = new PresetBar();
        _presets.Changed += OnPresetChanged;
        col.AddChild(_presets);
        return wrap;
    }

    void BuildDialogs()
    {
        _openDlg = MakeFileDialog(FileDialog.FileModeEnum.OpenFile, "Open BulletScript file");
        _openDlg.FileSelected += OpenFile;
        _newDlg = MakeFileDialog(FileDialog.FileModeEnum.SaveFile, "New BulletScript file");
        _newDlg.FileSelected += CreateNewFile;
        _exportDlg = MakeFileDialog(FileDialog.FileModeEnum.SaveFile, "Export compiled JSON", "*.json", "JSON");
        _exportDlg.FileSelected += ExportJson;

        _rangeDlg = new ConfirmationDialog { Title = "Slider range" };
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddChild(Ui.MakeLabel("Min", 12));
        _rMin = new SpinBox { MinValue = -1e6, MaxValue = 1e6, Step = 0.001, CustomMinimumSize = new Vector2(140, 0) };
        grid.AddChild(_rMin);
        grid.AddChild(Ui.MakeLabel("Max", 12));
        _rMax = new SpinBox { MinValue = -1e6, MaxValue = 1e6, Step = 0.001 };
        grid.AddChild(_rMax);
        grid.AddChild(Ui.MakeLabel("Step", 12));
        _rStep = new SpinBox { MinValue = 0.0001, MaxValue = 1e6, Step = 0.0001 };
        grid.AddChild(_rStep);
        _rangeDlg.AddChild(grid);
        _rangeDlg.AddButton("Reset to default", false, "reset");
        _rangeDlg.Confirmed += () =>
        {
            if (_rangeKey == null) return;
            double min = _rMin.Value, max = _rMax.Value, step = Math.Max(_rStep.Value, 0.0001);
            if (max <= min) max = min + step;
            EditorConfig.SetRange(_rangeKey, new EditorConfig.SliderRange(min, max, step));
            _widgets.ReapplyRanges();
        };
        _rangeDlg.CustomAction += action =>
        {
            if (action == "reset" && _rangeKey != null)
            {
                EditorConfig.ResetRange(_rangeKey);
                _widgets.ReapplyRanges();
                _rangeDlg.Hide();
            }
        };
        AddChild(_openDlg); AddChild(_newDlg); AddChild(_exportDlg); AddChild(_rangeDlg);
    }

    static FileDialog MakeFileDialog(FileDialog.FileModeEnum mode, string title, string filter = "*.bs", string filterName = "BulletScript")
    {
        var d = new FileDialog
        {
            FileMode = mode,
            Access = FileDialog.AccessEnum.Resources,
            Title = title,
            UseNativeDialog = false,
        };
        d.AddFilter(filter, filterName);
        return d;
    }

    void OpenRangeDialog(string key)
    {
        _rangeKey = key;
        var r = EditorConfig.GetRange(key);
        _rangeDlg.Title = $"Slider range for '{key}'  (seconds / degrees / plain numbers)";
        _rMin.Value = r.Min; _rMax.Value = r.Max; _rStep.Value = r.Step;
        _rangeDlg.PopupCentered();
    }

    // ======================================================== file handling

    void OpenFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !Godot.FileAccess.FileExists(path)) return;
        string text = Godot.FileAccess.GetFileAsString(path).Replace("\r\n", "\n");
        _loading = true;
        _path = path;
        _edit.Text = text;
        _edit.ClearUndoHistory();
        _edit.ClearError();
        _loading = false;
        _dirty = false;
        EditorConfig.LastFile = path;
        UpdatePill();
        _widgets.Refresh();
        Recompile();
    }

    void CreateNewFile(string path)
    {
        if (!path.EndsWith(".bs")) path += ".bs";
        using (var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write))
        {
            if (f == null) { SetStatus($"Could not create {path}", Ui.Bad); return; }
            f.StoreString(NewFileTemplate);
        }
        OpenFile(path);
    }

    void Save(bool notifyGame)
    {
        if (string.IsNullOrEmpty(_path)) { _newDlg.PopupCentered(new Vector2I(700, 460)); return; }
        using (var f = Godot.FileAccess.Open(_path, Godot.FileAccess.ModeFlags.Write))
        {
            if (f == null) { SetStatus($"Could not write {_path}", Ui.Bad); return; }
            f.StoreString(_edit.Text);
        }
        _dirty = false;
        UpdatePill();
        if (notifyGame) NotifyGame();
    }

    // The running game (BulletScriptRunner with HotReload on) polls this file's modified time.
    void NotifyGame()
    {
        using var f = Godot.FileAccess.Open(SyncFile, Godot.FileAccess.ModeFlags.Write);
        f?.StoreString(_path + "\n" + Time.GetTicksMsec());
    }

    void ExportJson(string path)
    {
        if (!path.EndsWith(".json")) path += ".json";
        if (!TryCompile(_edit.Text, out var prog, out var msg, out _))
        {
            SetStatus("Export failed: " + msg, Ui.Bad);
            return;
        }
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
        if (f == null) { SetStatus($"Could not write {path}", Ui.Bad); return; }
        f.StoreString(JsonSerializer.Serialize(prog, opts));
        int n = prog.Patterns.Values.Sum(p => p.Count);
        SetStatus($"Exported {prog.Patterns.Count} pattern(s), {n} instructions to {path}", Ui.Good);
    }

    void UpdatePill()
    {
        string name = string.IsNullOrEmpty(_path) ? "untitled" : _path.GetFile();
        _filePill.Text = $"{name}  —  {(_dirty ? "Unsaved" : "Saved")}";
        _filePill.AddThemeColorOverride("font_color", _dirty ? Ui.Warn : Ui.Good);
        _fileTab.Text = name + (_dirty ? " •" : "");
    }

    void UpdateSyncButton()
    {
        bool on = EditorConfig.SyncOnSave || EditorConfig.SyncOnEdit;
        _syncMenu.Text = on ? "Live Sync ON" : "Live Sync OFF";
        Ui.StyleButton(_syncMenu, on);
    }

    // ======================================================== compile + preview

    void OnTextChanged()
    {
        if (_loading) return;
        _dirty = true;
        UpdatePill();
        _widgets.Refresh();
        _edit.ClearError();
        _debounce.Start();
    }

    void OnWidgetEdited()
    {
        if (_loading) return;
        _dirty = true;
        UpdatePill();
        _debounce.Start();
    }

    void OnDebounce()
    {
        Recompile();
        if (EditorConfig.SyncOnEdit && _dirty && _lastCompileOk && !string.IsNullOrEmpty(_path))
            Save(true);   // only push code that compiles
    }

    void OnPresetChanged()
    {
        Recompile();
    }

    bool TryCompile(string src, out CompiledProgram prog, out string msg, out int line)
    {
        prog = null; msg = null; line = -1;
        try
        {
            prog = BulletScriptCompiler.CompileSource(src, Engine.PhysicsTicksPerSecond);
            return true;
        }
        catch (LexError e) { msg = e.Message; line = e.Line; }
        catch (ParseError e) { msg = e.Message; line = e.Line; }
        catch (CompileError e) { msg = e.Message; line = e.Line; }
        catch (Exception e) { msg = e.Message; }
        return false;
    }

    void Recompile()
    {
        _lastCompileOk = false;
        string source = _edit.Text;

        List<Token> toks;
        try { toks = new Lexer(source).Tokenize(); }
        catch (LexError e) { ShowError(e.Message, e.Line); return; }

        UpdatePatternList(BsTokenScanner.FindPatterns(toks));
        if (_patterns.Count == 0 || _selectedPattern == null)
        {
            SetStatus("No SPAWN_PATTERN yet. Add one to see a preview.", Ui.Dim);
            SetBadge("● IDLE", Ui.Dim);
            return;
        }

        string key = _selectedPattern;
        var info = _patterns.First(p => p.Name == key);
        RebuildParamBox(info);
        if (info.Params.Count > 0)
        {
            // A parameterised pattern is a template, so preview it through a generated CALL.
            string args = string.Join(", ", info.Params.Select(p => ParamValue(info.Name, p)));
            source += $"\nSPAWN_PATTERN {PreviewPatternName} {{ CALL {info.Name}({args}); }}\n";
            key = PreviewPatternName;
        }

        if (!TryCompile(source, out var prog, out var msg, out int line))
        {
            // Errors in the generated CALL line (past the user's text) have no line to mark.
            ShowError(msg, line <= _edit.GetLineCount() ? line : -1);
            return;
        }

        var preset = _presets.Current;
        _player.SpeedScale = (float)preset.SpeedScale;
        _player.TimeScale = (float)preset.TimeScale;
        _player.Zoom = (float)preset.Zoom;
        _player.Loop = preset.Loop;

        if (!_player.Load(prog, key, out var err)) { ShowError(err, -1); return; }

        _lastCompileOk = true;
        _edit.ClearError();
        int events = prog.Patterns[key].Count;
        SetStatus($"OK · {events:N0} spawn events · {prog.Bullets.Count} bullet type(s)", Ui.Good);
        SetBadge("● SYNCED", Ui.Accent);

        foreach (var b in prog.Bullets)
            if (b.LifetimeMs <= 0)
                SetStatus($"OK, but bullet '{b.Name}' has no lifetime so it vanishes at once. Add `lifetime: 4s;`", Ui.Warn);
    }

    void ShowError(string msg, int line)
    {
        SetStatus(msg, Ui.Bad);
        SetBadge("● ERROR (showing last good run)", Ui.Bad);
        if (line > 0) _edit.MarkError(line - 1);
    }

    void SetStatus(string text, Color c)
    {
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", c);
    }

    void SetBadge(string text, Color c)
    {
        _badge.Text = text;
        _badge.AddThemeColorOverride("font_color", c);
    }

    // ---- pattern picker and parameter prompts ----

    void UpdatePatternList(List<PatternInfo> found)
    {
        bool same = found.Count == _patterns.Count
                    && found.Zip(_patterns).All(z => z.First.Name == z.Second.Name
                                                    && z.First.Params.SequenceEqual(z.Second.Params));
        if (same) return;

        _patterns = found;
        if (_selectedPattern == null || !_patterns.Any(p => p.Name == _selectedPattern))
            _selectedPattern = _patterns.FirstOrDefault()?.Name;

        _patternBox.Clear();
        int sel = 0;
        for (int i = 0; i < _patterns.Count; i++)
        {
            var p = _patterns[i];
            _patternBox.AddItem(p.Params.Count > 0 ? $"{p.Name}({string.Join(", ", p.Params)})" : p.Name);
            if (p.Name == _selectedPattern) sel = i;
        }
        if (_patterns.Count > 0) _patternBox.Select(sel);
    }

    string ParamValue(string pattern, string param)
        => _paramValues.TryGetValue($"{pattern}.{param}", out var v) && !string.IsNullOrWhiteSpace(v) ? v : "1";

    string _paramBoxFor;

    void RebuildParamBox(PatternInfo info)
    {
        string sig = info.Name + "(" + string.Join(",", info.Params) + ")";
        if (sig == _paramBoxFor) return;
        _paramBoxFor = sig;

        foreach (var c in _paramBox.GetChildren()) c.QueueFree();
        _paramBox.Visible = info.Params.Count > 0;
        if (info.Params.Count == 0) return;

        _paramBox.AddChild(Ui.MakeLabel($"PARAMETERS for {info.Name}  (any BulletScript expression: 12, 0.5s, #FF3030, 2 * 3)", 10, Ui.Dim));
        foreach (var p in info.Params)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            row.AddChild(new Label { Text = p, CustomMinimumSize = new Vector2(80, 0) });
            var edit = new LineEdit
            {
                Text = ParamValue(info.Name, p),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                PlaceholderText = "value",
            };
            string key = $"{info.Name}.{p}";
            edit.TextChanged += t =>
            {
                _paramValues[key] = t;
                _debounce.Start();
            };
            row.AddChild(edit);
            _paramBox.AddChild(row);
        }
    }

    // ======================================================== per-frame

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree()) return;
        _perfAcc += delta;
        if (_perfAcc < 0.25) return;
        _perfAcc = 0;
        _perf.SetStats(_player.ActiveBullets, Engine.GetFramesPerSecond(), _player.TickMs,
                       GC.GetTotalMemory(false) / 1048576.0, _player.Dropped);
    }
}
