using Godot;

namespace BulletScript.EditorTools;

// Shared palette and tiny UI helpers (dark theme with a cyan accent).
public static class Ui
{
    public static readonly Color Bg = new("0d1017");
    public static readonly Color Panel = new("141821");
    public static readonly Color Panel2 = new("1a1f2b");
    public static readonly Color Border = new("262c3a");
    public static readonly Color Text = new("c9d1e0");
    public static readonly Color Dim = new("6b7488");
    public static readonly Color Accent = new("22d3ee");
    public static readonly Color Good = new("4ade80");
    public static readonly Color Warn = new("fbbf24");
    public static readonly Color Bad = new("f87171");
    public static readonly Color Blue = new("60a5fa");

    public static StyleBoxFlat Box(Color bg, Color? border = null, int radius = 6, int margin = 8)
    {
        var sb = new StyleBoxFlat { BgColor = bg };
        sb.SetCornerRadiusAll(radius);
        if (border.HasValue)
        {
            sb.BorderColor = border.Value;
            sb.SetBorderWidthAll(1);
        }
        sb.SetContentMarginAll(margin);
        return sb;
    }

    public static Label MakeLabel(string text, int size = 12, Color? color = null)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color ?? Text);
        return l;
    }

    public static void StyleButton(Button b, bool accent = false)
    {
        var normal = Box(accent ? new Color(Accent, 0.12f) : Panel2, accent ? Accent : Border, 6, 8);
        var hover = Box(accent ? new Color(Accent, 0.22f) : new Color("232a38"), accent ? Accent : Dim, 6, 8);
        var pressed = Box(new Color(Accent, 0.2f), Accent, 6, 8);
        b.AddThemeStyleboxOverride("normal", normal);
        b.AddThemeStyleboxOverride("hover", hover);
        b.AddThemeStyleboxOverride("pressed", pressed);
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeColorOverride("font_color", accent ? Accent : Text);
        b.AddThemeColorOverride("font_hover_color", accent ? Accent : Colors.White);
        b.AddThemeColorOverride("font_pressed_color", Accent);
        b.AddThemeFontSizeOverride("font_size", 12);
    }

    // Small filled circle, used as the slider grabber.
    public static Texture2D Dot(Color c, int px)
    {
        return new GradientTexture2D
        {
            Width = px,
            Height = px,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(1f, 0.5f),
            Gradient = new Gradient
            {
                Offsets = new[] { 0f, 0.85f, 1f },
                Colors = new[] { c, c, new Color(c, 0f) },
            },
        };
    }
}
