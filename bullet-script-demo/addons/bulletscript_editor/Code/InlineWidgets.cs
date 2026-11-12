using System;
using System.Collections.Generic;
using BulletScript.Compiler;
using Godot;

namespace BulletScript.EditorTools;

// Draws a slider (and a unit dropdown for durations / angles) next to each literal that
// can be driven by one, overlaid on the code editor. Dragging rewrites the literal in the
// text, so the .bs file is always the single source of truth.
[Tool]
public partial class InlineWidgets : Control
{
    public event Action TextEdited;                  // a widget changed the text
    public event Action<string> RangeEditRequested;  // right-click on a slider: edit its range

    BsCodeEdit _edit;
    readonly List<Row> _rows = new();
    int _active = -1;

    sealed class Row
    {
        public Token Tok;
        public int Length;
        public string Key;
        public NumericUnit Unit;
        public double Base;
        public HSlider Slider;
        public OptionButton UnitBox;
    }

    public void Init(BsCodeEdit edit)
    {
        _edit = edit;
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        SetAnchorsPreset(LayoutPreset.FullRect);
        OffsetLeft = 56;      // keep clear of the line-number / fold gutters
        OffsetRight = -14;    // and the scrollbar
        edit.AddChild(this);
    }

    // Re-lex the text and sync the widgets with it. Rows are reused by index so a slider that
    // is being dragged survives the text rewrite it caused.
    public void Refresh()
    {
        List<Token> toks;
        try { toks = new Lexer(_edit.Text).Tokenize(); }
        catch (LexError)
        {
            foreach (var r in _rows) SetRowVisible(r, false);   // text is mid-edit and invalid
            return;
        }

        var spots = BsTokenScanner.FindLiterals(toks);

        while (_rows.Count > spots.Count)
        {
            DestroyRow(_rows[^1]);
            _rows.RemoveAt(_rows.Count - 1);
        }

        for (int i = 0; i < spots.Count; i++)
        {
            var s = spots[i];
            bool wantUnit = BsTokenScanner.IsDuration(s.Unit) || BsTokenScanner.IsAngle(s.Unit);
            if (i >= _rows.Count)
                _rows.Add(null);

            var row = _rows[i];
            if (row == null || (row.UnitBox != null) != wantUnit)
            {
                if (row != null) DestroyRow(row);
                row = CreateRow(s, wantUnit);
                _rows[i] = row;
            }

            row.Tok = s.Tok;
            row.Key = s.Key;
            row.Unit = s.Unit;
            row.Length = s.Tok.Lexeme.Length;
            row.Slider.TooltipText = $"{s.Key}: drag to change. Right-click to set the slider range.";

            if (i == _active) continue;   // never fight the user's drag

            row.Base = BsTokenScanner.ToBase(BsTokenScanner.ParseNumber(s.Tok.Lexeme, s.Unit), s.Unit);
            ApplyRange(row);
            if (row.UnitBox != null) row.UnitBox.Selected = UnitIndex(row);
        }
    }

    // Re-apply slider ranges (after the user edited one).
    public void ReapplyRanges()
    {
        foreach (var r in _rows) if (r != null) ApplyRange(r);
    }

    void ApplyRange(Row row)
    {
        var range = EditorConfig.GetRange(row.Key);
        row.Slider.Step = range.Step;
        // Widen the slider if the literal is outside the configured range, so it is never clamped by accident.
        row.Slider.MinValue = Math.Min(range.Min, row.Base);
        row.Slider.MaxValue = Math.Max(range.Max, row.Base);
        row.Slider.SetValueNoSignal(row.Base);
    }

    static NumericUnit[] UnitsFor(NumericUnit u) => BsTokenScanner.IsDuration(u)
        ? new[] { NumericUnit.Seconds, NumericUnit.Milliseconds, NumericUnit.Frames }
        : new[] { NumericUnit.Degrees, NumericUnit.Radians };

    static int UnitIndex(Row row) => Math.Max(0, Array.IndexOf(UnitsFor(row.Unit), row.Unit));

    Row CreateRow(LiteralSpot spot, bool withUnitBox)
    {
        var row = new Row { Unit = spot.Unit, Key = spot.Key, Tok = spot.Tok };

        if (withUnitBox)
        {
            var box = new OptionButton { CustomMinimumSize = new Vector2(52, 20) };
            foreach (var u in UnitsFor(spot.Unit)) box.AddItem(BsTokenScanner.Suffix(u));
            box.AddThemeFontSizeOverride("font_size", 11);
            box.AddThemeStyleboxOverride("normal", Ui.Box(Ui.Panel2, Ui.Border, 4, 3));
            box.AddThemeStyleboxOverride("hover", Ui.Box(Ui.Panel2, Ui.Accent, 4, 3));
            box.AddThemeStyleboxOverride("pressed", Ui.Box(Ui.Panel2, Ui.Accent, 4, 3));
            box.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            box.ItemSelected += idx =>
            {
                row.Unit = UnitsFor(row.Unit)[(int)idx];
                WriteValue(row);
            };
            AddChild(box);
            row.UnitBox = box;
        }

        var slider = new HSlider { CustomMinimumSize = new Vector2(64, 14), MouseFilter = MouseFilterEnum.Stop };
        var track = Ui.Box(new Color("3a4254"), null, 2, 0);
        track.ContentMarginTop = 1.5f; track.ContentMarginBottom = 1.5f;
        slider.AddThemeStyleboxOverride("slider", track);
        slider.AddThemeStyleboxOverride("grabber_area", Ui.Box(Ui.Accent, null, 2, 0));
        slider.AddThemeStyleboxOverride("grabber_area_highlight", Ui.Box(Ui.Accent, null, 2, 0));
        var dot = Ui.Dot(Ui.Accent, 12);
        slider.AddThemeIconOverride("grabber", dot);
        slider.AddThemeIconOverride("grabber_highlight", Ui.Dot(Colors.White, 12));
        slider.AddThemeIconOverride("grabber_disabled", dot);

        slider.ValueChanged += v => { row.Base = v; WriteValue(row); };
        slider.DragStarted += () => _active = _rows.IndexOf(row);
        slider.DragEnded += _ => { _active = -1; Refresh(); };
        slider.GuiInput += e =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right)
            {
                RangeEditRequested?.Invoke(row.Key);
                slider.AcceptEvent();
            }
        };
        AddChild(slider);
        row.Slider = slider;
        return row;
    }

    void DestroyRow(Row row)
    {
        if (row == null) return;
        row.Slider?.QueueFree();
        row.UnitBox?.QueueFree();
    }

    // Rewrite the literal in the text from the row's base value and unit.
    void WriteValue(Row row)
    {
        string text = BsTokenScanner.FormatNumber(BsTokenScanner.FromBase(row.Base, row.Unit))
                      + BsTokenScanner.Suffix(row.Unit);
        // ReplaceSpan emits TextChanged, which triggers EditorMain.OnTextChanged -> Refresh().
        _edit.ReplaceSpan(row.Tok.Line, row.Tok.Column, row.Length, text);
        row.Length = text.Length;
        TextEdited?.Invoke();
    }

    static void SetRowVisible(Row r, bool v)
    {
        if (r == null) return;
        if (r.Slider.Visible != v) r.Slider.Visible = v;
        if (r.UnitBox != null && r.UnitBox.Visible != v) r.UnitBox.Visible = v;
    }

    static void SetRectIfChanged(Control c, Vector2 pos, Vector2 size)
    {
        if (!c.Position.IsEqualApprox(pos)) c.Position = pos;
        if (!c.Size.IsEqualApprox(size)) c.Size = size;
    }

    // Widgets are positioned every frame: that keeps them glued to their line through
    // scrolling, folding and resizing without having to chase each of those signals.
    public override void _Process(double delta)
    {
        if (_edit == null || !_edit.IsVisibleInTree()) return;

        int lastLine = -1;
        float x = 0;
        foreach (var r in _rows)
        {
            if (r == null) continue;
            int line = r.Tok.Line - 1;
            if (line < 0 || line >= _edit.GetLineCount()) { SetRowVisible(r, false); continue; }

            var rect = _edit.GetRectAtLineColumn(line, _edit.GetLine(line).Length);
            bool visible = rect.Position.X >= 0 && rect.Position.Y >= 0;
            SetRowVisible(r, visible);

            if (line != lastLine)
            {
                x = rect.Position.X + 20 - OffsetLeft;
                lastLine = line;
            }
            if (!visible) continue;

            float lineH = Mathf.Max(rect.Size.Y, 16);
            float y = rect.Position.Y + (lineH - 20) / 2f;

            if (r.UnitBox != null)
            {
                var unitPos = new Vector2(x, y);
                if (!r.UnitBox.Position.IsEqualApprox(unitPos)) r.UnitBox.Position = unitPos;
                x += r.UnitBox.Size.X + 4;
            }
            SetRectIfChanged(r.Slider, new Vector2(x, y + 3), new Vector2(64, 14));
            x += 64 + 10;
        }
    }
}
