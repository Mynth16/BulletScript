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

        // Pattern name -> parameter count. A name is added only AFTER its body is
        // parsed, so a pattern can't CALL itself, and CALL can only reach patterns
        // declared earlier in the file (same no-forward-references rule as VAR/BULLET).
        private readonly Dictionary<string, int> _declaredPatterns = new();

        private bool _seedDeclared;
        private bool _randUsed;

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
                TokenType.KwSeed => ParseSeedDecl(),
                _ => throw new ParseError(
                        $"Expected a top-level declaration (SEED, VAR, BULLET, SPAWN_POINT, SPAWN_PATTERN), found {Current.Type}",
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
            RejectReservedName(nameTok, "VAR");

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
                    // rand(...) / randi(...) are built-in functions, recognised by the '(' after them.
                    if ((tok.Lexeme == "rand" || tok.Lexeme == "randi") && _tokens[_pos + 1].Type == TokenType.LParen)
                        return ParseRandCall();

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

        // rand_call ::= ('rand' | 'randi') '(' const_expr ',' const_expr ')'

        private RandExpr ParseRandCall()
        {
            var nameTok = Advance();
            Expect(TokenType.LParen, $"after {nameTok.Lexeme}");
            var lo = ParseConstExpr();
            Expect(TokenType.Comma, $"between the two arguments of {nameTok.Lexeme}()");
            var hi = ParseConstExpr();
            Expect(TokenType.RParen, $"to close {nameTok.Lexeme}()");

            _randUsed = true;
            return new RandExpr
            {
                Integer = nameTok.Lexeme == "randi",
                Lo = lo,
                Hi = hi,
                Line = nameTok.Line,
                Column = nameTok.Column
            };
        }

        // Parses an expression in which `index` (the copy number 0..count-1 inside a
        // SPAWN) is visible: SPAWN color, speed, accel, speed_limit, turn, and the
        // bullet list index.
        private ConstExpr ParsePerCopyExpr()
        {
            _copyIndexInScope = true;
            try { return ParseConstExpr(); }
            finally { _copyIndexInScope = false; }
        }

        // `index`, `rand` and `randi` have fixed meanings, so no VAR, LOOP variable or
        // pattern parameter may take those names.
        private static void RejectReservedName(Token nameTok, string what)
        {
            if (nameTok.Lexeme == CopyIndexName)
                throw new ParseError(
                    $"'{CopyIndexName}' is reserved (it is the per-copy counter inside SPAWN) and can't be used as a {what} name",
                    nameTok.Line, nameTok.Column);
            if (nameTok.Lexeme == "rand" || nameTok.Lexeme == "randi")
                throw new ParseError(
                    $"'{nameTok.Lexeme}' is a built-in function and can't be used as a {what} name",
                    nameTok.Line, nameTok.Column);
        }

        // ---------- SEED ----------
        // seed_decl ::= 'SEED' [ '-' ] INTEGER ';'

        private SeedDeclNode ParseSeedDecl()
        {
            var kw = Expect(TokenType.KwSeed, "to start a SEED declaration");

            if (_seedDeclared)
                throw new ParseError("SEED is already declared", kw.Line, kw.Column);
            if (_randUsed)
                throw new ParseError("SEED must come before the first use of rand() or randi()", kw.Line, kw.Column);

            bool negative = Match(TokenType.Minus);
            var valueTok = Expect(TokenType.IntegerLiteral, "as the SEED value (a whole number)");
            Expect(TokenType.Semicolon, "to end SEED declaration");

            _seedDeclared = true;
            long value = (long)valueTok.NumericValue;
            return new SeedDeclNode { Value = negative ? -value : value, Line = kw.Line, Column = kw.Column };
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

            if (_declaredPatterns.ContainsKey(nameTok.Lexeme))
                throw new ParseError($"SPAWN_PATTERN '{nameTok.Lexeme}' is already declared", nameTok.Line, nameTok.Column);

            // Optional compile-time parameters: SPAWN_PATTERN ring(n, spd) { ... }
            var parameters = new List<string>();
            if (Match(TokenType.LParen))
            {
                if (!Check(TokenType.RParen))
                {
                    do
                    {
                        var paramTok = Expect(TokenType.Identifier, "as a parameter name");
                        RejectReservedName(paramTok, "parameter");
                        if (_declaredVars.Contains(paramTok.Lexeme))
                            throw new ParseError(
                                $"Parameter '{paramTok.Lexeme}' collides with an existing VAR of the same name",
                                paramTok.Line, paramTok.Column);
                        if (parameters.Contains(paramTok.Lexeme))
                            throw new ParseError($"Parameter '{paramTok.Lexeme}' is listed twice", paramTok.Line, paramTok.Column);
                        parameters.Add(paramTok.Lexeme);
                    } while (Match(TokenType.Comma));
                }
                Expect(TokenType.RParen, "to close the parameter list");
            }

            Expect(TokenType.LBrace, "to open SPAWN_PATTERN body");

            // Parameters are in scope for the whole body, exactly like LOOP variables.
            // Patterns only appear at top level, so _activeLoopVars is empty here and the
            // caller's LOOP variables can never leak into a pattern body.
            _activeLoopVars.AddRange(parameters);
            List<PatternStmt> body;
            try
            {
                body = ParsePatternStmtList();
            }
            finally
            {
                _activeLoopVars.RemoveRange(_activeLoopVars.Count - parameters.Count, parameters.Count);
            }
            Expect(TokenType.RBrace, "to close SPAWN_PATTERN body");

            // Registered only now, so the pattern can't CALL itself.
            _declaredPatterns[nameTok.Lexeme] = parameters.Count;

            return new SpawnPatternDefNode { Name = nameTok.Lexeme, Params = parameters, Body = body, Line = kw.Line, Column = kw.Column };
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
                TokenType.KwCall => ParseCallStmt(),
                _ => throw new ParseError(
                        $"Expected SPAWN, LOOP, PHASE, or CALL inside SPAWN_PATTERN, found {Current.Type}",
                        Current.Line, Current.Column)
            };
        }

        // call_stmt ::= 'CALL' IDENTIFIER '(' [ const_expr { ',' const_expr } ] ')' ';'

        private CallStmt ParseCallStmt()
        {
            var kw = Expect(TokenType.KwCall, "to start a CALL statement");
            var nameTok = Expect(TokenType.Identifier, "as the pattern name after CALL");

            if (!_declaredPatterns.TryGetValue(nameTok.Lexeme, out int arity))
                throw new ParseError(
                    $"CALL of undeclared SPAWN_PATTERN '{nameTok.Lexeme}' (a pattern must be declared earlier in the file)",
                    nameTok.Line, nameTok.Column);

            Expect(TokenType.LParen, "after the pattern name in CALL");
            var args = new List<ConstExpr>();
            if (!Check(TokenType.RParen))
            {
                do
                {
                    args.Add(ParseConstExpr());
                } while (Match(TokenType.Comma));
            }
            Expect(TokenType.RParen, "to close the CALL argument list");
            Expect(TokenType.Semicolon, "after CALL statement");

            if (args.Count != arity)
                throw new ParseError(
                    $"Pattern '{nameTok.Lexeme}' takes {arity} argument(s), got {args.Count}",
                    nameTok.Line, nameTok.Column);

            return new CallStmt { PatternName = nameTok.Lexeme, Args = args, Line = kw.Line, Column = kw.Column };
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

                bulletIndex = ParsePerCopyExpr();

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

            ConstExpr? angle = null, count = null, spread = null, delay = null;
            ConstExpr? color = null, speed = null, accel = null, speedLimit = null, turn = null;

            while (!Check(TokenType.RBrace))
            {
                var propTok = Expect(TokenType.Identifier,
                    "as a SPAWN property (angle/count/spread/delay/color/speed/accel/speed_limit/turn)");
                Expect(TokenType.Colon, "after SPAWN property name");

                switch (propTok.Lexeme)
                {
                    case "angle": angle = ParseConstExpr(); break;
                    case "count": count = ParseConstExpr(); break;
                    case "spread": spread = ParseConstExpr(); break;
                    case "delay": delay = ParseConstExpr(); break;
                    // These are evaluated once per bullet, so `index` (0..count-1) is
                    // visible inside them.
                    case "color": color = ParsePerCopyExpr(); break;
                    case "speed": speed = ParsePerCopyExpr(); break;
                    case "accel": accel = ParsePerCopyExpr(); break;
                    case "speed_limit": speedLimit = ParsePerCopyExpr(); break;
                    case "turn": turn = ParsePerCopyExpr(); break;
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
                Speed = speed,
                Accel = accel,
                SpeedLimit = speedLimit,
                Turn = turn,
                Line = kw.Line,
                Column = kw.Column
            };
        }

        // loop_stmt ::= 'LOOP' IDENTIFIER 'IN' const_expr 'TO' const_expr [ 'STEP' const_expr ] '{' { pattern_stmt } '}'
        // The bounds must evaluate to whole numbers at compile time; they are checked
        // (including "would never execute" and STEP 0) by the compiler, not here, because
        // they may depend on VARs and pattern parameters.

        private LoopStmt ParseLoopStmt()
        {
            var kw = Expect(TokenType.KwLoop, "to start a LOOP");
            var loopVarTok = Expect(TokenType.Identifier, "as LOOP variable name");

            // The bounds are parsed BEFORE the loop variable is added to scope, so a
            // bound can't refer to the variable it is defining.
            Expect(TokenType.KwIn, "after LOOP variable name");
            var from = ParseConstExpr();
            Expect(TokenType.KwTo, "after LOOP start bound");
            var to = ParseConstExpr();

            ConstExpr? step = null;
            if (Match(TokenType.KwStep))
                step = ParseConstExpr();

            // The loop variable is a compile-time-only binding, resolved
            // during unrolling — it is a separate namespace from VAR, not
            // added to _declaredVars. It IS pushed onto _activeLoopVars so
            // const_expr inside this LOOP's body (including nested
            // LOOPs/PHASEs) can reference it, e.g. `angle: i * 30deg`.
            RejectReservedName(loopVarTok, "LOOP variable");
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