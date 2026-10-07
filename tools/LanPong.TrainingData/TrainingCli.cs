using System.Globalization;

namespace LanPong.TrainingData;

internal static class TrainingCli
{
    private const ulong DefaultSeed = 20261007;

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                PrintHelp();
                return 0;
            }

            var values = ParseOptions(args.AsSpan(1));
            switch (args[0])
            {
                case "evaluate":
                    RejectUnknown(values, "output", "seed", "matches", "max-ticks");
                    TrainingDataRunner.EvaluateToFile(new EvaluationOptions(
                        Required(values, "output"),
                        ULong(values, "seed", DefaultSeed),
                        Int(values, "matches", 24),
                        Int(values, "max-ticks", 20_000)));
                    return 0;
                case "direct-evaluate":
                    RejectUnknown(values, "output", "seed", "matches", "max-ticks");
                    TrainingDataRunner.EvaluateDirectToFile(new EvaluationOptions(
                        Required(values, "output"),
                        ULong(values, "seed", DefaultSeed),
                        Int(values, "matches", 8),
                        Int(values, "max-ticks", 20_000)));
                    return 0;
                case "generate":
                    RejectUnknown(values, "output", "seed", "train", "validation", "test",
                        "gate-matches", "max-ticks", "sample-every", "behavior", "student-model");
                    var studentModel = Optional(values, "student-model");
                    var behavior = Optional(values, "behavior") ??
                        (studentModel is null ? "teacher" : "student");
                    TrainingDataRunner.Generate(new GenerationOptions(
                        Required(values, "output"),
                        ULong(values, "seed", DefaultSeed),
                        Int(values, "train", 48),
                        Int(values, "validation", 12),
                        Int(values, "test", 12),
                        Int(values, "gate-matches", 24),
                        Int(values, "max-ticks", 20_000),
                        Int(values, "sample-every", 9),
                        behavior, studentModel));
                    return 0;
                default:
                    throw new ArgumentException($"Unknown command '{args[0]}'. Use --help.");
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Training-data error: {error.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(ReadOnlySpan<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var flag = args[index];
            if (!flag.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException("Options must be --name value pairs. Use --help.");
            var name = flag[2..];
            if (!values.TryAdd(name, args[index + 1]))
                throw new ArgumentException($"Option --{name} was provided twice.");
        }
        return values;
    }

    private static void RejectUnknown(Dictionary<string, string> values, params string[] allowed)
    {
        foreach (var name in values.Keys)
            if (!allowed.Contains(name, StringComparer.Ordinal))
                throw new ArgumentException($"Unknown option --{name}.");
    }

    private static string Required(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && value.Length > 0
            ? value : throw new ArgumentException($"--{name} is required.");

    private static string? Optional(Dictionary<string, string> values, string name) =>
        values.GetValueOrDefault(name);

    private static int Int(Dictionary<string, string> values, string name, int defaultValue) =>
        values.TryGetValue(name, out var value)
            ? int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)
            : defaultValue;

    private static ulong ULong(Dictionary<string, string> values, string name, ulong defaultValue) =>
        values.TryGetValue(name, out var value)
            ? ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)
            : defaultValue;

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Exact-engine right-bot data and evaluation (60 fixed ticks/second).

            evaluate --output FILE [--seed 20261007] [--matches 24] [--max-ticks 20000]
              Compare Teacher and Simple as right policies against identical seeded
              left opponents. Output has paired per-match scores, wins, and seeds.

            direct-evaluate --output FILE [--seed 20261007] [--matches 8]
              [--max-ticks 20000]
              Diagnostic direct Teacher-vs-Simple duels with both side assignments,
              using the same seeded, legal opening disturbance for each pair.
              Capped games are reported, never counted as wins.

            generate --output DIRECTORY [--seed 20261007] [--train 48]
              [--validation 12] [--test 12] [--gate-matches 24]
              [--max-ticks 20000] [--sample-every 9]
              [--behavior teacher|simple|student] [--student-model FILE]
              Verify Teacher beats Simple on held-out scenarios before writing
              split JSONL labels, manifest.json, matches.json, evaluation.json.
              Student behavior loads an ONNX model with observation -> logits;
              visited student states still receive Teacher labels.
            """);
    }
}
