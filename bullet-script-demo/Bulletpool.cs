using Godot;

namespace BulletScript;

// One pool per bullet definition. Plain C# class (not a Godot script) that owns
// a MultiMeshInstance2D, so there is no node per bullet and one draw call per type.
//
// Each bullet carries its own heading, speed, acceleration and turn rate, so
// accelerating and curving bullets need no extra nodes or per-frame allocations.
public sealed class BulletPool
{
    public readonly MultiMeshInstance2D Node;

    readonly MultiMesh _mm;
    readonly float[] _x, _y, _age;
    readonly float[] _angle;     // heading, radians
    readonly float[] _speed;     // speed units (multiplied by _speedScale to get px/s)
    readonly float[] _accel;     // speed units per second
    readonly float[] _limit;     // speed that _accel stops at (only valid when _hasLimit)
    readonly bool[] _hasLimit;
    readonly float[] _turn;      // radians per second
    readonly Color[] _color;
    readonly int _capacity;
    readonly float _speedScale, _lifetime, _diameter;
    int _count;

    public int Count => _count;
    public int Dropped { get; private set; }   // spawns refused because the pool was full

    public BulletPool(int capacity, Texture2D texture, float radiusPx, float speedScale, float lifetimeSec)
    {
        _capacity = capacity;
        _speedScale = speedScale;
        _lifetime = lifetimeSec;
        _diameter = radiusPx * 2f;

        _x = new float[capacity]; _y = new float[capacity];
        _age = new float[capacity];
        _angle = new float[capacity];
        _speed = new float[capacity];
        _accel = new float[capacity];
        _limit = new float[capacity];
        _hasLimit = new bool[capacity];
        _turn = new float[capacity];
        _color = new Color[capacity];

        _mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D, // must be set before InstanceCount
            UseColors = true,                                            // per-bullet tint; also before InstanceCount
            Mesh = new QuadMesh { Size = Vector2.One },                  // 1x1, scaled per bullet in Flush()
            InstanceCount = capacity,
            VisibleInstanceCount = 0,
        };
        Node = new MultiMeshInstance2D { Multimesh = _mm, Texture = texture };
    }

    // lead = how many seconds late this spawn is (clock - scheduled time), so bullets
    // spawned between physics ticks still land exactly where they should be. The lead
    // is simulated with the same Advance() as normal ticks, so accelerating and
    // curving bullets end up in the right place too.
    public void Spawn(float x, float y, float angleRad, float lead, Color color,
                      float speed, float accel, float limit, bool hasLimit, float turnRadPerSec)
    {
        if (_count >= _capacity) { Dropped++; return; }

        int i = _count++;
        _x[i] = x;
        _y[i] = y;
        _angle[i] = angleRad;
        _speed[i] = speed;
        _accel[i] = accel;
        _limit[i] = limit;
        _hasLimit[i] = hasLimit;
        _turn[i] = turnRadPerSec;
        _color[i] = color;
        _age[i] = lead;

        if (lead > 0f) Advance(i, lead);
    }

    // Moves one bullet forward by dt seconds.
    void Advance(int i, float dt)
    {
        float a = _accel[i];
        if (a != 0f)
        {
            float old = _speed[i];
            float s = old + a * dt;
            if (_hasLimit[i])
            {
                // Accelerate up to the limit (or slow down to it) and stop there. A bullet
                // that is already past the limit keeps its speed instead of snapping to it.
                if (a > 0f && s > _limit[i]) s = Mathf.Max(old, _limit[i]);
                else if (a < 0f && s < _limit[i]) s = Mathf.Min(old, _limit[i]);
            }
            else if (s < 0f)
            {
                s = 0f;   // with no limit, a slowing bullet stops instead of reversing
            }
            _speed[i] = s;
        }

        _angle[i] += _turn[i] * dt;

        float v = _speed[i] * _speedScale;
        _x[i] += Mathf.Cos(_angle[i]) * v * dt;
        _y[i] += Mathf.Sin(_angle[i]) * v * dt;
    }

    // Simulation only: move, age, and despawn expired bullets (swap-remove).
    public void Step(float dt)
    {
        for (int i = 0; i < _count;)
        {
            _age[i] += dt;
            if (_age[i] >= _lifetime)
            {
                int last = --_count;
                _x[i] = _x[last]; _y[i] = _y[last];
                _age[i] = _age[last];
                _angle[i] = _angle[last];
                _speed[i] = _speed[last];
                _accel[i] = _accel[last];
                _limit[i] = _limit[last];
                _hasLimit[i] = _hasLimit[last];
                _turn[i] = _turn[last];
                _color[i] = _color[last];
                continue;   // re-check the bullet that was swapped into slot i
            }
            Advance(i, dt);
            i++;
        }
    }

    // Push the current state to the renderer. Call after Step() and after spawning.
    public void Flush()
    {
        float d = _diameter;
        for (int i = 0; i < _count; i++)
        {
            _mm.SetInstanceTransform2D(i, new Transform2D(
                new Vector2(d, 0), new Vector2(0, d), new Vector2(_x[i], _y[i])));
            _mm.SetInstanceColor(i, _color[i]);
        }
        _mm.VisibleInstanceCount = _count;
    }

    public void Clear()
    {
        _count = 0;
        _mm.VisibleInstanceCount = 0;
    }

    // Frees the renderer-side MultiMesh buffers right away. Without this they live until the
    // C# GC happens to finalize the wrapper, which Godot's native memory pressure does not
    // trigger, so rebuilding pools on every recompile leaked until the editor stalled.
    public void Dispose()
    {
        if (Node == null || !GodotObject.IsInstanceValid(Node)) return;
        Node.Multimesh = null;
        if (Node.GetParent() is Node parent) parent.RemoveChild(Node);
        Node.QueueFree();
        _mm.Mesh = null;
        _mm.Dispose();
    }
}