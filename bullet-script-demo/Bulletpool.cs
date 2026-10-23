using Godot;

namespace BulletScript;

// One pool per bullet definition. Plain C# class (not a Godot script) that owns
// a MultiMeshInstance2D, so there is no node per bullet and one draw call per type.
public sealed class BulletPool
{
    public readonly MultiMeshInstance2D Node;

    readonly MultiMesh _mm;
    readonly float[] _x, _y, _vx, _vy, _age;
    readonly Color[] _color;
    readonly int _capacity;
    readonly float _speed, _lifetime, _diameter;
    int _count;

    public int Count => _count;
    public int Dropped { get; private set; }   // spawns refused because the pool was full

    public BulletPool(int capacity, Texture2D texture, float radiusPx, float speedPxPerSec, float lifetimeSec)
    {
        _capacity = capacity;
        _speed = speedPxPerSec;
        _lifetime = lifetimeSec;
        _diameter = radiusPx * 2f;

        _x = new float[capacity]; _y = new float[capacity];
        _vx = new float[capacity]; _vy = new float[capacity];
        _age = new float[capacity];
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
    // spawned between physics ticks still land exactly where they should be.
    public void Spawn(float x, float y, float angleRad, float lead, Color color)
    {
        if (_count >= _capacity) { Dropped++; return; }

        int i = _count++;
        _vx[i] = Mathf.Cos(angleRad) * _speed;
        _vy[i] = Mathf.Sin(angleRad) * _speed;
        _x[i] = x + _vx[i] * lead;
        _y[i] = y + _vy[i] * lead;
        _age[i] = lead;
        _color[i] = color;
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
                _vx[i] = _vx[last]; _vy[i] = _vy[last];
                _age[i] = _age[last];
                _color[i] = _color[last];
                continue;   // re-check the bullet that was swapped into slot i
            }
            _x[i] += _vx[i] * dt;
            _y[i] += _vy[i] * dt;
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
}