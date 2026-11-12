using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace BulletScript.EditorTools;

// Persistent editor settings, stored in a ConfigFile next to the plugin so they
// travel with the project: slider ranges (user-definable), presets, sync toggles.
public static class EditorConfig
{
    // user:// (not res://): writing under res:// makes Godot's editor rescan the filesystem, which
    // stalled the editor whenever a setting (e.g. the Live Sync toggle) was saved.
    const string Path = "user://bulletscript_editor_settings.cfg";
    static readonly ConfigFile Cfg = new();
    static bool _loaded;

    public readonly record struct SliderRange(double Min, double Max, double Step);

    // Ranges are in "base units": seconds for durations, degrees for angles.
    static readonly Dictionary<string, SliderRange> Defaults = new()
    {
        ["speed"] = new(0, 10, 0.1),
        ["size"] = new(0, 40, 0.5),
        ["lifetime"] = new(0, 15, 0.1),
        ["x"] = new(-640, 640, 1),
        ["y"] = new(-360, 360, 1),
        ["count"] = new(1, 120, 1),
        ["spread"] = new(0, 360, 1),
        ["angle"] = new(-360, 360, 1),
        ["delay"] = new(0, 5, 0.05),
        ["accel"] = new(-10, 10, 0.1),
        ["speed_limit"] = new(0, 10, 0.1),
        ["turn"] = new(-360, 360, 1),
        ["loop"] = new(0, 60, 1),
        ["after"] = new(0, 3, 0.05),
        ["var"] = new(0, 100, 1),
    };

    static readonly SliderRange Fallback = new(0, 100, 1);

    static void Ensure()
    {
        if (_loaded) return;
        _loaded = true;
        Cfg.Load(Path);   // missing file is fine: defaults apply
    }

    public static void Save() => Cfg.Save(Path);

    // ---- slider ranges ----

    static readonly Dictionary<string, SliderRange> RangeCache = new();

    public static SliderRange GetRange(string key)
    {
        // Called for every slider on every refresh; cache to avoid Variant marshalling churn.
        if (RangeCache.TryGetValue(key, out var cached)) return cached;
        Ensure();
        var result = GetDefaultRange(key);
        if (Cfg.HasSectionKey("ranges", key))
        {
            var parts = Cfg.GetValue("ranges", key).AsString().Split(',');
            if (parts.Length == 3
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var mn)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mx)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var st)
                && st > 0 && mx > mn)
                result = new SliderRange(mn, mx, st);
        }
        RangeCache[key] = result;
        return result;
    }

    public static SliderRange GetDefaultRange(string key)
        => Defaults.TryGetValue(key, out var r) ? r : Fallback;

    public static void SetRange(string key, SliderRange r)
    {
        Ensure();
        RangeCache.Remove(key);
        Cfg.SetValue("ranges", key, string.Create(CultureInfo.InvariantCulture, $"{r.Min},{r.Max},{r.Step}"));
        Save();
    }

    public static void ResetRange(string key)
    {
        Ensure();
        RangeCache.Remove(key);
        if (Cfg.HasSectionKey("ranges", key)) Cfg.EraseSectionKey("ranges", key);
        Save();
    }

    // ---- general ----

    public static string LastFile
    {
        get { Ensure(); return Cfg.GetValue("editor", "last_file", "").AsString(); }
        set { Ensure(); Cfg.SetValue("editor", "last_file", value); Save(); }
    }

    public static bool SyncOnSave
    {
        get { Ensure(); return Cfg.GetValue("sync", "on_save", true).AsBool(); }
        set { Ensure(); Cfg.SetValue("sync", "on_save", value); Save(); }
    }

    public static bool SyncOnEdit
    {
        get { Ensure(); return Cfg.GetValue("sync", "on_edit", false).AsBool(); }
        set { Ensure(); Cfg.SetValue("sync", "on_edit", value); Save(); }
    }

    // ---- simulation presets ----

    public const int PresetCount = 3;

    public static int ActivePreset
    {
        get { Ensure(); return Mathf.Clamp(Cfg.GetValue("presets", "active", 0).AsInt32(), 0, PresetCount - 1); }
        set { Ensure(); Cfg.SetValue("presets", "active", value); Save(); }
    }

    public sealed class Preset
    {
        public string Name = "";
        public double SpeedScale = 60;   // px per second per speed unit (matches the runner)
        public double TimeScale = 1;     // playback speed
        public double Zoom = 0.35;       // preview zoom
        public bool Loop = true;
    }

    static readonly (string name, double time)[] PresetDefaults =
    {
        ("Normal", 1.0), ("Slow-mo", 0.4), ("Fast", 2.0),
    };

    public static Preset GetPreset(int i)
    {
        Ensure();
        string s = $"preset{i}";
        var d = PresetDefaults[i];
        return new Preset
        {
            Name = Cfg.GetValue(s, "name", d.name).AsString(),
            SpeedScale = Cfg.GetValue(s, "speed_scale", 60.0).AsDouble(),
            TimeScale = Cfg.GetValue(s, "time_scale", d.time).AsDouble(),
            Zoom = Cfg.GetValue(s, "zoom", 0.35).AsDouble(),
            Loop = Cfg.GetValue(s, "loop", true).AsBool(),
        };
    }

    public static void SetPreset(int i, Preset p)
    {
        Ensure();
        string s = $"preset{i}";
        Cfg.SetValue(s, "name", p.Name);
        Cfg.SetValue(s, "speed_scale", p.SpeedScale);
        Cfg.SetValue(s, "time_scale", p.TimeScale);
        Cfg.SetValue(s, "zoom", p.Zoom);
        Cfg.SetValue(s, "loop", p.Loop);
        Save();
    }
}
