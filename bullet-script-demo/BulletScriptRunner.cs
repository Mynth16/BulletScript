using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using BulletScript.Compiler;

namespace BulletScript;

// Attach to a Node2D. Compiles the .bs source in memory (no JSON, nothing written to
// disk), resolves every instruction into a flat time-sorted struct array at load
// time, then each physics tick just advances a clock and fires what is due.
public partial class BulletScriptRunner : Node2D
{
    [Export(PropertyHint.File, "*.bs")] public string SourcePath = "res://ring.bs";
    [Export] public string PatternName = "basic_ring";
    [Export] public bool AutoStart = true;
    [Export] public bool CenterOnScreen = true;   // spawn point (0,0) = middle of the viewport
    [Export] public int PoolCapacity = 5000;
    // Live Sync from the BulletScript editor plugin: when the editor touches the sync file for
    // this SourcePath, recompile and restart. Ignored in exported builds.
    [Export] public bool HotReload = true;

    // The language has no pixel units. These are the decisions made here; pin them
    // in the BulletScript spec so the JSON and hand-coded C# treatments match exactly.
    [Export] public float SpeedScale = 60f;   // speed 3.5 => 3.5 px per 60 Hz tick = 210 px/s
                                              // accel is in speed units per second: accel 1 => +60 px/s every second
    [Export] public float SizeScale = 1f;     // size 4 => 4 px radius

    struct Ev
    {
        public float T;        // seconds since pattern start
        public int Pool;
        public float X, Y;
        public float Angle;    // radians, Godot convention: 0 = right, +90 deg = down
        public Color Tint;     // resolved per bullet at compile time
        public float Speed;    // speed units; each bullet's own (SPAWN speed or BULLET speed)
        public float Accel;    // speed units per second
        public float Limit;
        public bool HasLimit;
        public float Turn;     // radians per second
    }

    Ev[] _events = Array.Empty<Ev>();
    BulletPool[] _pools = Array.Empty<BulletPool>();
    Texture2D _circle;
    int _pc;
    double _clock;
    bool _running;
    const string SyncFile = "res://.bulletscript_sync";
    ulong _lastSync;
    double _syncPoll;

    public override void _Ready()
    {
        if (CenterOnScreen) Position = GetViewportRect().Size / 2f;
        _lastSync = Godot.FileAccess.GetModifiedTime(SyncFile);
        if (Load() && AutoStart) Start();
    }

    // Polls the sync token written by the editor plugin (4x per second, negligible cost).
    bool SyncRequested(double delta)
    {
        if (!HotReload || !OS.IsDebugBuild()) return false;
        _syncPoll += delta;
        if (_syncPoll < 0.25) return false;
        _syncPoll = 0;

        ulong t = Godot.FileAccess.GetModifiedTime(SyncFile);
        if (t == _lastSync) return false;
        _lastSync = t;

        string first = (Godot.FileAccess.GetFileAsString(SyncFile).Split('\n')[0] ?? "").Trim();
        return first == SourcePath;
    }

    bool Load()
    {
        string source = Godot.FileAccess.GetFileAsString(SourcePath);
        if (string.IsNullOrEmpty(source))
        {
            GD.PushError($"BulletScript: could not read '{SourcePath}'");
            return false;
        }

        CompiledProgram prog;
        ulong t0 = Time.GetTicksMsec();
        try
        {
            // "frames" durations in .bs are converted using the physics tick rate.
            prog = BulletScriptCompiler.CompileSource(source, Engine.PhysicsTicksPerSecond);
        }
        catch (LexError e) { ReportError("lex error", e.Line, e.Column, e.Message); return false; }
        catch (ParseError e) { ReportError("parse error", e.Line, e.Column, e.Message); return false; }
        catch (CompileError e) { ReportError("compile error", e.Line, e.Column, e.Message); return false; }
        GD.Print($"BulletScript: compiled '{SourcePath}' in {Time.GetTicksMsec() - t0} ms");

        if (!prog.Patterns.TryGetValue(PatternName, out var list))
        {
            GD.PushError($"BulletScript: pattern '{PatternName}' not found. Have: {string.Join(", ", prog.Patterns.Keys)}");
            return false;
        }

        // Only now (compile succeeded) throw away the previous run's pools,
        // so a compile error leaves the last good state on screen.
        foreach (var old in _pools) old.Node.QueueFree();

        // One pool per bullet definition.
        var poolIndex = new Dictionary<int, int>();
        var pools = new List<BulletPool>();
        foreach (var b in prog.Bullets)
        {
            if (b.LifetimeMs <= 0)
                GD.PushWarning($"BulletScript: bullet '{b.Name}' has no lifetime, so it is removed right after spawning. Add `lifetime: 6s;` to its BULLET block.");

            var pool = new BulletPool(PoolCapacity, LoadSprite(b.Sprite),
                (float)b.Size * SizeScale, SpeedScale, (float)b.LifetimeMs / 1000f);
            AddChild(pool.Node);
            poolIndex[b.Id] = pools.Count;
            pools.Add(pool);
        }
        _pools = pools.ToArray();

        var points = prog.SpawnPoints.ToDictionary(p => p.Id);

        // Resolve ids -> concrete numbers now, so the tick loop does no lookups.
        var events = new List<Ev>(list.Count);
        foreach (var ins in list)
        {
            if (!poolIndex.TryGetValue(ins.BulletId, out int pi) || !points.TryGetValue(ins.SpawnPointId, out var sp))
            {
                GD.PushWarning($"BulletScript: skipping instruction with unknown bullet {ins.BulletId} / spawn point {ins.SpawnPointId}");
                continue;
            }
            events.Add(new Ev
            {
                T = (float)(ins.DelayMs / 1000.0),   // DelayMs is absolute from pattern start
                Pool = pi,
                X = (float)sp.X,
                Y = (float)sp.Y,
                Angle = Mathf.DegToRad((float)ins.AngleDegrees),
                Tint = new Color(
                    ((ins.Rgb >> 16) & 0xFF) / 255f,
                    ((ins.Rgb >> 8) & 0xFF) / 255f,
                    (ins.Rgb & 0xFF) / 255f),
                Speed = (float)ins.Speed,
                Accel = (float)ins.Accel,
                Limit = (float)(ins.SpeedLimit ?? 0.0),
                HasLimit = ins.SpeedLimit.HasValue,
                Turn = Mathf.DegToRad((float)ins.TurnDegreesPerSec),
            });
        }
        _events = events.OrderBy(ev => ev.T).ToArray();   // stable sort

        GD.Print($"BulletScript: loaded '{PatternName}' with {_events.Length} spawn events across {_pools.Length} bullet type(s)");
        return true;
    }

    void ReportError(string kind, int line, int column, string message)
        => GD.PushError($"BulletScript: {SourcePath}:{line}:{column} {kind}: {message}");

    // Uses res://sprites/<name>.png if it exists, otherwise a generated white circle.
    Texture2D LoadSprite(string name)
    {
        string path = $"res://sprites/{name}.png";
        if (!string.IsNullOrEmpty(name) && ResourceLoader.Exists(path))
            return GD.Load<Texture2D>(path);

        return _circle ??= new GradientTexture2D
        {
            Width = 64,
            Height = 64,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(1f, 0.5f),
            Gradient = new Gradient
            {
                Offsets = new[] { 0f, 0.9f, 1f },
                Colors = new[] { Colors.White, Colors.White, new Color(1, 1, 1, 0) },
            },
        };
    }

    public void Start()
    {
        foreach (var p in _pools) p.Clear();
        _pc = 0;
        _clock = 0;
        _running = true;
    }

    public override void _PhysicsProcess(double delta)
    {
        // Space/Enter = recompile the .bs and restart, so you can edit, alt-tab, and re-run
        // without stopping the game. On a compile error the last good run keeps going.
        if (Input.IsActionJustPressed("ui_accept") || SyncRequested(delta))
        {
            if (Load()) Start();
            return;
        }
        if (!_running) return;

        float dt = (float)delta;
        _clock += delta;

        // 1) advance bullets that already exist (up to the previous tick)
        foreach (var p in _pools) p.Step(dt);

        // 2) fire everything that is due, compensating for sub-tick lateness
        while (_pc < _events.Length && _events[_pc].T <= _clock)
        {
            ref Ev e = ref _events[_pc++];
            _pools[e.Pool].Spawn(e.X, e.Y, e.Angle, (float)(_clock - e.T), e.Tint,
                                 e.Speed, e.Accel, e.Limit, e.HasLimit, e.Turn);
        }

        // 3) render
        foreach (var p in _pools) p.Flush();
    }
}