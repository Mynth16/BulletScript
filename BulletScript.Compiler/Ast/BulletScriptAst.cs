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

    public enum BinaryOperator { Add, Subtract, Multiply, Divide, Modulo }

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
        public List<PatternStmt> Body { get; init; } = new();
    }

    public abstract class PatternStmt
    {
        public int Line { get; init; }
        public int Column { get; init; }
    }

    public class SpawnStmt : PatternStmt
    {
        public string BulletName { get; init; } = "";
        public string SpawnPointName { get; init; } = "";
        public ConstExpr? Angle { get; init; }
        public ConstExpr? Count { get; init; }
        public ConstExpr? Spread { get; init; }
        public ConstExpr? Delay { get; init; }
    }

    public class LoopStmt : PatternStmt
    {
        public string LoopVar { get; init; } = "";
        public int From { get; init; }
        public int To { get; init; }
        public int Step { get; init; } = 1;
        public List<PatternStmt> Body { get; init; } = new();
    }

    public class PhaseStmt : PatternStmt
    {
        public string Name { get; init; } = "";
        // Null when no AFTER clause is present (defaults to 0 delay downstream).
        public ConstExpr? AfterDelay { get; init; }
        public List<PatternStmt> Body { get; init; } = new();
    }
}