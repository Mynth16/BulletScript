using Godot;

namespace BulletScript.EditorTools;

// Three stat cards: active bullets, frame time, memory.
[Tool]
public partial class PerfMonitor : VBoxContainer
{
    Label _bullets, _bulletsSub, _frame, _frameSub, _mem, _memSub;

    public PerfMonitor()
    {
        AddThemeConstantOverride("separation", 6);

        var header = new HBoxContainer();
        header.AddChild(Ui.MakeLabel("PERFORMANCE MONITOR", 10, Ui.Dim));
        header.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        header.AddChild(Ui.MakeLabel("● LIVE", 10, Ui.Blue));
        AddChild(header);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        AddChild(row);
        row.AddChild(Card("ACTIVE BULLETS", Ui.Blue, out _bullets, out _bulletsSub));
        row.AddChild(Card("FRAME TIME", Colors.White, out _frame, out _frameSub));
        row.AddChild(Card("MEMORY", Ui.Good, out _mem, out _memSub));
    }

    static Control Card(string title, Color valueColor, out Label value, out Label sub)
    {
        var p = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        p.AddThemeStyleboxOverride("panel", Ui.Box(Ui.Panel, Ui.Border, 6, 8));
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 0);
        p.AddChild(v);
        v.AddChild(Ui.MakeLabel("● " + title, 9, Ui.Dim));
        value = Ui.MakeLabel("-", 17, valueColor);
        v.AddChild(value);
        sub = Ui.MakeLabel("", 9, Ui.Dim);
        v.AddChild(sub);
        return p;
    }

    public void SetStats(int bullets, double fps, double tickMs, double memMb, int dropped)
    {
        _bullets.Text = bullets.ToString("N0");
        _bulletsSub.Text = dropped > 0 ? $"{dropped} dropped (pool full)" : "entities";
        _bulletsSub.AddThemeColorOverride("font_color", dropped > 0 ? Ui.Warn : Ui.Dim);

        double ms = fps > 0 ? 1000.0 / fps : 0;
        _frame.Text = $"{ms:0.0}ms";
        _frame.AddThemeColorOverride("font_color", ms > 16.8 ? Ui.Warn : Colors.White);
        _frameSub.Text = $"{fps:0} FPS · tick {tickMs:0.00}ms";

        _mem.Text = $"{memMb:0.0} MB";
        _memSub.Text = "managed heap";
    }
}
