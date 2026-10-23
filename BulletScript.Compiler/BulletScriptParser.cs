using System;
using System.Collections.Generic;
using BulletScript.Compiler.Ast;

namespace BulletScript.Compiler
{
    public class ParseError : Exception
    {
        public int Line { get; }
        public int Column { get; }

        public ParseError(string message, int line, int column)
            : base($"Parse error at {line}:{column}: {message}")
        {
            Line = line;
            Column = column;
        }
    }

    /// <summary>
    /// Recursive-descent parser. Enforces "no forward references" for VAR,
    /// BULLET, and SPAWN_POINT as it goes: a name must already be in the
    /// relevant symbol set at the point it's referenced. This means
    /// downstream compiler stages (the unroller) can assume every reference
    /// in the AST is already valid and never need to re-check existence.
    /// </summary>
    public class Parser
    {
        private readonly List<Token> _tokens;
        private int _pos;

        // Symbol tracking, populated in declaration order as we parse.
        private readonly HashSet<string> _declaredVars = new();
        private readonly HashSet<string> _declaredBullets = new();
        private readonly HashSet<string> _declaredSpawnPoints = new();

        // LOOP variables are compile-time-only bindings scoped to the body of
        // their LOOP (and any nested LOOPs/PHASEs inside it) — a separate
        // namespace from VAR. Tracked as a stack so nested LOOPs work and
        // each variable falls out of scope when its LOOP body finishes.
        private readonly List<string> _activeLoopVars = new();

        // `index` is the per-copy counter (0..count-1) inside a SPAWN's color
        // expression. It is only in scope while that expression is parsed, and
        // the name is reserved so it can never collide with a VAR or LOOP var.
        private const string CopyIndexName = "index";
        private bool _copyIndexInScope;

        public Parser(List<Token> tokens)
        {
            _tokens = tokens;
        }

        // ---------- Token stream helpers ----------

        private Token Current => _tokens[_pos];

        private Token Advance() => _tokens[_pos++];

        private bool Check(TokenType type) => Current.Type == type;

        private Token Expect(TokenType type, string context)
        {
            if (Current.Type != type)
                throw new ParseError(
                    $"Expected {type} {context}, found {Current.Type} ('{Current.Lexeme}')",
                    Current.Line, Current.Column);
            return Advance();
        }

        private bool Match(TokenType type)
        {
            if (Check(type)) { Advance(); return true; }
            return false;
        }

        // ---------- Entry point ----------

        public ProgramNode ParseProgram()
        {
            var program = new ProgramNode();
            while (!Check(TokenType.EOF))
            {
                program.Declarations.Add(ParseTopLevelDecl());
            }
            return program;
        }

        private TopLevelDecl ParseTopLevelDecl()
        {
            return Current.Type switch
            {
                TokenType.KwVar => ParseVarDecl(),
                TokenType.KwBullet => ParseBulletDef(),
                TokenType.KwSpawnPoint => ParseSpawnPointDef(),
                TokenType.KwSpawnPattern => ParseSpawnPatternDef(),
                _ => throw new ParseError(
                        $"Expected a top-level declaration (VAR, BULLET, SPAWN_POINT, SPAWN_PATTERN), found {Current.Type}",
                        Current.Line, Current.Column)
            };
        }

        // ---------- VAR ----------

        private VarDeclNode ParseVarDecl()
        {
            var kw = Expect(TokenType.KwVar, "to start a VAR declaration");
            var nameTok = Expect(TokenType.Identifier, "as VAR name");

            if (_declaredVars.Contains(nameTok.Lexeme))
                throw new ParseError($"VAR '{nameTok.Lexeme}' is already declared", nameTok.Line, nameTok.Column);
            if (nameTok.Lexeme == CopyIndexName)
                throw new ParseError($"'{CopyIndexName}' is reserved (it is the per-copy counter inside SPAWN color)", nameTok.Line, nameTok.Column);

            Expect(TokenType.Equals, "after VAR name");
            var value = ParseConstExpr();
            Expect(TokenType.Semicolon, "to end VAR declaration");

            _declaredVars.Add(nameTok.Lexeme);

            return new VarDeclNode { Name = nameTok.Lexeme, Value = value, Line = kw.Line, Column = kw.Column };
        }

        // ---------- Constant expressions ----------
        // const_expr ::= additive [ ('==' | '!=' | '<' | '>' | '<=' | '>=') additive ]
        // A comparison yields 1 or 0 and is not chainable; use parentheses to combine.

        private ConstExpr ParseConstExpr()
        {
            var left = ParseAdditive();

            BinaryOperator? cmp = Current.Type switch
            {
                TokenType.EqEq => BinaryOperator.Equal,
                TokenType.NotEq => BinaryOperator.NotEqual,
                TokenType.Lt => BinaryOperator.Less,
                TokenType.Gt => BinaryOperator.Greater,
                TokenType.LtEq => BinaryOperator.LessEqual,
                TokenType.GtEq => BinaryOperator.GreaterEqual,
                _ => null
            };
            if (cmp == null) return left;

            var opTok = Advance();
            var right = ParseAdditive();
            return new BinaryOpExpr { Op = cmp.Value, Left = left, Right = right, Line = opTok.Line, Column = opTok.Column };
        }

        // additive ::= term { ('+' | '-') term }

        private ConstExpr ParseAdditive()
        {
            var left = ParseTerm();
            while (Check(TokenType.Plus) || Check(TokenType.Minus))
            {
                var opTok = Advance();
                var op = opTok.Type == TokenType.Plus ? BinaryOperator.Add : BinaryOperator.Subtract;
                var right = ParseTerm();
                left = new BinaryOpExpr { Op = op, Left = left, Right = right, Line = opTok.Line, Column = opTok.Column };
            }
            return left;
        }

        // term ::= factor { ('*' | '/' | '%') factor }

        private ConstExpr ParseTerm()
        {
            var left = ParseFactor();
            while (Check(TokenType.Star) || Check(TokenType.Slash) || Check(TokenType.Percent))
            {
                var opTok = Advance();
                var op = opTok.Type switch
                {
                    TokenType.Star => BinaryOperator.Multiply,
                    TokenType.Slash => BinaryOperator.Divide,
                    _ => BinaryOperator.Modulo
                };
                var right = ParseFactor();
                left = new BinaryOpExpr { Op = op, Left = left, Right = right, Line = opTok.Line, Column = opTok.Column };
            }
            return left;
        }

        // factor  ::= '-' factor | primary [ '[' const_expr ']' ]
        // primary ::= NUMBER | ANGLE | DURATION | COLOR | IDENTIFIER | '(' const_expr ')' | list
        // list    ::= '[' const_expr { ',' const_expr } ']'   (only valid when indexed)

        private ConstExpr ParseFactor()
        {
            var tok = Current;

            if (tok.Type == TokenType.Minus)
            {
                Advance();
                var operand = ParseFactor();
                return new UnaryMinusExpr { Operand = operand, Line = tok.Line, Column = tok.Column };
            }

            var primary = ParsePrimary();

            if (Check(TokenType.LBracket))
            {
                var bracket = Advance();
                if (primary is not ListExpr list)
                    throw new ParseError("Only a list literal like [a, b] can be indexed", bracket.Line, bracket.Column);

                var index = ParseConstExpr();
                Expect(TokenType.RBracket, "to close list index");
                return new IndexExpr { List = list, Index = index, Line = bracket.Line, Column = bracket.Column };
            }

            if (primary is ListExpr bare)
                throw new ParseError(
                    "A list must be indexed immediately, e.g. [a, b][i % 2]",
                    bare.Line, bare.Column);

            return primary;
        }

        private ConstExpr ParsePrimary()
        {
            var tok = Current;

            switch (tok.Type)
            {
                case TokenType.ColorLiteral:
                    Advance();
                    return new ColorLiteral { Rgb = (uint)tok.NumericValue, Line = tok.Line, Column = tok.Column };

                case TokenType.LBracket:
                {
                    Advance();
                    var items = new List<ConstExpr> { ParseConstExpr() };
                    while (Match(TokenType.Comma))
                        items.Add(ParseConstExpr());
                    Expect(TokenType.RBracket, "to close list literal");
                    return new ListExpr { Items = items, Line = tok.Line, Column = tok.Column };
                }

                case TokenType.IntegerLiteral:
                case TokenType.FloatLiteral:
                case TokenType.AngleLiteral:
                case TokenType.DurationLiteral:
                    Advance();
                    return new NumberLiteral { Value = tok.NumericValue, Unit = tok.Unit, Line = tok.Line, Column = tok.Column };

                case TokenType.Identifier:
                    Advance();
                    bool isCopyIndex = _copyIndexInScope && tok.Lexeme == CopyIndexName;
                    if (!isCopyIndex && !_declaredVars.Contains(tok.Lexeme) && !_activeLoopVars.Contains(tok.Lexeme))
                        throw new ParseError(
                            $"Reference to undeclared VAR or LOOP variable '{tok.Lexeme}' (must be declared/bound before use)",
                            tok.Line, tok.Column);
                    return new VarRef { Name = tok.Lexeme, Line = tok.Line, Column = tok.Column };

                case TokenType.LParen:
                    Advance();
                    var inner = ParseConstExpr();
                    Expect(TokenType.RParen, "to close parenthesized expression");
                    return inner;

                case TokenType.Minus:
                    Advance();
                    var operand = ParseFactor();
                    return new UnaryMinusExpr { Operand = operand, Line = tok.Line, Column = tok.Column };

                default:
                    throw new ParseError(
                        $"Expected a number, VAR reference, or '(' in expression, found {tok.Type}",
                        tok.Line, tok.Column);
            }
        }

        // ---------- BULLET ----------

        private BulletDefNode ParseBulletDef()
        {
            var kw = Expect(TokenType.KwBullet, "to start a BULLET definition");
            var nameTok = Expect(TokenType.Identifier, "as BULLET name");

            if (_declaredBullets.Contains(nameTok.Lexeme))
                throw new ParseError($"BULLET '{nameTok.Lexeme}' is already declared", nameTok.Line, nameTok.Column);

            Expect(TokenType.LBrace, "to open BULLET body");

            ConstExpr? speed = null, size = null, lifetime = null, color = null;
            string? sprite = null;

            while (!Check(TokenType.RBrace))
            {
                var propTok = Expect(TokenType.Identifier, "as a BULLET property name (speed/size/sprite/lifetime)");
                Expect(TokenType.Colon, "after BULLET property name");

                switch (propTok.Lexeme)
                {
                    case "speed":
                        speed = ParseConstExpr();
                        break;
                    case "size":
                        size = ParseConstExpr();
                        break;
                    case "sprite":
                        sprite = Expect(TokenType.StringLiteral, "as sprite path").Lexeme;
                        break;
                    case "lifetime":
                        lifetime = ParseConstExpr();
                        break;
                    case "color":
                        color = ParseConstExpr();
                        break;
                    case "damage":
                        throw new ParseError(
                            "'damage' is a gameplay-only field and is not part of BULLET (excluded from the benchmark corpus)",
                            propTok.Line, propTok.Column);
                    default:
                        throw new ParseError($"Unknown BULLET property '{propTok.Lexeme}'", propTok.Line, propTok.Column);
                }

                Expect(TokenType.Semicolon, "after BULLET property value");
            }

            Expect(TokenType.RBrace, "to close BULLET body");
            _declaredBullets.Add(nameTok.Lexeme);

            return new BulletDefNode
            {
                Name = nameTok.Lexeme,
                Speed = speed,
                Size = size,
                Sprite = sprite,
                Lifetime = lifetime,
                Color = color,
                Line = kw.Line,
                Column = kw.Column
            };
        }

        // ---------- SPAWN_POINT ----------

        private SpawnPointDefNode ParseSpawnPointDef()
        {
            var kw = Expect(TokenType.KwSpawnPoint, "to start a SPAWN_POINT definition");
            var nameTok = Expect(TokenType.Identifier, "as SPAWN_POINT name");

            if (_declaredSpawnPoints.Contains(nameTok.Lexeme))
                throw new ParseError($"SPAWN_POINT '{nameTok.Lexeme}' is already declared", nameTok.Line, nameTok.Column);

            Expect(TokenType.LBrace, "to open SPAWN_POINT body");

            ConstExpr? x = null, y = null;

            while (!Check(TokenType.RBrace))
            {
                var propTok = Expect(TokenType.Identifier, "as 'x' or 'y'");
                Expect(TokenType.Colon, "after SPAWN_POINT property name");

                switch (propTok.Lexeme)
                {
                    case "x": x = ParseConstExpr(); break;
                    case "y": y = ParseConstExpr(); break;
                    default:
                        throw new ParseError($"Unknown SPAWN_POINT property '{propTok.Lexeme}' (expected x or y)", propTok.Line, propTok.Column);
                }

                Expect(TokenType.Semicolon, "after SPAWN_POINT property value");
            }

            Expect(TokenType.RBrace, "to close SPAWN_POINT body");

            if (x == null || y == null)
                throw new ParseError($"SPAWN_POINT '{nameTok.Lexeme}' must define both x and y", nameTok.Line, nameTok.Column);

            _declaredSpawnPoints.Add(nameTok.Lexeme);

            return new SpawnPointDefNode { Name = nameTok.Lexeme, X = x, Y = y, Line = kw.Line, Column = kw.Column };
        }

        // ---------- SPAWN_PATTERN ----------

        private SpawnPatternDefNode ParseSpawnPatternDef()
        {
            var kw = Expect(TokenType.KwSpawnPattern, "to start a SPAWN_PATTERN definition");
            var nameTok = Expect(TokenType.Identifier, "as SPAWN_PATTERN name");

            Expect(TokenType.LBrace, "to open SPAWN_PATTERN body");
            var body = ParsePatternStmtList();
            Expect(TokenType.RBrace, "to close SPAWN_PATTERN body");

            return new SpawnPatternDefNode { Name = nameTok.Lexeme, Body = body, Line = kw.Line, Column = kw.Column };
        }

        private List<PatternStmt> ParsePatternStmtList()
        {
            var stmts = new List<PatternStmt>();
            while (!Check(TokenType.RBrace))
            {
                stmts.Add(ParsePatternStmt());
            }
            return stmts;
        }

        private PatternStmt ParsePatternStmt()
        {
            return Current.Type switch
            {
                TokenType.KwSpawn => ParseSpawnStmt(),
                TokenType.KwLoop => ParseLoopStmt(),
                TokenType.KwPhase => ParsePhaseStmt(),
                _ => throw new ParseError(
                        $"Expected SPAWN, LOOP, or PHASE inside SPAWN_PATTERN, found {Current.Type}",
                        Current.Line, Current.Column)
            };
        }

        // spawn_stmt   ::= 'SPAWN' bullet_ref 'AT' IDENTIFIER '{' { spawn_prop } '}' ';'
        // bullet_ref   ::= IDENTIFIER | '[' IDENTIFIER { ',' IDENTIFIER } ']' '[' const_expr ']'
        //                  (in the indexed form, `index` is the copy number 0..count-1)

        private SpawnStmt ParseSpawnStmt()
        {
            var kw = Expect(TokenType.KwSpawn, "to start a SPAWN statement");

            string bulletName = "";
            List<string>? bulletChoices = null;
            ConstExpr? bulletIndex = null;

            if (Check(TokenType.LBracket))
            {
                Advance();
                bulletChoices = new List<string>();
                do
                {
                    var choiceTok = Expect(TokenType.Identifier, "as a bullet type in the list");
                    if (!_declaredBullets.Contains(choiceTok.Lexeme))
                        throw new ParseError($"Reference to undeclared BULLET '{choiceTok.Lexeme}'", choiceTok.Line, choiceTok.Column);
                    bulletChoices.Add(choiceTok.Lexeme);
                } while (Match(TokenType.Comma));

                Expect(TokenType.RBracket, "to close the bullet list");
                Expect(TokenType.LBracket, "to index the bullet list, e.g. SPAWN [a, b][index % 2] AT ...");

                _copyIndexInScope = true;
                try { bulletIndex = ParseConstExpr(); }
                finally { _copyIndexInScope = false; }

                Expect(TokenType.RBracket, "to close the bullet list index");
            }
            else
            {
                var bulletTok = Expect(TokenType.Identifier, "as bullet type after SPAWN");

                if (!_declaredBullets.Contains(bulletTok.Lexeme))
                    throw new ParseError($"Reference to undeclared BULLET '{bulletTok.Lexeme}'", bulletTok.Line, bulletTok.Column);
                bulletName = bulletTok.Lexeme;
            }

            Expect(TokenType.KwAt, "after bullet type in SPAWN statement");
            var pointTok = Expect(TokenType.Identifier, "as spawn point after AT");

            if (!_declaredSpawnPoints.Contains(pointTok.Lexeme))
                throw new ParseError($"Reference to undeclared SPAWN_POINT '{pointTok.Lexeme}'", pointTok.Line, pointTok.Column);

            Expect(TokenType.LBrace, "to open SPAWN body");

            ConstExpr? angle = null, count = null, spread = null, delay = null, color = null;

            while (!Check(TokenType.RBrace))
            {
                var propTok = Expect(TokenType.Identifier, "as a SPAWN property (angle/count/spread/delay)");
                Expect(TokenType.Colon, "after SPAWN property name");

                switch (propTok.Lexeme)
                {
                    case "angle": angle = ParseConstExpr(); break;
                    case "count": count = ParseConstExpr(); break;
                    case "spread": spread = ParseConstExpr(); break;
                    case "delay": delay = ParseConstExpr(); break;
                    case "color":
                        // `index` (0..count-1) is only visible inside this expression.
                        _copyIndexInScope = true;
                        try { color = ParseConstExpr(); }
                        finally { _copyIndexInScope = false; }
                        break;
                    default:
                        throw new ParseError($"Unknown SPAWN property '{propTok.Lexeme}'", propTok.Line, propTok.Column);
                }

                Expect(TokenType.Semicolon, "after SPAWN property value");
            }

            Expect(TokenType.RBrace, "to close SPAWN body");
            Expect(TokenType.Semicolon, "after SPAWN statement");

            return new SpawnStmt
            {
                BulletName = bulletName,
                BulletChoices = bulletChoices,
                BulletIndex = bulletIndex,
                SpawnPointName = pointTok.Lexeme,
                Angle = angle,
                Count = count,
                Spread = spread,
                Delay = delay,
                Color = color,
                Line = kw.Line,
                Column = kw.Column
            };
        }

        // loop_stmt ::= 'LOOP' IDENTIFIER 'IN' INTEGER 'TO' INTEGER [ 'STEP' INTEGER ] '{' { pattern_stmt } '}'

        private LoopStmt ParseLoopStmt()
        {
            var kw = Expect(TokenType.KwLoop, "to start a LOOP");
            var loopVarTok = Expect(TokenType.Identifier, "as LOOP variable name");

            Expect(TokenType.KwIn, "after LOOP variable name");
            var fromTok = Expect(TokenType.IntegerLiteral, "as LOOP start bound (must be an integer literal)");
            Expect(TokenType.KwTo, "after LOOP start bound");
            var toTok = Expect(TokenType.IntegerLiteral, "as LOOP end bound (must be an integer literal)");

            int step = 1;
            if (Match(TokenType.KwStep))
            {
                var stepTok = Expect(TokenType.IntegerLiteral, "as LOOP step (must be an integer literal)");
                step = (int)stepTok.NumericValue;
                if (step == 0)
                    throw new ParseError("LOOP STEP cannot be 0 (would never terminate)", stepTok.Line, stepTok.Column);
            }

            int from = (int)fromTok.NumericValue;
            int to = (int)toTok.NumericValue;
            if ((step > 0 && from > to) || (step < 0 && from < to))
                throw new ParseError(
                    $"LOOP bounds {from} TO {to} STEP {step} would never execute",
                    kw.Line, kw.Column);

            // The loop variable is a compile-time-only binding, resolved
            // during unrolling — it is a separate namespace from VAR, not
            // added to _declaredVars. It IS pushed onto _activeLoopVars so
            // const_expr inside this LOOP's body (including nested
            // LOOPs/PHASEs) can reference it, e.g. `angle: i * 30deg`.
            if (loopVarTok.Lexeme == CopyIndexName)
                throw new ParseError(
                    $"'{CopyIndexName}' is reserved (it is the per-copy counter inside SPAWN color)",
                    loopVarTok.Line, loopVarTok.Column);
            if (_declaredVars.Contains(loopVarTok.Lexeme))
                throw new ParseError(
                    $"LOOP variable '{loopVarTok.Lexeme}' collides with an existing VAR of the same name",
                    loopVarTok.Line, loopVarTok.Column);
            if (_activeLoopVars.Contains(loopVarTok.Lexeme))
                throw new ParseError(
                    $"LOOP variable '{loopVarTok.Lexeme}' shadows an already-active outer LOOP variable of the same name",
                    loopVarTok.Line, loopVarTok.Column);

            _activeLoopVars.Add(loopVarTok.Lexeme);

            Expect(TokenType.LBrace, "to open LOOP body");
            List<PatternStmt> body;
            try
            {
                body = ParsePatternStmtList();
            }
            finally
            {
                // Always pop, even if the body failed to parse, so a
                // caught/rethrown error higher up doesn't leave a stale
                // binding around for sibling LOOPs later in the file.
                _activeLoopVars.RemoveAt(_activeLoopVars.Count - 1);
            }
            Expect(TokenType.RBrace, "to close LOOP body");

            return new LoopStmt
            {
                LoopVar = loopVarTok.Lexeme,
                From = from,
                To = to,
                Step = step,
                Body = body,
                Line = kw.Line,
                Column = kw.Column
            };
        }

        // phase_stmt ::= 'PHASE' IDENTIFIER [ 'AFTER' phase_delay ] '{' { pattern_stmt } '}'
        // phase_delay ::= DURATION | const_expr

        private PhaseStmt ParsePhaseStmt()
        {
            var kw = Expect(TokenType.KwPhase, "to start a PHASE");
            var nameTok = Expect(TokenType.Identifier, "as PHASE name");

            ConstExpr? afterDelay = null;
            if (Match(TokenType.KwAfter))
            {
                afterDelay = ParseConstExpr();
            }

            Expect(TokenType.LBrace, "to open PHASE body");
            var body = ParsePatternStmtList();
            Expect(TokenType.RBrace, "to close PHASE body");

            return new PhaseStmt { Name = nameTok.Lexeme, AfterDelay = afterDelay, Body = body, Line = kw.Line, Column = kw.Column };
        }
    }
}