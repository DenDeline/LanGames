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
                case "evaluate-model":
                    RejectUnknown(values, "output", "student-model", "seed", "matches",
                        "max-ticks", "backend");
                    TrainingDataRunner.EvaluateModelToFile(new ModelEvaluationOptions(
                        Required(values, "output"),
                        Optional(values, "student-model"),
                        ULong(values, "seed", DefaultSeed),
                        Int(values, "matches", 24),
                        Int(values, "max-ticks", 20_000),
                        Optional(values, "backend") ?? "offline"));
                    return 0;
                case "direct-evaluate-model":
                    RejectUnknown(values, "output", "student-model", "seed", "matches",
                        "max-ticks", "countdown-mode", "backend");
                    TrainingDataRunner.EvaluateModelDirectToFile(new DirectModelEvaluationOptions(
                        Required(values, "output"),
                        Optional(values, "student-model"),
                        ULong(values, "seed", DefaultSeed),
                        Int(values, "matches", 8),
                        Int(values, "max-ticks", 20_000),
                        Optional(values, "countdown-mode") ?? "seeded-targets",
                        Optional(values, "backend") ?? "offline"));
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
              Compare Teacher and the tracker baseline as right policies against identical seeded
              left opponents. Output has paired per-match scores, wins, and seeds.

            direct-evaluate --output FILE [--seed 20261007] [--matches 8]
              [--max-ticks 20000]
              Diagnostic direct Teacher-vs-tracker duels with both side assignments,
              using the same seeded, legal opening disturbance for each pair.
              Capped games are reported, never counted as wins.

            evaluate-model --output FILE --student-model MODEL
              [--backend offline|production] [--seed 20261007]
              [--matches 24] [--max-ticks 20000]
              Compare an exported ONNX model and the tracker baseline as right policies
              against paired seeded left opponents. Report per-profile wins,
              score margins, Wilson intervals, model SHA-256, and match results.
              Production uses the strict supported ONNX policy with the frozen
              model at MODEL, verifies its hash, and fails on load or inference errors.

            direct-evaluate-model --output FILE --student-model MODEL
              [--backend offline|production]
              [--seed 20261007] [--matches 8] [--max-ticks 20000]
              [--countdown-mode seeded-targets|policies]
              Direct student-vs-tracker duels, one game per side assignment for
              each seed. The default moves both paddles toward seeded targets
              between points; policies lets each bot control its countdown.
              Report completed and capped games, side wins, Wilson interval,
              model SHA-256, protocol, and match results. Production uses the
              strict supported ONNX policy with MODEL and fails on model errors.

            generate --output DIRECTORY [--seed 20261007] [--train 48]
              [--validation 12] [--test 12] [--gate-matches 24]
              [--max-ticks 20000] [--sample-every 9]
              [--behavior teacher|simple|student] [--student-model FILE]
              Verify Teacher beats the tracker baseline on held-out scenarios before writing
              split JSONL labels, manifest.json, matches.json, evaluation.json.
              Student behavior loads an ONNX model with observation -> logits;
              visited student states still receive Teacher labels.

            Historical reports and --behavior simple retain the label "simple"
            for the same calibrated tracker baseline and reproducible datasets.
            """);
    }
}
