namespace BulletScript.Compiler
{
    /// <summary>
    /// Single entry point for the full pipeline (lex -> parse -> compile), so the
    /// console app and the Godot runtime share exactly the same code path.
    /// Throws LexError / ParseError / CompileError on invalid source.
    /// </summary>
    public static class BulletScriptCompiler
    {
        public static CompiledProgram CompileSource(string source, double framesPerSecond = 60.0, int maxInstructions = Compiler.DefaultMaxInstructions)
        {
            var tokens = new Lexer(source).Tokenize();
            var program = new Parser(tokens).ParseProgram();
            return new Compiler(framesPerSecond: framesPerSecond, maxInstructions: maxInstructions).Compile(program);
        }
    }
}