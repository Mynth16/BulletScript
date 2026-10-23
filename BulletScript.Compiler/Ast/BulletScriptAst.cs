using System.Collections.Generic;

namespace BulletScript.Compiler.Ast
{
    // ---------- Top-level program ----------

    public class ProgramNode
    {
        public List<TopLevelDecl> Declarations { get; } = new();
    }

    public abstract class TopLevelDecl
    {
        public int Line { get; init; }
        public int Column { get; init; }
    }

    // ---------- Constant expressions ----------
    // Everything under ConstExpr must be reducible to a literal number at
    // compile time — the unroller (next stage) walks these and folds them.

    public abstract class ConstExpr
    {
        public int Line { get; init; }
        public int Column { get; init; }
    }

    public class NumberLiteral : ConstExpr
    {
        public double Value { get; init; }
        public NumericUnit Unit { get; init; } = NumericUnit.None;
    }

    public class VarRef : ConstExpr
    {
        public string Name { get; init; } = "";
    }

    // The last six are comparisons: they evaluate to 1 (true) or 0 (false), as a plain number.
    public enum BinaryOperator { Add, Subtract, Multiply, Divide, Modulo, Equal, NotEqual, Less, Greater, LessEqual, GreaterEqual }

    public class BinaryOpExpr : ConstExpr
    {
        public BinaryOperator Op { get; init; }
        public ConstExpr Left { get; init; } = null!;
        public ConstExpr Right { get; init; } = null!;
    }

    public class UnaryMinusExpr : ConstExpr
    {
        public ConstExpr Operand { get; init; } = null!;
    }

    // A color literal such as #FF3030 or #f30. Rgb is packed 0xRRGGBB.
    public class ColorLiteral : ConstExpr
    {
        public uint Rgb { get; init; }
    }

    // A list literal such as [a, b, c]. Lists are not first-class values: the
    // parser only accepts them directly followed by an index (see IndexExpr),
    // so they always collapse to a single item at compile time.
    public class ListExpr : ConstExpr
    {
        public List<ConstExpr> Items { get; init; } = new();
    }

    // [a, b, c][i % 2] -- a compile-time selection. This is how a pattern
    // varies a value per bullet without any runtime conditional.
    public class IndexExpr : ConstExpr
    {
        public ListExpr List { get; init; } = null!;
        public ConstExpr Index { get; init; } = null!;
    }

    // rand(lo, hi) -- a float in [lo, hi); randi(lo, hi) -- a whole number in [lo, hi],
    // both ends included. Drawn from a seeded RNG during compilation, so a given
    // .bs file always compiles to the same output (see SEED).
    public class RandExpr : ConstExpr
    {
        public bool Integer { get; init; }
        public ConstExpr Lo { get; init; } = null!;
        public ConstExpr Hi { get; init; } = null!;
    }

    // ---------- SEED ----------

    public class SeedDeclNode : TopLevelDecl
    {
        public long Value { get; init; }
    }

    // ---------- VAR ----------

    public class VarDeclNode : TopLevelDecl
    {
        public string Name { get; init; } = "";
        public ConstExpr Value { get; init; } = null!;
    }

    // ---------- BULLET ----------
    // Gameplay-only fields (damage, etc.) are deliberately excluded — see
    // grammar spec §2 bullet_prop.

    public class BulletDefNode : TopLevelDecl
    {
        public string Name { get; init; } = "";
        public ConstExpr? Speed { get; init; }
        public ConstExpr? Size { get; init; }
        public string? Sprite { get; init; }
        public ConstExpr? Lifetime { get; init; }
        // Default tint for this bullet type; a SPAWN's own color overrides it.
        public ConstExpr? Color { get; init; }
    }

    // ---------- SPAWN_POINT ----------

    public class SpawnPointDefNode : TopLevelDecl
    {
        public string Name { get; init; } = "";
        public ConstExpr X { get; init; } = null!;
        public ConstExpr Y { get; init; } = null!;
    }

    // ---------- SPAWN_PATTERN and its statements ----------

    public class SpawnPatternDefNode : TopLevelDecl
    {
        public string Name { get; init; } = "";
        // Compile-time parameters: `SPAWN_PATTERN ring(n, spd) { ... }`. A pattern with
        // parameters is a template (only reachable through CALL), not an entry point.
        public List<string> Params { get; init; } = new();
        public List<PatternStmt> Body { get; init; } = new();
    }

    public abstract class PatternStmt
    {
        public int Line { get; init; }
        public int Column { get; init; }
    }

    public class SpawnStmt : PatternStmt
    {
        // Single bullet type: `SPAWN orb AT ...` (BulletChoices is null).
        public string BulletName { get; init; } = "";
        // Per-copy bullet type: `SPAWN [a, b][index % 2] AT ...`. Evaluated once per
        // copy, like Color, with `index` bound to 0..count-1.
        public List<string>? BulletChoices { get; init; }
        public ConstExpr? BulletIndex { get; init; }
        public string SpawnPointName { get; init; } = "";
        public ConstExpr? Angle { get; init; }
        public ConstExpr? Count { get; init; }
        public ConstExpr? Spread { get; init; }
        public ConstExpr? Delay { get; init; }
        // The fields below are evaluated once per copy (count), with `index` bound to
        // 0..count-1. Speed falls back to the BULLET's speed when absent.
        public ConstExpr? Color { get; init; }
        public ConstExpr? Speed { get; init; }
        // Speed change per second, in speed units.
        public ConstExpr? Accel { get; init; }
        // Speed that Accel stops at. Without it, deceleration stops at 0.
        public ConstExpr? SpeedLimit { get; init; }
        // Turn rate: angle per second (degrees when written as a plain number).
        public ConstExpr? Turn { get; init; }
    }

    public class LoopStmt : PatternStmt
    {
        public string LoopVar { get; init; } = "";
        // Bounds are compile-time expressions (whole numbers), so VARs and
        // pattern parameters can drive them: `LOOP i IN 0 TO n - 1`.
        public ConstExpr From { get; init; } = null!;
        public ConstExpr To { get; init; } = null!;
        public ConstExpr? Step { get; init; }
        public List<PatternStmt> Body { get; init; } = new();
    }

    // CALL ring(12, 2); -- expands another pattern's body right here, with its
    // parameters bound to the evaluated arguments. The called pattern must be
    // declared earlier in the file.
    public class CallStmt : PatternStmt
    {
        public string PatternName { get; init; } = "";
        public List<ConstExpr> Args { get; init; } = new();
    }

    public class PhaseStmt : PatternStmt
    {
        public string Name { get; init; } = "";
        // Null when no AFTER clause is present (defaults to 0 delay downstream).
        public ConstExpr? AfterDelay { get; init; }
        public List<PatternStmt> Body { get; init; } = new();
    }
}