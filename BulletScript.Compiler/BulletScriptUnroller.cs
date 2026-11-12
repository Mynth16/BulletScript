using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using BulletScript.Compiler.Ast;

namespace BulletScript.Compiler
{
    // Color values carry their packed 0xRRGGBB in EvalResult.Value.
    public enum UnitCategory { None, Angle, Duration, Color }

    public readonly struct EvalResult
    {
        public double Value { get; }
        public UnitCategory Category { get; }

        public EvalResult(double value, UnitCategory category)
        {
            Value = value;
            Category = category;
        }
    }

    public class CompileError : Exception
    {
        public int Line { get; }
        public int Column { get; }

        public CompileError(string message, int line, int column)
            : base($"Compile error at {line}:{column}: {message}")
        {
            Line = line;
            Column = column;
        }
    }

    // ---------- Compiled output ----------

    public class CompiledBullet
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public double Speed { get; init; }
        public double Size { get; init; }
        public string? Sprite { get; init; }
        public double LifetimeMs { get; init; }

        // Default tint (packed 0xRRGGBB); white when the BULLET has no color.
        [JsonIgnore] public uint Rgb { get; init; } = 0xFFFFFF;
        public string Color => $"#{Rgb:X6}";
    }

    public class CompiledSpawnPoint
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public double X { get; init; }
        public double Y { get; init; }
    }

    /// <summary>
    /// One flattened spawn instruction. This is the only thing the C#
    /// runtime executor ever reads — it just walks the array in order and
    /// fires each spawn at DelayMs, with no interpretation of BulletScript
    /// source at runtime. Every per-bullet value (color, speed, motion) is
    /// already resolved here.
    /// </summary>
    public readonly struct Instruction
    {
        public int BulletId { get; }
        public int SpawnPointId { get; }
        public double AngleDegrees { get; }
        public double DelayMs { get; }

        // Final tint for this one bullet (packed 0xRRGGBB), resolved at compile
        // time: the SPAWN's color for this copy, else the BULLET's, else white.
        [JsonIgnore] public uint Rgb { get; }
        public string Color => $"#{Rgb:X6}";

        // Starting speed in speed units (the SPAWN's speed, else the BULLET's).
        public double Speed { get; }
        // Speed change per second, in speed units. 0 = constant speed.
        public double Accel { get; }
        // Speed that Accel stops at; null = no limit (deceleration still stops at 0).
        public double? SpeedLimit { get; }
        // Heading change per second, in degrees. 0 = straight line.
        public double TurnDegreesPerSec { get; }

        public Instruction(
            int bulletId, int spawnPointId, double angleDegrees, double delayMs,
            uint rgb, double speed, double accel, double? speedLimit, double turnDegreesPerSec)
        {
            BulletId = bulletId;
            SpawnPointId = spawnPointId;
            AngleDegrees = angleDegrees;
            DelayMs = delayMs;
            Rgb = rgb;
            Speed = speed;
            Accel = accel;
            SpeedLimit = speedLimit;
            TurnDegreesPerSec = turnDegreesPerSec;
        }

        public override string ToString() =>
            $"Instruction(bullet={BulletId}, point={SpawnPointId}, angle={AngleDegrees:F2}deg, delay={DelayMs:F1}ms, " +
            $"color={Color}, speed={Speed}, accel={Accel}, limit={(SpeedLimit?.ToString() ?? "none")}, turn={TurnDegreesPerSec}deg/s)";
    }

    public class CompiledProgram
    {
        public List<CompiledBullet> Bullets { get; } = new();
        public List<CompiledSpawnPoint> SpawnPoints { get; } = new();

        // Pattern name -> its flat, time-ordered instruction list. Only patterns
        // without parameters appear here; parameterized ones are templates for CALL.
        public Dictionary<string, List<Instruction>> Patterns { get; } = new();
    }

    /// <summary>
    /// SplitMix64. Implemented here (rather than System.Random) so a seed gives
    /// the same numbers on every machine and runtime version.
    /// </summary>
    public sealed class SeededRandom
    {
        private ulong _state;

        public SeededRandom(long seed)
        {
            _state = unchecked((ulong)seed);
        }

        private ulong NextUInt64()
        {
            unchecked
            {
                _state += 0x9E3779B97F4A7C15UL;
                ulong z = _state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>Uniform in [0, 1).</summary>
        public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
    }

    /// <summary>
    /// Evaluates BulletScript const_exprs down to a single (value, unit
    /// category) pair. Units are normalized immediately on evaluation:
    /// angles always come out in degrees, durations always in
    /// milliseconds — so arithmetic never has to re-convert mid-expression.
    /// </summary>
    public class ConstEvaluator
    {
        private readonly Dictionary<string, EvalResult> _globalVars;
        // LOOP variables, pattern parameters and the per-copy `index`.
        private readonly Dictionary<string, EvalResult> _bindings;
        private readonly double _framesPerSecond;
        private readonly SeededRandom _rng;

        public ConstEvaluator(
            Dictionary<string, EvalResult> globalVars,
            Dictionary<string, EvalResult> bindings,
            double framesPerSecond,
            SeededRandom rng)
        {
            _globalVars = globalVars;
            _bindings = bindings;
            _framesPerSecond = framesPerSecond;
            _rng = rng;
        }

        public EvalResult Evaluate(ConstExpr expr) => expr switch
        {
            NumberLiteral n => EvalNumberLiteral(n),
            VarRef v => EvalVarRef(v),
            BinaryOpExpr b => EvalBinary(b),
            UnaryMinusExpr u => Negate(Evaluate(u.Operand), u),
            ColorLiteral c => new EvalResult(c.Rgb, UnitCategory.Color),
            IndexExpr ix => EvalIndex(ix),
            RandExpr r => EvalRand(r),
            _ => throw new CompileError($"Unhandled expression node {expr.GetType().Name}", expr.Line, expr.Column)
        };

        private EvalResult EvalNumberLiteral(NumberLiteral n) => n.Unit switch
        {
            NumericUnit.None => new EvalResult(n.Value, UnitCategory.None),
            NumericUnit.Degrees => new EvalResult(n.Value, UnitCategory.Angle),
            NumericUnit.Radians => new EvalResult(n.Value * (180.0 / Math.PI), UnitCategory.Angle),
            NumericUnit.Seconds => new EvalResult(n.Value * 1000.0, UnitCategory.Duration),
            NumericUnit.Milliseconds => new EvalResult(n.Value, UnitCategory.Duration),
            NumericUnit.Frames => new EvalResult(n.Value / _framesPerSecond * 1000.0, UnitCategory.Duration),
            _ => throw new CompileError($"Unhandled numeric unit {n.Unit}", n.Line, n.Column)
        };

        private EvalResult EvalVarRef(VarRef v)
        {
            // Bindings (LOOP variables, pattern parameters, `index`) never share a
            // name with a VAR — the parser forbids it — so checking them first is
            // safe and avoids a dictionary miss on the common case.
            if (_bindings.TryGetValue(v.Name, out var bound))
                return bound;

            if (_globalVars.TryGetValue(v.Name, out var globalValue))
                return globalValue;

            // The parser should have already rejected this, but a defensive
            // check here means a future caller of ConstEvaluator directly
            // (bypassing the parser) still fails loudly instead of crashing
            // with a raw KeyNotFoundException.
            throw new CompileError($"Unresolved reference to '{v.Name}' during evaluation", v.Line, v.Column);
        }

        private EvalResult Negate(EvalResult r, UnaryMinusExpr node)
        {
            if (r.Category == UnitCategory.Color)
                throw new CompileError("Cannot negate a color value", node.Line, node.Column);
            return new EvalResult(-r.Value, r.Category);
        }

        // [a, b, c][n] -- picks one item at compile time; only the chosen item is evaluated.
        private EvalResult EvalIndex(IndexExpr ix) =>
            Evaluate(ix.List.Items[EvaluateListIndex(ix.Index, ix.List.Items.Count, ix.Line, ix.Column)]);

        // Evaluates a list index and checks it is a whole number inside the list.
        // Shared by value lists ([#F00, #FFF][n]) and bullet-type lists (SPAWN [a, b][n]).
        public int EvaluateListIndex(ConstExpr indexExpr, int itemCount, int line, int column)
        {
            var idx = Evaluate(indexExpr);
            if (idx.Category != UnitCategory.None)
                throw new CompileError(
                    $"List index must be a plain number, got a {idx.Category.ToString().ToLowerInvariant()} value",
                    indexExpr.Line, indexExpr.Column);

            int i = (int)Math.Round(idx.Value);
            if (Math.Abs(idx.Value - i) > 1e-6)
                throw new CompileError($"List index must be a whole number (got {idx.Value})", indexExpr.Line, indexExpr.Column);
            if (i < 0 || i >= itemCount)
                throw new CompileError(
                    $"List index {i} is out of range (list has {itemCount} item(s))",
                    line, column);

            return i;
        }

        // rand(lo, hi) / randi(lo, hi). The result takes the unit of whichever bound
        // has one, so rand(0deg, 360deg) is an angle and rand(1, 3) a plain number.
        private EvalResult EvalRand(RandExpr r)
        {
            string name = r.Integer ? "randi" : "rand";
            var lo = Evaluate(r.Lo);
            var hi = Evaluate(r.Hi);

            if (lo.Category == UnitCategory.Color || hi.Category == UnitCategory.Color)
                throw new CompileError($"{name}() cannot take colors", r.Line, r.Column);

            UnitCategory category = lo.Category != UnitCategory.None ? lo.Category : hi.Category;
            if (lo.Category != UnitCategory.None && hi.Category != UnitCategory.None && lo.Category != hi.Category)
                throw new CompileError(
                    $"{name}() bounds must have the same unit (got {lo.Category.ToString().ToLowerInvariant()} and {hi.Category.ToString().ToLowerInvariant()})",
                    r.Line, r.Column);

            if (hi.Value < lo.Value)
                throw new CompileError($"{name}(lo, hi) needs lo <= hi (got {lo.Value} and {hi.Value})", r.Line, r.Column);

            if (!r.Integer)
                return new EvalResult(lo.Value + _rng.NextDouble() * (hi.Value - lo.Value), category);

            long a = (long)Math.Round(lo.Value);
            long b = (long)Math.Round(hi.Value);
            if (Math.Abs(lo.Value - a) > 1e-6 || Math.Abs(hi.Value - b) > 1e-6)
                throw new CompileError($"randi() bounds must be whole numbers (got {lo.Value} and {hi.Value})", r.Line, r.Column);

            long span = b - a + 1;
            long picked = a + (long)Math.Floor(_rng.NextDouble() * span);
            if (picked > b) picked = b;
            return new EvalResult(picked, category);
        }

        private EvalResult EvalBinary(BinaryOpExpr b)
        {
            var left = Evaluate(b.Left);
            var right = Evaluate(b.Right);

            if (left.Category == UnitCategory.Color || right.Category == UnitCategory.Color)
                throw new CompileError("Colors cannot be used in arithmetic", b.Line, b.Column);

            UnitCategory resultCategory;
            if (left.Category == UnitCategory.None && right.Category == UnitCategory.None)
            {
                resultCategory = UnitCategory.None;
            }
            else if (left.Category == UnitCategory.None)
            {
                resultCategory = right.Category;
            }
            else if (right.Category == UnitCategory.None)
            {
                resultCategory = left.Category;
            }
            else if (left.Category == right.Category)
            {
                resultCategory = left.Category;
            }
            else
            {
                throw new CompileError(
                    $"Cannot combine a {left.Category} value with a {right.Category} value in the same expression",
                    b.Line, b.Column);
            }

            // Multiplying/dividing two unit-bearing values (e.g. deg * ms)
            // isn't meaningful in this language — there's no compound-unit
            // result type — so reject it explicitly rather than silently
            // producing a number with a made-up unit.
            bool bothCategorized = left.Category != UnitCategory.None && right.Category != UnitCategory.None;
            if (bothCategorized && (b.Op == BinaryOperator.Multiply || b.Op == BinaryOperator.Divide || b.Op == BinaryOperator.Modulo))
            {
                throw new CompileError(
                    $"Cannot multiply/divide two unit-bearing values ({left.Category} and {right.Category})",
                    b.Line, b.Column);
            }

            double value = b.Op switch
            {
                BinaryOperator.Add => left.Value + right.Value,
                BinaryOperator.Subtract => left.Value - right.Value,
                BinaryOperator.Multiply => left.Value * right.Value,
                BinaryOperator.Divide => DivideChecked(left.Value, right.Value, b),
                BinaryOperator.Modulo => ModuloChecked(left.Value, right.Value, b),
                BinaryOperator.Equal => Math.Abs(left.Value - right.Value) < 1e-9 ? 1.0 : 0.0,
                BinaryOperator.NotEqual => Math.Abs(left.Value - right.Value) < 1e-9 ? 0.0 : 1.0,
                BinaryOperator.Less => left.Value < right.Value ? 1.0 : 0.0,
                BinaryOperator.Greater => left.Value > right.Value ? 1.0 : 0.0,
                BinaryOperator.LessEqual => left.Value <= right.Value ? 1.0 : 0.0,
                BinaryOperator.GreaterEqual => left.Value >= right.Value ? 1.0 : 0.0,
                _ => throw new CompileError($"Unhandled operator {b.Op}", b.Line, b.Column)
            };

            // A comparison is a plain 1/0 regardless of the units that were compared.
            if (b.Op >= BinaryOperator.Equal)
                return new EvalResult(value, UnitCategory.None);

            return new EvalResult(value, resultCategory);
        }

        private double DivideChecked(double l, double r, BinaryOpExpr b)
        {
            if (r == 0.0) throw new CompileError("Division by zero in constant expression", b.Line, b.Column);
            return l / r;
        }

        private double ModuloChecked(double l, double r, BinaryOpExpr b)
        {
            if (r == 0.0) throw new CompileError("Modulo by zero in constant expression", b.Line, b.Column);
            return l % r;
        }
    }

    /// <summary>
    /// Top-level compiler: evaluates VAR/BULLET/SPAWN_POINT declarations,
    /// then unrolls every SPAWN_PATTERN into a flat, time-ordered
    /// instruction array. See the grammar spec §4 for the delay
    /// accumulation model this implements.
    /// </summary>
    public class Compiler
    {
        // Guards against an accidental explosion (e.g. nested loops with huge bounds)
        // freezing the editor or the build.
        private const int MaxLoopIterations = 100_000;
        public const int DefaultMaxInstructions = 2_000_000;
        private const string CopyIndexName = "index";

        private readonly double _framesPerSecond;
        private readonly int _maxInstructions;
        private readonly Dictionary<string, EvalResult> _globalVars = new();
        private readonly Dictionary<string, int> _bulletIds = new();
        private readonly Dictionary<int, uint> _bulletColors = new();
        private readonly Dictionary<int, double> _bulletSpeeds = new();
        private readonly Dictionary<string, int> _spawnPointIds = new();
        private readonly Dictionary<string, SpawnPatternDefNode> _patternDefs = new();

        private long _seed;
        private SeededRandom _rng;

        public Compiler(double framesPerSecond = 60.0, int maxInstructions = DefaultMaxInstructions)
        {
            _framesPerSecond = framesPerSecond;
            _maxInstructions = maxInstructions > 0 ? maxInstructions : DefaultMaxInstructions;
            _seed = 0;
            _rng = new SeededRandom(_seed);
        }

        public CompiledProgram Compile(ProgramNode program)
        {
            var result = new CompiledProgram();

            foreach (var decl in program.Declarations)
            {
                switch (decl)
                {
                    case SeedDeclNode sd:
                        _seed = sd.Value;
                        _rng = new SeededRandom(_seed);
                        break;
                    case VarDeclNode v:
                        CompileVarDecl(v);
                        break;
                    case BulletDefNode b:
                        result.Bullets.Add(CompileBulletDef(b));
                        break;
                    case SpawnPointDefNode s:
                        result.SpawnPoints.Add(CompileSpawnPointDef(s));
                        break;
                    case SpawnPatternDefNode p:
                        _patternDefs[p.Name] = p;
                        // Patterns with parameters are templates: they only run when CALLed.
                        if (p.Params.Count == 0)
                            result.Patterns[p.Name] = CompileSpawnPattern(p);
                        break;
                }
            }

            return result;
        }

        private ConstEvaluator NewEvaluator(Dictionary<string, EvalResult>? bindings = null) =>
            new(_globalVars, bindings ?? new Dictionary<string, EvalResult>(), _framesPerSecond, _rng);

        private void CompileVarDecl(VarDeclNode v)
        {
            var value = NewEvaluator().Evaluate(v.Value);
            _globalVars[v.Name] = value;
        }

        private CompiledBullet CompileBulletDef(BulletDefNode b)
        {
            var eval = NewEvaluator();
            int id = _bulletIds.Count;
            _bulletIds[b.Name] = id;

            double speed = b.Speed != null ? RequireCategory(eval.Evaluate(b.Speed), UnitCategory.None, "speed", b.Speed) : 0.0;
            double size = b.Size != null ? RequireCategory(eval.Evaluate(b.Size), UnitCategory.None, "size", b.Size) : 0.0;
            double lifetimeMs = b.Lifetime != null ? RequireCategory(eval.Evaluate(b.Lifetime), UnitCategory.Duration, "lifetime", b.Lifetime) : 0.0;

            uint rgb = b.Color != null ? RequireColor(eval.Evaluate(b.Color), "color", b.Color) : 0xFFFFFF;
            _bulletColors[id] = rgb;
            _bulletSpeeds[id] = speed;

            return new CompiledBullet { Id = id, Name = b.Name, Speed = speed, Size = size, Sprite = b.Sprite, LifetimeMs = lifetimeMs, Rgb = rgb };
        }

        private CompiledSpawnPoint CompileSpawnPointDef(SpawnPointDefNode s)
        {
            var eval = NewEvaluator();
            int id = _spawnPointIds.Count;
            _spawnPointIds[s.Name] = id;

            double x = RequireCategory(eval.Evaluate(s.X), UnitCategory.None, "x", s.X);
            double y = RequireCategory(eval.Evaluate(s.Y), UnitCategory.None, "y", s.Y);

            return new CompiledSpawnPoint { Id = id, Name = s.Name, X = x, Y = y };
        }

        private List<Instruction> CompileSpawnPattern(SpawnPatternDefNode pattern)
        {
            // Every entry-point pattern restarts the random stream from the seed, so a
            // pattern compiles to the same output no matter what else is in the file.
            _rng = new SeededRandom(_seed);

            var output = new List<Instruction>();
            var bindings = new Dictionary<string, EvalResult>();
            double clockMs = 0.0;
            ProcessStmts(pattern.Body, ref clockMs, bindings, output);
            return output;
        }

        private void ProcessStmts(List<PatternStmt> stmts, ref double clockMs, Dictionary<string, EvalResult> bindings, List<Instruction> output)
        {
            foreach (var stmt in stmts)
            {
                switch (stmt)
                {
                    case SpawnStmt s:
                        CompileSpawnStmt(s, clockMs, bindings, output);
                        break;

                    case LoopStmt l:
                        CompileLoopStmt(l, ref clockMs, bindings, output);
                        break;

                    case PhaseStmt p:
                        CompilePhaseStmt(p, ref clockMs, bindings, output);
                        break;

                    case CallStmt c:
                        CompileCallStmt(c, ref clockMs, bindings, output);
                        break;
                }
            }
        }

        private void CompileSpawnStmt(SpawnStmt s, double clockMs, Dictionary<string, EvalResult> bindings, List<Instruction> output)
        {
            var eval = NewEvaluator(bindings);

            double baseAngle = s.Angle != null ? RequireCategory(eval.Evaluate(s.Angle), UnitCategory.Angle, "angle", s.Angle) : 0.0;
            double countRaw = s.Count != null ? RequireCategory(eval.Evaluate(s.Count), UnitCategory.None, "count", s.Count) : 1.0;
            double spread = s.Spread != null ? RequireCategory(eval.Evaluate(s.Spread), UnitCategory.Angle, "spread", s.Spread) : 0.0;
            double localDelay = s.Delay != null ? RequireCategory(eval.Evaluate(s.Delay), UnitCategory.Duration, "delay", s.Delay) : 0.0;

            int count = (int)Math.Round(countRaw);
            if (Math.Abs(countRaw - count) > 1e-6 || count < 1)
                throw new CompileError($"SPAWN 'count' must evaluate to a whole number >= 1 (got {countRaw})", s.Line, s.Column);

            int singleBulletId = s.BulletChoices == null ? LookupBulletId(s.BulletName, s) : -1;
            if (!_spawnPointIds.TryGetValue(s.SpawnPointName, out int spawnPointId))
                throw new CompileError($"Internal error: spawn point '{s.SpawnPointName}' not registered (should have been caught by the parser)", s.Line, s.Column);

            // Radial symmetry sugar (grammar §3.6): `count` copies, each
            // offset by spread/count from the base angle.
            double step = count > 1 ? spread / count : 0.0;
            for (int i = 0; i < count; i++)
            {
                if (output.Count >= _maxInstructions)
                    throw new CompileError(
                        $"Pattern produced more than {_maxInstructions} bullets; check LOOP bounds and counts",
                        s.Line, s.Column);

                double angle = NormalizeDegrees(baseAngle + i * step);

                // `index` is the copy number. `color: [#FF3030, #FFFFFF][index % 2]` and
                // `SPAWN [a, b][index % 2] AT ...` both alternate per bullet. All of it is
                // resolved here, so nothing is left to decide at runtime.
                bindings[CopyIndexName] = new EvalResult(i, UnitCategory.None);

                // Per-copy fields are evaluated in a fixed order (bullet, color, speed,
                // accel, speed_limit, turn) so rand()/randi() draws are reproducible.
                int bulletId = singleBulletId;
                if (s.BulletChoices != null)
                {
                    int pick = eval.EvaluateListIndex(s.BulletIndex!, s.BulletChoices.Count, s.Line, s.Column);
                    bulletId = LookupBulletId(s.BulletChoices[pick], s);
                }

                // SPAWN color overrides the chosen BULLET's own color.
                uint rgb = s.Color != null
                    ? RequireColor(eval.Evaluate(s.Color), "color", s.Color)
                    : _bulletColors[bulletId];

                // SPAWN speed overrides the chosen BULLET's speed.
                double speed = s.Speed != null
                    ? RequireCategory(eval.Evaluate(s.Speed), UnitCategory.None, "speed", s.Speed)
                    : _bulletSpeeds[bulletId];
                if (speed < 0)
                    throw new CompileError($"SPAWN 'speed' must be >= 0 (got {speed})", s.Speed?.Line ?? s.Line, s.Speed?.Column ?? s.Column);

                double accel = s.Accel != null
                    ? RequireCategory(eval.Evaluate(s.Accel), UnitCategory.None, "accel", s.Accel)
                    : 0.0;

                double? speedLimit = null;
                if (s.SpeedLimit != null)
                {
                    double limit = RequireCategory(eval.Evaluate(s.SpeedLimit), UnitCategory.None, "speed_limit", s.SpeedLimit);
                    if (limit < 0)
                        throw new CompileError($"SPAWN 'speed_limit' must be >= 0 (got {limit})", s.SpeedLimit.Line, s.SpeedLimit.Column);
                    speedLimit = limit;
                }

                double turn = s.Turn != null
                    ? RequireCategory(eval.Evaluate(s.Turn), UnitCategory.Angle, "turn", s.Turn)
                    : 0.0;

                output.Add(new Instruction(bulletId, spawnPointId, angle, clockMs + localDelay, rgb, speed, accel, speedLimit, turn));
            }
            bindings.Remove(CopyIndexName);
        }

        private int LookupBulletId(string name, SpawnStmt s)
        {
            if (!_bulletIds.TryGetValue(name, out int id))
                throw new CompileError($"Internal error: bullet '{name}' not registered (should have been caught by the parser)", s.Line, s.Column);
            return id;
        }

        private void CompileLoopStmt(LoopStmt l, ref double clockMs, Dictionary<string, EvalResult> bindings, List<Instruction> output)
        {
            var eval = NewEvaluator(bindings);
            int first = RequireWholeNumber(eval.Evaluate(l.From), "LOOP start", l.From);
            int last = RequireWholeNumber(eval.Evaluate(l.To), "LOOP end", l.To);
            int stride = l.Step != null ? RequireWholeNumber(eval.Evaluate(l.Step), "LOOP STEP", l.Step) : 1;

            if (stride == 0)
                throw new CompileError("LOOP STEP cannot be 0 (would never terminate)", l.Step!.Line, l.Step.Column);
            if ((stride > 0 && first > last) || (stride < 0 && first < last))
                throw new CompileError($"LOOP bounds {first} TO {last} STEP {stride} would never execute", l.Line, l.Column);

            long iterations = ((long)last - first) / stride + 1;
            if (iterations > MaxLoopIterations)
                throw new CompileError($"LOOP would run {iterations} times (limit is {MaxLoopIterations})", l.Line, l.Column);

            for (int i = first; stride > 0 ? i <= last : i >= last; i += stride)
            {
                bindings[l.LoopVar] = new EvalResult(i, UnitCategory.None);
                ProcessStmts(l.Body, ref clockMs, bindings, output);
            }
            bindings.Remove(l.LoopVar);
        }

        private void CompilePhaseStmt(PhaseStmt p, ref double clockMs, Dictionary<string, EvalResult> bindings, List<Instruction> output)
        {
            if (p.AfterDelay != null)
            {
                var eval = NewEvaluator(bindings);
                double afterMs = RequireCategory(eval.Evaluate(p.AfterDelay), UnitCategory.Duration, "PHASE AFTER", p.AfterDelay);
                clockMs += afterMs; // cumulative — see grammar spec §4 delay accumulation model
            }

            ProcessStmts(p.Body, ref clockMs, bindings, output);
        }

        // CALL expands the other pattern's body in place. The arguments are evaluated
        // in the caller's scope; the body then runs in a fresh scope that holds only its
        // own parameters, so it cannot see the caller's LOOP variables. The clock is
        // shared, so a PHASE inside the called pattern advances the caller's time too.
        private void CompileCallStmt(CallStmt c, ref double clockMs, Dictionary<string, EvalResult> bindings, List<Instruction> output)
        {
            if (!_patternDefs.TryGetValue(c.PatternName, out var def))
                throw new CompileError($"Internal error: pattern '{c.PatternName}' not registered (should have been caught by the parser)", c.Line, c.Column);
            if (c.Args.Count != def.Params.Count)
                throw new CompileError(
                    $"Pattern '{c.PatternName}' takes {def.Params.Count} argument(s), got {c.Args.Count}",
                    c.Line, c.Column);

            var eval = NewEvaluator(bindings);
            var calleeBindings = new Dictionary<string, EvalResult>();
            for (int i = 0; i < def.Params.Count; i++)
                calleeBindings[def.Params[i]] = eval.Evaluate(c.Args[i]);

            ProcessStmts(def.Body, ref clockMs, calleeBindings, output);
        }

        /// <summary>
        /// Enforces the field-context unit coercion rule from the grammar
        /// spec (§3.8): a value with no unit is accepted anywhere, taking
        /// on the field's expected category; a value whose category
        /// actively conflicts with the field's expected category is a
        /// compile error.
        /// </summary>
        private static double RequireCategory(EvalResult r, UnitCategory expected, string fieldName, ConstExpr sourceForLocation)
        {
            if (r.Category == UnitCategory.None || r.Category == expected)
                return r.Value;

            throw new CompileError(
                $"'{fieldName}' expected a {(expected == UnitCategory.None ? "plain number" : expected.ToString().ToLowerInvariant() + " value")}, got a {r.Category.ToString().ToLowerInvariant()} value",
                sourceForLocation.Line, sourceForLocation.Column);
        }

        // LOOP bounds: a plain whole number, no units.
        private static int RequireWholeNumber(EvalResult r, string what, ConstExpr sourceForLocation)
        {
            if (r.Category != UnitCategory.None)
                throw new CompileError(
                    $"{what} must be a plain whole number, got a {r.Category.ToString().ToLowerInvariant()} value",
                    sourceForLocation.Line, sourceForLocation.Column);

            int n = (int)Math.Round(r.Value);
            if (Math.Abs(r.Value - n) > 1e-6)
                throw new CompileError($"{what} must be a whole number (got {r.Value})", sourceForLocation.Line, sourceForLocation.Column);
            return n;
        }

        // Unlike RequireCategory, a plain number is NOT accepted here: `color: 5` is an error.
        private static uint RequireColor(EvalResult r, string fieldName, ConstExpr sourceForLocation)
        {
            if (r.Category == UnitCategory.Color)
                return (uint)r.Value;

            throw new CompileError(
                $"'{fieldName}' expected a color like #FF3030, got a {(r.Category == UnitCategory.None ? "plain number" : r.Category.ToString().ToLowerInvariant() + " value")}",
                sourceForLocation.Line, sourceForLocation.Column);
        }

        private static double NormalizeDegrees(double degrees)
        {
            double d = degrees % 360.0;
            return d < 0 ? d + 360.0 : d;
        }
    }
}