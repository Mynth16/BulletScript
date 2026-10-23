using System;
using System.Collections.Generic;
using System.Text;

namespace BulletScript.Compiler
{
    public enum TokenType
    {
        // Literals
        Identifier,
        IntegerLiteral,
        FloatLiteral,
        AngleLiteral,      // e.g. 45deg, 1.57rad
        DurationLiteral,   // e.g. 0.5s, 500ms, 30f
        StringLiteral,
        ColorLiteral,      // e.g. #FF3030, #f30 (NumericValue holds packed 0xRRGGBB)

        // Keywords
        KwBullet,
        KwSpawnPattern,
        KwSpawnPoint,
        KwVar,
        KwLoop,
        KwPhase,
        KwIn,
        KwTo,
        KwStep,
        KwTrue,
        KwFalse,
        KwSpawn,
        KwAt,
        KwAfter,

        // Operators / punctuation
        Plus, Minus, Star, Slash, Percent,
        Equals,
        EqEq, NotEq, Lt, Gt, LtEq, GtEq,   // comparisons: evaluate to 1 or 0 at compile time
        LParen, RParen,
        LBrace, RBrace,
        LBracket, RBracket,
        Comma, Colon, Semicolon,

        EOF
    }

    public enum NumericUnit
    {
        None,
        Degrees,
        Radians,
        Seconds,
        Milliseconds,
        Frames
    }

    /// <summary>
    /// A single lexical token. NumericValue/Unit are only populated for
    /// numeric literal token types; StringValue is only populated for
    /// StringLiteral and Identifier/keyword tokens (the raw lexeme).
    /// </summary>
    public readonly struct Token
    {
        public TokenType Type { get; }
        public string Lexeme { get; }
        public double NumericValue { get; }
        public NumericUnit Unit { get; }
        public int Line { get; }
        public int Column { get; }

        public Token(TokenType type, string lexeme, double numericValue, NumericUnit unit, int line, int column)
        {
            Type = type;
            Lexeme = lexeme;
            NumericValue = numericValue;
            Unit = unit;
            Line = line;
            Column = column;
        }

        public override string ToString() =>
            Unit != NumericUnit.None
                ? $"{Type}({NumericValue}{UnitSuffix(Unit)}) @{Line}:{Column}"
                : $"{Type}('{Lexeme}') @{Line}:{Column}";

        private static string UnitSuffix(NumericUnit u) => u switch
        {
            NumericUnit.Degrees => "deg",
            NumericUnit.Radians => "rad",
            NumericUnit.Seconds => "s",
            NumericUnit.Milliseconds => "ms",
            NumericUnit.Frames => "f",
            _ => ""
        };
    }

    public class LexError : Exception
    {
        public int Line { get; }
        public int Column { get; }

        public LexError(string message, int line, int column)
            : base($"Lex error at {line}:{column}: {message}")
        {
            Line = line;
            Column = column;
        }
    }

    /// <summary>
    /// Hand-rolled single-pass lexer for BulletScript. Converts source text
    /// into a flat token list; the parser (recursive descent) consumes this
    /// list directly. No lookahead beyond a handful of characters is needed
    /// anywhere in this lexer.
    /// </summary>
    public class Lexer
    {
        private static readonly Dictionary<string, TokenType> Keywords = new()
        {
            ["BULLET"] = TokenType.KwBullet,
            ["SPAWN_PATTERN"] = TokenType.KwSpawnPattern,
            ["SPAWN_POINT"] = TokenType.KwSpawnPoint,
            ["VAR"] = TokenType.KwVar,
            ["LOOP"] = TokenType.KwLoop,
            ["PHASE"] = TokenType.KwPhase,
            ["IN"] = TokenType.KwIn,
            ["TO"] = TokenType.KwTo,
            ["STEP"] = TokenType.KwStep,
            ["TRUE"] = TokenType.KwTrue,
            ["FALSE"] = TokenType.KwFalse,
            ["SPAWN"] = TokenType.KwSpawn,
            ["AT"] = TokenType.KwAt,
            ["AFTER"] = TokenType.KwAfter,
        };

        // Units are checked longest-first so "ms" isn't swallowed as "m" + "s".
        private static readonly (string suffix, NumericUnit unit)[] UnitSuffixes =
        {
            ("deg", NumericUnit.Degrees),
            ("rad", NumericUnit.Radians),
            ("ms",  NumericUnit.Milliseconds),
            ("s",   NumericUnit.Seconds),
            ("f",   NumericUnit.Frames),
        };

        private readonly string _src;
        private int _pos;
        private int _line = 1;
        private int _col = 1;

        public Lexer(string source)
        {
            _src = source;
        }

        public List<Token> Tokenize()
        {
            var tokens = new List<Token>();
            Token t;
            do
            {
                t = NextToken();
                tokens.Add(t);
            } while (t.Type != TokenType.EOF);
            return tokens;
        }

        private char Current => _pos < _src.Length ? _src[_pos] : '\0';
        private char Peek(int ahead = 1) => _pos + ahead < _src.Length ? _src[_pos + ahead] : '\0';

        private char Advance()
        {
            char c = _src[_pos++];
            if (c == '\n') { _line++; _col = 1; }
            else { _col++; }
            return c;
        }

        private void SkipWhitespaceAndComments()
        {
            while (true)
            {
                if (Current is ' ' or '\t' or '\r' or '\n')
                {
                    Advance();
                }
                else if (Current == '/' && Peek() == '/')
                {
                    while (Current != '\n' && Current != '\0') Advance();
                }
                else
                {
                    break;
                }
            }
        }

        private Token NextToken()
        {
            SkipWhitespaceAndComments();

            int startLine = _line, startCol = _col;

            if (_pos >= _src.Length)
                return new Token(TokenType.EOF, "", 0, NumericUnit.None, startLine, startCol);

            char c = Current;

            if (char.IsDigit(c)) return ScanNumber(startLine, startCol);
            if (char.IsLetter(c) || c == '_') return ScanIdentifierOrKeyword(startLine, startCol);
            if (c == '"') return ScanString(startLine, startCol);
            if (c == '#') return ScanColor(startLine, startCol);

            // Comparison operators (checked before the single-character '=' below).
            if ((c == '=' || c == '!') && Peek() == '=')
            {
                Advance(); Advance();
                return new Token(c == '=' ? TokenType.EqEq : TokenType.NotEq, c + "=", 0, NumericUnit.None, startLine, startCol);
            }
            if (c == '<' || c == '>')
            {
                Advance();
                bool orEqual = Current == '=';
                if (orEqual) Advance();
                TokenType cmp = c == '<'
                    ? (orEqual ? TokenType.LtEq : TokenType.Lt)
                    : (orEqual ? TokenType.GtEq : TokenType.Gt);
                return new Token(cmp, orEqual ? c + "=" : c.ToString(), 0, NumericUnit.None, startLine, startCol);
            }

            // Single-character operators / punctuation.
            Advance();
            TokenType type = c switch
            {
                '+' => TokenType.Plus,
                '-' => TokenType.Minus,
                '*' => TokenType.Star,
                '/' => TokenType.Slash,
                '%' => TokenType.Percent,
                '=' => TokenType.Equals,
                '(' => TokenType.LParen,
                ')' => TokenType.RParen,
                '{' => TokenType.LBrace,
                '}' => TokenType.RBrace,
                '[' => TokenType.LBracket,
                ']' => TokenType.RBracket,
                ',' => TokenType.Comma,
                ':' => TokenType.Colon,
                ';' => TokenType.Semicolon,
                _ => throw new LexError($"Unexpected character '{c}'", startLine, startCol)
            };
            return new Token(type, c.ToString(), 0, NumericUnit.None, startLine, startCol);
        }

        // #RGB or #RRGGBB. Packed into NumericValue as 0xRRGGBB.
        private Token ScanColor(int startLine, int startCol)
        {
            Advance(); // '#'
            var sb = new StringBuilder();
            while (Uri.IsHexDigit(Current)) sb.Append(Advance());

            if (sb.Length != 3 && sb.Length != 6)
                throw new LexError($"Color literal must be #RGB or #RRGGBB (got '#{sb}')", startLine, startCol);

            if (char.IsLetterOrDigit(Current) || Current == '_')
                throw new LexError($"Invalid character '{Current}' in color literal '#{sb}'", startLine, startCol);

            string hex = sb.ToString();
            if (hex.Length == 3)
                hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";

            uint rgb = Convert.ToUInt32(hex, 16);
            return new Token(TokenType.ColorLiteral, "#" + sb, rgb, NumericUnit.None, startLine, startCol);
        }

        private Token ScanNumber(int startLine, int startCol)
        {
            var sb = new StringBuilder();
            bool isFloat = false;

            while (char.IsDigit(Current)) sb.Append(Advance());

            if (Current == '.' && char.IsDigit(Peek()))
            {
                isFloat = true;
                sb.Append(Advance()); // '.'
                while (char.IsDigit(Current)) sb.Append(Advance());
            }

            double value = double.Parse(sb.ToString(), System.Globalization.CultureInfo.InvariantCulture);

            // Check for a unit suffix immediately following (no whitespace).
            NumericUnit unit = NumericUnit.None;
            string matchedSuffix = null;
            foreach (var (suffix, u) in UnitSuffixes)
            {
                if (MatchesSuffixHere(suffix))
                {
                    unit = u;
                    matchedSuffix = suffix;
                    break;
                }
            }

            if (matchedSuffix != null)
            {
                for (int i = 0; i < matchedSuffix.Length; i++) Advance();

                // Reject things like "5degrees" — suffix must not be followed
                // by another identifier character, or it wasn't really a unit.
                if (char.IsLetterOrDigit(Current) || Current == '_')
                    throw new LexError($"Invalid numeric suffix near '{sb}{matchedSuffix}{Current}'", startLine, startCol);

                TokenType litType = unit is NumericUnit.Degrees or NumericUnit.Radians
                    ? TokenType.AngleLiteral
                    : TokenType.DurationLiteral;

                return new Token(litType, sb.ToString() + matchedSuffix, value, unit, startLine, startCol);
            }

            // No unit: plain integer or float. Reject a bare trailing letter
            // (e.g. "5x") which is neither a valid suffix nor a separate token.
            if (char.IsLetter(Current) || Current == '_')
                throw new LexError($"Unexpected identifier character immediately after number literal", startLine, startCol);

            return new Token(
                isFloat ? TokenType.FloatLiteral : TokenType.IntegerLiteral,
                sb.ToString(), value, NumericUnit.None, startLine, startCol);
        }

        private bool MatchesSuffixHere(string suffix)
        {
            for (int i = 0; i < suffix.Length; i++)
            {
                if (Peek(i) != suffix[i]) return false;
            }
            return true;
        }

        private Token ScanIdentifierOrKeyword(int startLine, int startCol)
        {
            var sb = new StringBuilder();
            while (char.IsLetterOrDigit(Current) || Current == '_') sb.Append(Advance());

            string text = sb.ToString();
            if (Keywords.TryGetValue(text, out var kwType))
                return new Token(kwType, text, 0, NumericUnit.None, startLine, startCol);

            return new Token(TokenType.Identifier, text, 0, NumericUnit.None, startLine, startCol);
        }

        private Token ScanString(int startLine, int startCol)
        {
            Advance(); // opening quote
            var sb = new StringBuilder();
            while (Current != '"')
            {
                if (Current == '\0' || Current == '\n')
                    throw new LexError("Unterminated string literal", startLine, startCol);
                sb.Append(Advance());
            }
            Advance(); // closing quote
            return new Token(TokenType.StringLiteral, sb.ToString(), 0, NumericUnit.None, startLine, startCol);
        }
    }
}