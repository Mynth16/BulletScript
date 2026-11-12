using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BulletScript.Compiler;
using Godot;

namespace BulletScript.EditorTools;

// Plays one compiled pattern inside the editor. Same spawning model as BulletScriptRunner
// (flat time-sorted events + one BulletPool per bullet type), so the preview shows exactly
// what the game will do.
[Tool]
public partial class PreviewPlayer : Node2D
{
    public float SpeedScale = 60f;
    public float TimeScale = 1f;
    public float Zoom = 0.35f;
    public bool Loop = true;
    public int PoolCapacity = 4000;

    public int ActiveBullets { get; private set; }
    public int Dropped { get; private set; }
    public double TickMs { get; private set; }

    struct Ev
    {
        public float T;
        public int Pool;
        public float X, Y, Angle;
        public Color Tint;
        public float Speed, Accel, Limit;
        public bool HasLimit;
        public float Turn;
    }

    Ev[] _events = Array.Empty<Ev>();
    BulletPool[] _pools = Array.Empty<BulletPool>();
    Texture2D _circle;
    int _pc;
    double _clock;
    double _idle;
    bool _running;

    // Replaces the current run. Returns false (and leaves the old run untouched) on bad input.
    public bool Load(CompiledProgram prog, string patternKey, out string error)
    {
        error = null;
        if (!prog.Patterns.TryGetValue(patternKey, out var list))
        {
            error = $"pattern '{patternKey}' not found";
            return false;
        }

        foreach (var old in _pools) { RemoveChild(old.Node); old.Node.QueueFree(); }

        var poolIndex = new Dictionary<int, int>();
        var pools = new List<BulletPool>();
        foreach (var b in prog.Bullets)
        {
            var pool = new BulletPool(PoolCapacity, LoadSprite(b.Sprite),
                (float)b.Size, SpeedScale, (float)b.LifetimeMs / 1000f);
            AddChild(pool.Node);
            poolIndex[b.Id] = pools.Count;
            pools.Add(pool);
        }
        _pools = pools.ToArray();

        var points = prog.SpawnPoints.ToDictionary(p => p.Id);
        var events = new List<Ev>(list.Count);
        foreach (var ins in list)
        {
            if (!poolIndex.TryGetValue(ins.BulletId, out int pi) || !points.TryGetValue(ins.SpawnPointId, out var sp))
                continue;
            events.Add(new Ev
            {
                T = (float)(ins.DelayMs / 1000.0),
                Pool = pi,
                X = (float)sp.X,
                Y = (float)sp.Y,
                Angle = Mathf.DegToRad((float)ins.AngleDegrees),
                Tint = new Color(((ins.Rgb >> 16) & 0xFF) / 255f, ((ins.Rgb >> 8) & 0xFF) / 255f, (ins.Rgb & 0xFF) / 255f),
                Speed = (float)ins.Speed,
                Accel = (float)ins.Accel,
                Limit = (float)(ins.SpeedLimit ?? 0.0),
                HasLimit = ins.SpeedLimit.HasValue,
                Turn = Mathf.DegToRad((float)ins.TurnDegreesPerSec),
            });
        }
        _events = events.OrderBy(e => e.T).ToArray();
        Restart();
        return true;
    }

    public void Restart()
    {
        foreach (var p in _pools) p.Clear();
        _pc = 0;
        _clock = 0;
        _idle = 0;
        _running = true;
    }

    // Uses res://sprites/<name>.png when it exists, otherwise a generated white circle.
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

    public override void _Process(double delta)
    {
        // Centre the origin of the pattern in the preview viewport.
        Position = GetViewport().GetVisibleRect().Size / 2f;
        Scale = Vector2.One * Zoom;

        if (!_running) return;

        var sw = Stopwatch.StartNew();
        float dt = (float)Math.Min(delta, 0.1) * TimeScale;   // clamp: editor hitches must not teleport bullets
        _clock += dt;

        foreach (var p in _pools) p.Step(dt);

        while (_pc < _events.Length && _events[_pc].T <= _clock)
        {
            ref Ev e = ref _events[_pc++];
            _pools[e.Pool].Spawn(e.X, e.Y, e.Angle, (float)(_clock - e.T), e.Tint,
                                 e.Speed, e.Accel, e.Limit, e.HasLimit, e.Turn);
        }

        int active = 0, dropped = 0;
        foreach (var p in _pools) { p.Flush(); active += p.Count; dropped += p.Dropped; }
        ActiveBullets = active;
        Dropped = dropped;

        sw.Stop();
        TickMs = TickMs * 0.9 + sw.Elapsed.TotalMilliseconds * 0.1;

        // Pattern finished and the screen is clear: wait a beat, then replay.
        if (Loop && _pc >= _events.Length && active == 0)
        {
            _idle += delta;
            if (_idle > 0.6) Restart();
        }
    }
}
