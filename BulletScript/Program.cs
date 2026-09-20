using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BulletScript.Compiler;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: bsc <input.bs> [-o <output.json>] [--fps <n>]");
    return 1;
}

string inputPath = args[0];
string? outputPath = null;
double fps = 60.0;

for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-o":
            if (i + 1 >= args.Length) { Console.Error.WriteLine("-o requires a path argument"); return 1; }
            outputPath = args[++i];
            break;
        case "--fps":
            if (i + 1 >= args.Length || !double.TryParse(args[i + 1], out fps))
            {
                Console.Error.WriteLine("--fps requires a numeric argument");
                return 1;
            }
            i++;
            break;
        default:
            Console.Error.WriteLine($"Unrecognized argument '{args[i]}'");
            return 1;
    }
}

outputPath ??= Path.ChangeExtension(inputPath, ".json");

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Input file not found: {inputPath}");
    return 1;
}

string source = File.ReadAllText(inputPath);

try
{
    var compiled = BulletScriptCompiler.CompileSource(source, fps);

    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    string json = JsonSerializer.Serialize(compiled, jsonOptions);
    File.WriteAllText(outputPath, json);

    int totalInstructions = 0;
    foreach (var kv in compiled.Patterns) totalInstructions += kv.Value.Count;

    Console.WriteLine(
        $"Compiled {inputPath} -> {outputPath} " +
        $"({compiled.Bullets.Count} bullets, {compiled.SpawnPoints.Count} spawn points, " +
        $"{compiled.Patterns.Count} patterns, {totalInstructions} instructions total)");

    return 0;
}
catch (LexError ex)
{
    Console.Error.WriteLine($"{inputPath}({ex.Line},{ex.Column}): lex error: {ex.Message}");
    return 1;
}
catch (ParseError ex)
{
    Console.Error.WriteLine($"{inputPath}({ex.Line},{ex.Column}): parse error: {ex.Message}");
    return 1;
}
catch (CompileError ex)
{
    Console.Error.WriteLine($"{inputPath}({ex.Line},{ex.Column}): compile error: {ex.Message}");
    return 1;
}