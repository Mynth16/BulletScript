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
    /// source at runtime.
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

        public Instruction(int bulletId, int spawnPointId, double angleDegrees, double delayMs, uint rgb = 0xFFFFFF)
        {
            BulletId = bulletId;
            SpawnPointId = spawnPointId;
            AngleDegrees = angleDegrees;
            DelayMs = delayMs;
            Rgb = rgb;
        }

        public override string ToString() =>
            $"Instruction(bullet={BulletId}, point={SpawnPointId}, angle={AngleDegrees:F2}deg, delay={DelayMs:F1}ms, color={Color})";
    }

    public class CompiledProgram
    {
        public List<CompiledBullet> Bullets { get; } = new();
        public List<CompiledSpawnPoint> SpawnPoints { get; } = new();

        // Pattern name -> its flat, time-ordered instruction list.
        public Dictionary<string, List<Instruction>> Patterns { get; } = new();
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
        private readonly Dictionary<string, int> _loopBindings;
        private readonly double _framesPerSecond;

        public ConstEvaluator(Dictionary<string, EvalResult> globalVars, Dictionary<string, int> loopBindings, double framesPerSecond)
        {
            _globalVars = globalVars;
            _loopBindings = loopBindings;
            _framesPerSecond = framesPerSecond;
        }

        public EvalResult Evaluate(ConstExpr expr) => expr switch
        {
            NumberLiteral n => EvalNumberLiteral(n),
            VarRef v => EvalVarRef(v),
            BinaryOpExpr b => EvalBinary(b),
            UnaryMinusExpr u => Negate(Evaluate(u.Operand), u),
            ColorLiteral c => new EvalResult(c.Rgb, UnitCategory.Color),
            IndexExpr ix => EvalIndex(ix),
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
            // LOOP variables shadow nothing (parser already forbids name
            // collisions with VAR), so checking loop bindings first is safe
            // and just avoids an unnecessary dictionary miss on the common
            // case of referencing a LOOP index.
            if (_loopBindings.TryGetValue(v.Name, out int loopValue))
                return new EvalResult(loopValue, UnitCategory.None);

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
        private readonly double _framesPerSecond;
        private readonly Dictionary<string, EvalResult> _globalVars = new();
        private readonly Dictionary<string, int> _bulletIds = new();
        private readonly Dictionary<int, uint> _bulletColors = new();
        private readonly Dictionary<string, int> _spawnPointIds = new();

        public Compiler(double framesPerSecond = 60.0)
        {
            _framesPerSecond = framesPerSecond;
        }

        public CompiledProgram Compile(ProgramNode program)
        {
            var result = new CompiledProgram();

            foreach (var decl in program.Declarations)
            {
                switch (decl)
                {
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
                        result.Patterns[p.Name] = CompileSpawnPattern(p);
                        break;
                }
            }

            return result;
        }

        private ConstEvaluator NewEvaluator(Dictionary<string, int>? loopBindings = null) =>
            new(_globalVars, loopBindings ?? new Dictionary<string, int>(), _framesPerSecond);

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
            var output = new List<Instruction>();
            var loopBindings = new Dictionary<string, int>();
            double clockMs = 0.0;
            ProcessStmts(pattern.Body, ref clockMs, loopBindings, output);
            return output;
        }

        private void ProcessStmts(List<PatternStmt> stmts, ref double clockMs, Dictionary<string, int> loopBindings, List<Instruction> output)
        {
            foreach (var stmt in stmts)
            {
                switch (stmt)
                {
                    case SpawnStmt s:
                        CompileSpawnStmt(s, clockMs, loopBindings, output);
                        break;

                    case LoopStmt l:
                        CompileLoopStmt(l, ref clockMs, loopBindings, output);
                        break;

                    case PhaseStmt p:
                        CompilePhaseStmt(p, ref clockMs, loopBindings, output);
                        break;
                }
            }
        }

        private void CompileSpawnStmt(SpawnStmt s, double clockMs, Dictionary<string, int> loopBindings, List<Instruction> output)
        {
            var eval = NewEvaluator(loopBindings);

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
                double angle = NormalizeDegrees(baseAngle + i * step);

                // `index` is the copy number. `color: [#FF3030, #FFFFFF][index % 2]` and
                // `SPAWN [a, b][index % 2] AT ...` both alternate per bullet. All of it is
                // resolved here, so nothing is left to decide at runtime.
                loopBindings[CopyIndexName] = i;

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

                output.Add(new Instruction(bulletId, spawnPointId, angle, clockMs + localDelay, rgb));
            }
            loopBindings.Remove(CopyIndexName);
        }

        private int LookupBulletId(string name, SpawnStmt s)
        {
            if (!_bulletIds.TryGetValue(name, out int id))
                throw new CompileError($"Internal error: bullet '{name}' not registered (should have been caught by the parser)", s.Line, s.Column);
            return id;
        }

        private void CompileLoopStmt(LoopStmt l, ref double clockMs, Dictionary<string, int> loopBindings, List<Instruction> output)
        {
            for (int i = l.From; l.Step > 0 ? i <= l.To : i >= l.To; i += l.Step)
            {
                loopBindings[l.LoopVar] = i;
                ProcessStmts(l.Body, ref clockMs, loopBindings, output);
            }
            loopBindings.Remove(l.LoopVar);
        }

        private void CompilePhaseStmt(PhaseStmt p, ref double clockMs, Dictionary<string, int> loopBindings, List<Instruction> output)
        {
            if (p.AfterDelay != null)
            {
                var eval = NewEvaluator(loopBindings);
                double afterMs = RequireCategory(eval.Evaluate(p.AfterDelay), UnitCategory.Duration, "PHASE AFTER", p.AfterDelay);
                clockMs += afterMs; // cumulative — see grammar spec §4 delay accumulation model
            }

            ProcessStmts(p.Body, ref clockMs, loopBindings, output);
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

        private const string CopyIndexName = "index";

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