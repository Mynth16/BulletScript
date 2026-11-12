using System.Collections.Generic;
using System.Globalization;
using BulletScript.Compiler;

namespace BulletScript.EditorTools;

public sealed record PatternInfo(string Name, List<string> Params);

// A numeric literal that is safe to drive with a slider.
public sealed record LiteralSpot(Token Tok, string Key, NumericUnit Unit);

// Finds things in a token stream that the editor cares about. Works purely on the
// lexer's tokens so it can never disagree with the compiler about what is a literal.
public static class BsTokenScanner
{
    public static List<PatternInfo> FindPatterns(List<Token> t)
    {
        var result = new List<PatternInfo>();
        for (int i = 0; i + 1 < t.Count; i++)
        {
            if (t[i].Type != TokenType.KwSpawnPattern || t[i + 1].Type != TokenType.Identifier) continue;
            var info = new PatternInfo(t[i + 1].Lexeme, new List<string>());
            int j = i + 2;
            if (j < t.Count && t[j].Type == TokenType.LParen)
            {
                for (j++; j < t.Count && t[j].Type != TokenType.RParen && t[j].Type != TokenType.EOF; j++)
                    if (t[j].Type == TokenType.Identifier) info.Params.Add(t[j].Lexeme);
            }
            result.Add(info);
        }
        return result;
    }

    static bool IsNumber(TokenType ty)
        => ty is TokenType.IntegerLiteral or TokenType.FloatLiteral
              or TokenType.AngleLiteral or TokenType.DurationLiteral;

    // Only plain, stand-alone literals get a slider; anything inside a larger expression
    // (`1.5 + r * 0.75`) is left alone because moving the slider would be ambiguous.
    public static List<LiteralSpot> FindLiterals(List<Token> t)
    {
        var result = new List<LiteralSpot>();
        for (int i = 1; i + 1 < t.Count; i++)
        {
            if (!IsNumber(t[i].Type)) continue;
            var prev = t[i - 1];
            var next = t[i + 1];
            string key = null;

            if (prev.Type == TokenType.Colon && i >= 2 && t[i - 2].Type == TokenType.Identifier
                && next.Type == TokenType.Semicolon)
                key = t[i - 2].Lexeme.ToLowerInvariant();                       // speed: 2;
            else if (prev.Type is TokenType.KwIn or TokenType.KwTo
                     && next.Type is TokenType.KwTo or TokenType.LBrace or TokenType.KwStep)
                key = "loop";                                                    // LOOP i IN 1 TO 5 {
            else if (prev.Type == TokenType.KwAfter && next.Type == TokenType.LBrace)
                key = "after";                                                   // PHASE p AFTER 400ms {
            else if (prev.Type == TokenType.Equals && i >= 3 && t[i - 2].Type == TokenType.Identifier
                     && t[i - 3].Type == TokenType.KwVar && next.Type == TokenType.Semicolon)
                key = "var";                                                     // VAR n = 12;

            if (key != null) result.Add(new LiteralSpot(t[i], key, t[i].Unit));
        }
        return result;
    }

    // ---- unit handling. Slider values are in base units: seconds / degrees / plain. ----

    const double Fps = 60.0;

    public static string Suffix(NumericUnit u) => u switch
    {
        NumericUnit.Degrees => "deg",
        NumericUnit.Radians => "rad",
        NumericUnit.Seconds => "s",
        NumericUnit.Milliseconds => "ms",
        NumericUnit.Frames => "f",
        _ => "",
    };

    // The number part of a literal such as "0.5s" or "45deg" (the lexer's Lexeme includes the unit).
    public static double ParseNumber(string lexeme, NumericUnit u)
    {
        string s = lexeme.Substring(0, lexeme.Length - Suffix(u).Length);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    public static double ToBase(double v, NumericUnit u) => u switch
    {
        NumericUnit.Radians => v * 180.0 / System.Math.PI,
        NumericUnit.Milliseconds => v / 1000.0,
        NumericUnit.Frames => v / Fps,
        _ => v,
    };

    public static double FromBase(double v, NumericUnit u) => u switch
    {
        NumericUnit.Radians => v * System.Math.PI / 180.0,
        NumericUnit.Milliseconds => v * 1000.0,
        NumericUnit.Frames => v * Fps,
        _ => v,
    };

    public static string FormatNumber(double v)
    {
        string s = v.ToString("0.####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    public static bool IsDuration(NumericUnit u) => u is NumericUnit.Seconds or NumericUnit.Milliseconds or NumericUnit.Frames;
    public static bool IsAngle(NumericUnit u) => u is NumericUnit.Degrees or NumericUnit.Radians;
}
