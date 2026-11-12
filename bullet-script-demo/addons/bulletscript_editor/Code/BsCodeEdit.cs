using System;
using Godot;

namespace BulletScript.EditorTools;

// CodeEdit configured for .bs files: highlighting, line numbers, folding, error line marker.
[Tool]
public partial class BsCodeEdit : CodeEdit
{
    public event Action SaveRequested;

    int _errLine = -1;

    public override void _Ready()
    {
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;

        GuttersDrawLineNumbers = true;
        GuttersDrawFoldGutter = true;
        LineFolding = true;
        IndentAutomatic = true;
        IndentUseSpaces = false;
        AutoBraceCompletionEnabled = true;
        AutoBraceCompletionHighlightMatching = true;
        HighlightCurrentLine = true;
        CaretBlink = true;
        DrawTabs = false;
        MinimapDraw = false;
        ScrollSmooth = true;

        var font = new SystemFont
        {
            FontNames = new[] { "JetBrains Mono", "Cascadia Code", "Consolas", "Menlo", "DejaVu Sans Mono", "monospace" },
            Antialiasing = TextServer.FontAntialiasing.Lcd,
        };
        AddThemeFontOverride("font", font);
        AddThemeFontSizeOverride("font_size", 15);
        AddThemeConstantOverride("line_spacing", 6);

        AddThemeColorOverride("background_color", Ui.Bg);
        AddThemeColorOverride("font_color", new Color("c0c8d8"));
        AddThemeColorOverride("current_line_color", new Color("ffffff", 0.04f));
        AddThemeColorOverride("caret_color", Ui.Accent);
        AddThemeColorOverride("line_number_color", new Color("4a5266"));
        AddThemeColorOverride("selection_color", new Color(Ui.Accent, 0.25f));
        AddThemeStyleboxOverride("normal", Ui.Box(Ui.Bg, null, 0, 0));
        AddThemeStyleboxOverride("focus", new StyleBoxEmpty());

        SyntaxHighlighter = BuildHighlighter();

        GuiInput += OnGuiInput;
    }

    static CodeHighlighter BuildHighlighter()
    {
        var h = new CodeHighlighter
        {
            NumberColor = new Color("fb923c"),
            SymbolColor = new Color("8b93a7"),
            FunctionColor = new Color("5eead4"),
            MemberVariableColor = new Color("c0c8d8"),
        };

        var keyword = new Color("f87171");
        foreach (var k in new[]
        {
            "BULLET", "SPAWN_PATTERN", "SPAWN_POINT", "SPAWN", "VAR", "LOOP", "PHASE",
            "IN", "TO", "STEP", "AT", "AFTER", "CALL", "SEED", "TRUE", "FALSE",
        })
            h.AddKeywordColor(k, keyword);

        var prop = new Color("c084fc");
        foreach (var p in new[]
        {
            "speed", "size", "lifetime", "sprite", "color", "x", "y", "count", "spread",
            "angle", "delay", "accel", "speed_limit", "turn",
        })
            h.AddKeywordColor(p, prop);

        h.AddKeywordColor("index", new Color("5eead4"));
        h.AddColorRegion("\"", "\"", new Color("a3e635"));
        h.AddColorRegion("//", "", new Color("5b6478"), true);
        return h;
    }

    void OnGuiInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.S && (k.CtrlPressed || k.MetaPressed))
        {
            SaveRequested?.Invoke();
            AcceptEvent();
        }
    }

    // line1/col1 are the 1-based positions the lexer reports.
    public void ReplaceSpan(int line1, int col1, int oldLength, string newText)
    {
        int line = line1 - 1, col = col1 - 1;
        if (line < 0 || line >= GetLineCount()) return;
        string s = GetLine(line);
        if (col < 0 || col + oldLength > s.Length) return;
        SetLine(line, s.Substring(0, col) + newText + s.Substring(col + oldLength));
        // SetLine may not queue text_changed in every version; the debounce makes a double emit harmless.
        EmitSignal(TextEdit.SignalName.TextChanged);
    }

    public void MarkError(int line0)
    {
        ClearError();
        if (line0 < 0 || line0 >= GetLineCount()) return;
        _errLine = line0;
        SetLineBackgroundColor(line0, new Color(Ui.Bad, 0.16f));
    }

    public void ClearError()
    {
        if (_errLine >= 0 && _errLine < GetLineCount())
            SetLineBackgroundColor(_errLine, new Color(0, 0, 0, 0));
        _errLine = -1;
    }
}
