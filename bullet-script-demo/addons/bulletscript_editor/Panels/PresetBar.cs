using System;
using Godot;

namespace BulletScript.EditorTools;

// Three simulation presets (playback speed, zoom, px-per-speed-unit, looping). The active
// preset is edited live by the controls underneath; right-click a preset to rename it.
[Tool]
public partial class PresetBar : VBoxContainer
{
    public event Action Changed;

    readonly Button[] _buttons = new Button[EditorConfig.PresetCount];
    SpinBox _time, _zoom, _speedScale;
    CheckBox _loop;
    ConfirmationDialog _renameDlg;
    LineEdit _renameEdit;
    int _renaming;
    bool _updating;

    public int Active { get; private set; }
    public EditorConfig.Preset Current => EditorConfig.GetPreset(Active);

    public PresetBar()
    {
        AddThemeConstantOverride("separation", 6);
        AddChild(Ui.MakeLabel("SIMULATION PRESET", 10, Ui.Dim));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        AddChild(row);

        var group = new ButtonGroup();
        for (int i = 0; i < _buttons.Length; i++)
        {
            int idx = i;
            var b = new Button
            {
                ToggleMode = true,
                ButtonGroup = group,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 44),
                TooltipText = "Click to select. Right-click to rename.",
            };
            Ui.StyleButton(b);
            b.AddThemeStyleboxOverride("pressed", Ui.Box(new Color("1d2a3a"), Ui.Accent, 6, 8));
            b.Pressed += () => Select(idx);
            b.GuiInput += e =>
            {
                if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right)
                    BeginRename(idx);
            };
            row.AddChild(b);
            _buttons[i] = b;
        }

        var grid = new HBoxContainer();
        grid.AddThemeConstantOverride("separation", 8);
        AddChild(grid);
        _time = AddSpin(grid, "Time ×", 0.05, 4, 0.05);
        _zoom = AddSpin(grid, "Zoom", 0.1, 2, 0.05);
        _speedScale = AddSpin(grid, "px/speed", 10, 200, 5);
        _loop = new CheckBox { Text = "Loop" };
        _loop.AddThemeColorOverride("font_color", Ui.Text);
        _loop.AddThemeFontSizeOverride("font_size", 11);
        _loop.Toggled += _ => WriteBack();
        grid.AddChild(_loop);

        _renameDlg = new ConfirmationDialog { Title = "Rename preset" };
        _renameEdit = new LineEdit { CustomMinimumSize = new Vector2(220, 0) };
        _renameDlg.AddChild(_renameEdit);
        _renameDlg.Confirmed += () =>
        {
            var p = EditorConfig.GetPreset(_renaming);
            p.Name = string.IsNullOrWhiteSpace(_renameEdit.Text) ? p.Name : _renameEdit.Text.Trim();
            EditorConfig.SetPreset(_renaming, p);
            RefreshButtons();
        };
        AddChild(_renameDlg);

        Active = EditorConfig.ActivePreset;
        RefreshButtons();
        LoadControls();
    }

    SpinBox AddSpin(HBoxContainer parent, string label, double min, double max, double step)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        box.AddChild(Ui.MakeLabel(label, 9, Ui.Dim));
        var s = new SpinBox { MinValue = min, MaxValue = max, Step = step, CustomMinimumSize = new Vector2(78, 0) };
        s.ValueChanged += _ => WriteBack();
        box.AddChild(s);
        parent.AddChild(box);
        return s;
    }

    void RefreshButtons()
    {
        for (int i = 0; i < _buttons.Length; i++)
        {
            var p = EditorConfig.GetPreset(i);
            _buttons[i].Text = $"{i + 1}\n{p.Name}";
            _buttons[i].SetPressedNoSignal(i == Active);
        }
    }

    void LoadControls()
    {
        _updating = true;
        var p = Current;
        _time.SetValueNoSignal(p.TimeScale);
        _zoom.SetValueNoSignal(p.Zoom);
        _speedScale.SetValueNoSignal(p.SpeedScale);
        _loop.SetPressedNoSignal(p.Loop);
        _updating = false;
    }

    void Select(int i)
    {
        Active = i;
        EditorConfig.ActivePreset = i;
        LoadControls();
        RefreshButtons();
        Changed?.Invoke();
    }

    void WriteBack()
    {
        if (_updating) return;
        var p = Current;
        p.TimeScale = _time.Value;
        p.Zoom = _zoom.Value;
        p.SpeedScale = _speedScale.Value;
        p.Loop = _loop.ButtonPressed;
        EditorConfig.SetPreset(Active, p);
        Changed?.Invoke();
    }

    void BeginRename(int i)
    {
        _renaming = i;
        _renameEdit.Text = EditorConfig.GetPreset(i).Name;
        _renameDlg.PopupCentered();
        _renameEdit.GrabFocus();
        _renameEdit.SelectAll();
    }
}
