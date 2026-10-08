using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanPong;
using LanPong.Bots.Inference;
using LanPong.Bots.Strategies;
using static LanPong.GameConstants;

namespace LanPong.TrainingData;

internal sealed record GenerationOptions(string OutputDirectory, ulong Seed, int TrainMatches,
    int ValidationMatches, int TestMatches, int GateMatches, int MaxTicks,
    int SampleEveryTicks, string Behavior, string? StudentModel);

internal sealed record EvaluationOptions(string OutputFile, ulong Seed, int Matches, int MaxTicks);

internal sealed record ModelEvaluationOptions(string OutputFile, string? StudentModel,
    ulong Seed, int Matches, int MaxTicks, string Backend = "offline");

internal sealed record DirectModelEvaluationOptions(string OutputFile, string? StudentModel,
    ulong Seed, int Matches, int MaxTicks, string CountdownMode, string Backend = "offline");

internal sealed record TrainingRow(string MatchId, long Tick, float[] Observation,
    int TeacherClass, int BehaviorAxis);

internal sealed record MatchMetrics(string MatchId, string Split, string Seed, string LeftPolicy,
    string RightPolicy, int Ticks, int LeftScore, int RightScore, int Serves, int Goals,
    int LeftPaddleHits, int RightPaddleHits, int WallBounces, int Rallies, int Rows)
{
    public bool RightWon => RightScore == WinningScore;
    public int RightScoreMargin => RightScore - LeftScore;
}

internal sealed record EvaluationPair(int ScenarioIndex, string Seed, string LeftPolicy,
    MatchMetrics Teacher, MatchMetrics Simple)
{
    public int ScoreImprovement => Teacher.RightScoreMargin - Simple.RightScoreMargin;
    // A completed 7-point race decides exact score ties only when the time
    // difference is at least one second (60 authoritative ticks).
    public int RaceWinner => Teacher.RightWon != Simple.RightWon
        ? Teacher.RightWon ? 1 : -1
        : ScoreImprovement != 0 ? Math.Sign(ScoreImprovement)
        : Math.Abs(Teacher.Ticks - Simple.Ticks) < SimulationTicksPerSecond ? 0
        : Teacher.RightWon
            ? Teacher.Ticks < Simple.Ticks ? 1 : -1
            : Teacher.Ticks > Simple.Ticks ? 1 : -1;
}

internal sealed record EvaluationReport(int ObservationVersion, string SeedPolicy,
    string MasterSeed, int ScenarioCount, int TeacherWins, int SimpleWins,
    int PairedBetter, int PairedWorse, int PairedTies, int PairedScoreImprovement,
    int TeacherRaceWins, int SimpleRaceWins, int RaceTies,
    int TeacherTotalTicks, int SimpleTotalTicks,
    double TeacherWinRate, double SimpleWinRate, double TeacherRaceWinRate,
    int PairedScoreDecisiveCount, double TeacherPairedScoreWinRate,
    double TeacherPairedScoreWilson95Lower, double TeacherPairedScoreWilson95Upper,
    bool Passed,
    EvaluationPair[] Pairs);

internal sealed record DirectDuelMatch(int ScenarioIndex, string Seed, string TeacherSide,
    bool Completed, int Ticks, int LeftScore, int RightScore)
{
    public bool TeacherWon => Completed && (TeacherSide == "right"
        ? RightScore == WinningScore : LeftScore == WinningScore);
}

internal sealed record DirectDuelReport(string MasterSeed, int ScenarioCount,
    int OpeningDisturbanceTicks, int TickCap, int CompletedMatches,
    int TeacherWins, int SimpleWins, int CappedMatches,
    double TeacherWinRateCompleted, double TeacherWilson95Lower,
    double TeacherWilson95Upper, DirectDuelMatch[] Matches);

// Historical baseline labels and fallback fields stay in the report schema for
// artifact comparison. Strict evaluation always writes false/0/null or fails.
internal sealed record ModelEvaluationPair(int ScenarioIndex, string Seed, string LeftPolicy,
    MatchMetrics Student, MatchMetrics Simple, bool StudentFallbackActive,
    string? StudentFallbackReason)
{
    public int PairedScoreImprovement => Student.RightScoreMargin - Simple.RightScoreMargin;
}

internal sealed record ModelProfileSummary(string LeftPolicy, int ScenarioCount,
    int StudentWins, int SimpleWins, int StudentScoreMargin, int SimpleScoreMargin,
    int PairedBetter, int PairedWorse, int PairedTies,
    double StudentWinRate, double StudentWinWilson95Lower, double StudentWinWilson95Upper,
    double StudentPairedScoreWinWilson95Lower, double StudentPairedScoreWinWilson95Upper);

internal sealed record ModelEvaluationReport(int ObservationVersion, string SeedPolicy,
    string MasterSeed, string Backend, string? StudentModelSha256,
    bool FallbackActiveAtLoad, int StudentFallbackMatches, string? FallbackReason,
    int ScenarioCount, int MaxTicks,
    int StudentWins, int SimpleWins, int StudentScoreMargin, int SimpleScoreMargin,
    int PairedBetter, int PairedWorse, int PairedTies,
    double StudentWinRate, double SimpleWinRate,
    double StudentWinWilson95Lower, double StudentWinWilson95Upper,
    double StudentPairedScoreWinWilson95Lower, double StudentPairedScoreWinWilson95Upper,
    ModelProfileSummary[] Profiles, ModelEvaluationPair[] Pairs)
{
    public bool UsedModelThroughout => StudentModelSha256 is not null &&
        !FallbackActiveAtLoad && StudentFallbackMatches == 0;
}

internal sealed record DirectModelDuelMatch(int ScenarioIndex, string Seed,
    string StudentSide, bool Completed, int Ticks, int LeftScore, int RightScore,
    string OpeningCheckpointHash, string PlayingTrajectoryHash,
    bool StudentFallbackActive, string? StudentFallbackReason)
{
    public bool StudentWon => Completed && (StudentSide == "right"
        ? RightScore == WinningScore : LeftScore == WinningScore);
}

internal sealed record DirectModelDuelReport(string MasterSeed, string Backend,
    string? StudentModelSha256, bool FallbackActiveAtLoad, int FallbackGames,
    string? FallbackReason,
    string SeedPolicy, string OpeningProtocol, string CountdownMode,
    string CountdownProtocol,
    int ScenarioCount, int ScheduledMatches, int OpeningDisturbanceTicks,
    int TickCap, int CompletedMatches,
    int StudentWins, int SimpleWins, int CappedMatches, int StudentRightCompleted,
    int StudentRightWins, int StudentLeftCompleted, int StudentLeftWins,
    int DistinctOpeningCheckpoints, int DistinctStudentRightTrajectories,
    int DistinctStudentLeftTrajectories,
    double StudentWinRateCompleted, double StudentWilson95Lower,
    double StudentWilson95Upper, double StudentWinRateScheduled,
    double StudentScheduledWilson95Lower, double StudentScheduledWilson95Upper,
    string UncertaintyNote, DirectModelDuelMatch[] Matches)
{
    public bool UsedModelThroughout => StudentModelSha256 is not null &&
        !FallbackActiveAtLoad && FallbackGames == 0;
}

internal sealed record SplitSummary(string Name, int MatchCount, int RowCount,
    string[] MatchIds, string[] MatchSeeds);

internal sealed record GenerationManifest(int ObservationVersion, string[] FeatureNames,
    string InputName, string OutputName, int[] ClassToAxis, int InferenceCadenceTicks,
    int SampleEveryTicks, double FixedStepSeconds, string SeedPolicy, string MasterSeed,
    string Behavior, string? StudentModelSha256, int MaxTicks,
    SplitSummary[] Splits, string[] LeftPolicies,
    string EvaluationFile, string MatchMetricsFile);

internal static class TrainingDataRunner
{
    private const string SeedPolicy = "splitmix64-v1: seed = mix(master, splitId, matchIndex); " +
        "splitId 0=train, 1=validation, 2=test, 3=held-out-evaluation";
    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static EvaluationReport Evaluate(ulong seed, int scenarioCount, int maxTicks)
    {
        ValidateCount(scenarioCount, nameof(scenarioCount));
        ValidateCount(maxTicks, nameof(maxTicks));
        var pairs = new EvaluationPair[scenarioCount];
        var teacherWins = 0;
        var simpleWins = 0;
        var pairedBetter = 0;
        var pairedWorse = 0;
        var pairedTies = 0;
        var scoreImprovement = 0;
        var teacherRaceWins = 0;
        var simpleRaceWins = 0;
        var raceTies = 0;
        var teacherTotalTicks = 0;
        var simpleTotalTicks = 0;

        for (var index = 0; index < scenarioCount; index++)
        {
            var matchSeed = StableRandom.DeriveSeed(seed, 3, index);
            var matchId = $"eval-{index:D5}";
            var teacher = RunMatch(matchId, "evaluation", matchSeed, index, "teacher",
                maxTicks, 0, null, null);
            var simple = RunMatch(matchId, "evaluation", matchSeed, index, "simple",
                maxTicks, 0, null, null);
            pairs[index] = new EvaluationPair(index, matchSeed.ToString(),
                LeftPolicyProfile.ForMatch(index).Name, teacher, simple);
            teacherWins += teacher.RightWon ? 1 : 0;
            simpleWins += simple.RightWon ? 1 : 0;
            teacherTotalTicks += teacher.Ticks;
            simpleTotalTicks += simple.Ticks;
            var delta = pairs[index].ScoreImprovement;
            scoreImprovement += delta;
            if (delta > 0) pairedBetter++;
            else if (delta < 0) pairedWorse++;
            else pairedTies++;
            var raceWinner = pairs[index].RaceWinner;
            if (raceWinner > 0) teacherRaceWins++;
            else if (raceWinner < 0) simpleRaceWins++;
            else raceTies++;
        }

        // Race time is diagnostic only. Label export requires completed
        // held-out scenarios with an actual paired score advantage, and the
        // score comparison's Wilson lower bound above one half.
        var scoreDecisive = pairedBetter + pairedWorse;
        var (wilsonLower, wilsonUpper) = Wilson95(pairedBetter, scoreDecisive);
        var passed = scenarioCount >= 24 && scoreDecisive >= 12 &&
            teacherWins >= simpleWins && scoreImprovement > 0 && wilsonLower > 0.5;
        return new EvaluationReport(RightBotObservationV1.Version, SeedPolicy,
            seed.ToString(), scenarioCount, teacherWins, simpleWins,
            pairedBetter, pairedWorse, pairedTies, scoreImprovement,
            teacherRaceWins, simpleRaceWins, raceTies,
            teacherTotalTicks, simpleTotalTicks,
            teacherWins / (double)scenarioCount, simpleWins / (double)scenarioCount,
            teacherRaceWins / (double)scenarioCount,
            scoreDecisive, scoreDecisive == 0 ? 0 : pairedBetter / (double)scoreDecisive,
            wilsonLower, wilsonUpper,
            passed, pairs);
    }

    public static void EvaluateToFile(EvaluationOptions options)
    {
        var report = Evaluate(options.Seed, options.Matches, options.MaxTicks);
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.OutputFile))!;
        Directory.CreateDirectory(directory);
        WriteJson(options.OutputFile, report);
        Console.WriteLine($"Teacher {report.TeacherWins}/{report.ScenarioCount}, " +
            $"Simple {report.SimpleWins}/{report.ScenarioCount}; " +
            $"paired scores {report.PairedBetter}:{report.PairedWorse}:" +
            $"{report.PairedTies}, Wilson lower {report.TeacherPairedScoreWilson95Lower:F3}; " +
            $"races {report.TeacherRaceWins}:{report.SimpleRaceWins}:" +
            $"{report.RaceTies}, point improvement {report.PairedScoreImprovement}; " +
            $"gate {(report.Passed ? "passed" : "failed")}. Report: {options.OutputFile}");
    }

    public static void EvaluateDirectToFile(EvaluationOptions options)
    {
        ValidateCount(options.Matches, nameof(options.Matches));
        ValidateCount(options.MaxTicks, nameof(options.MaxTicks));
        const int openingTicks = 120;
        var matches = new DirectDuelMatch[options.Matches * 2];
        for (var index = 0; index < options.Matches; index++)
        {
            var seed = StableRandom.DeriveSeed(options.Seed, 4, index);
            matches[index * 2] = RunDirectDuel(index, seed, true, openingTicks, options.MaxTicks);
            matches[index * 2 + 1] = RunDirectDuel(index, seed, false, openingTicks, options.MaxTicks);
        }
        var completed = matches.Count(match => match.Completed);
        var teacherWins = matches.Count(match => match.TeacherWon);
        var simpleWins = completed - teacherWins;
        var (wilsonLower, wilsonUpper) = Wilson95(teacherWins, completed);
        var report = new DirectDuelReport(options.Seed.ToString(), options.Matches,
            openingTicks, options.MaxTicks, completed,
            teacherWins, simpleWins, matches.Length - completed,
            completed == 0 ? 0 : teacherWins / (double)completed,
            wilsonLower, wilsonUpper, matches);
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.OutputFile))!;
        Directory.CreateDirectory(directory);
        WriteJson(options.OutputFile, report);
        Console.WriteLine($"Direct side-swapped duels: Teacher {report.TeacherWins}, " +
            $"Simple {report.SimpleWins}, capped {report.CappedMatches}/{matches.Length}, " +
            $"Wilson lower {report.TeacherWilson95Lower:F3}. " +
            $"Report: {options.OutputFile}");
    }

    public static ModelEvaluationReport EvaluateModel(string? modelPath, ulong seed,
        int scenarioCount, int maxTicks, string backend = "offline")
    {
        ValidateCount(scenarioCount, nameof(scenarioCount));
        ValidateCount(maxTicks, nameof(maxTicks));
        using var student = EvaluatedModelPolicy.Create(backend, modelPath);
        var pairs = new ModelEvaluationPair[scenarioCount];
        for (var index = 0; index < scenarioCount; index++)
        {
            // Match seed and profile are identical on both runs. The left
            // policy's random disturbance is keyed by absolute tick, so
            // diverging trajectories cannot consume different random streams.
            var matchSeed = StableRandom.DeriveSeed(seed, 3, index);
            var matchId = $"model-eval-{index:D5}";
            var studentMatch = RunMatch(matchId, "model-evaluation", matchSeed,
                index, "student", maxTicks, 0, null, student);
            var simpleMatch = RunMatch(matchId, "model-evaluation", matchSeed,
                index, "simple", maxTicks, 0, null, null);
            pairs[index] = new ModelEvaluationPair(index, matchSeed.ToString(),
                LeftPolicyProfile.ForMatch(index).Name, studentMatch, simpleMatch,
                false, null);
        }

        var profileSummaries = LeftPolicyProfile.All
            .Select(profile => SummarizeProfile(profile.Name,
                pairs.Where(pair => pair.LeftPolicy == profile.Name).ToArray()))
            .Where(summary => summary.ScenarioCount > 0).ToArray();
        var studentWins = pairs.Count(pair => pair.Student.RightWon);
        var simpleWins = pairs.Count(pair => pair.Simple.RightWon);
        var studentMargin = pairs.Sum(pair => pair.Student.RightScoreMargin);
        var simpleMargin = pairs.Sum(pair => pair.Simple.RightScoreMargin);
        var better = pairs.Count(pair => pair.PairedScoreImprovement > 0);
        var worse = pairs.Count(pair => pair.PairedScoreImprovement < 0);
        var ties = scenarioCount - better - worse;
        var (winLower, winUpper) = Wilson95(studentWins, scenarioCount);
        var (pairedLower, pairedUpper) = Wilson95(better, better + worse);
        return new ModelEvaluationReport(RightBotObservationV1.Version, SeedPolicy,
            seed.ToString(), student.Backend, student.ModelSha256,
            false, 0, null, scenarioCount, maxTicks,
            studentWins, simpleWins, studentMargin, simpleMargin,
            better, worse, ties, studentWins / (double)scenarioCount,
            simpleWins / (double)scenarioCount, winLower, winUpper,
            pairedLower, pairedUpper, profileSummaries, pairs);
    }

    public static void EvaluateModelToFile(ModelEvaluationOptions options)
    {
        var report = EvaluateModel(options.StudentModel, options.Seed,
            options.Matches, options.MaxTicks, options.Backend);
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.OutputFile))!;
        Directory.CreateDirectory(directory);
        WriteJson(options.OutputFile, report);
        Console.WriteLine($"Paired left-opponent scenarios ({report.Backend}): student {report.StudentWins}/" +
            $"{report.ScenarioCount}, Simple {report.SimpleWins}/{report.ScenarioCount}; " +
            $"score margins {report.StudentScoreMargin}:{report.SimpleScoreMargin}, " +
            $"paired scores {report.PairedBetter}:{report.PairedWorse}:{report.PairedTies}, " +
            $"student win Wilson 95% [{report.StudentWinWilson95Lower:F3}, " +
            $"{report.StudentWinWilson95Upper:F3}]. Report: {options.OutputFile}");
    }

    public static DirectModelDuelReport EvaluateModelDirect(string? modelPath, ulong seed,
        int scenarioCount, int maxTicks, string countdownMode = "seeded-targets",
        string backend = "offline")
    {
        ValidateCount(scenarioCount, nameof(scenarioCount));
        ValidateCount(maxTicks, nameof(maxTicks));
        if (countdownMode is not ("seeded-targets" or "policies"))
            throw new ArgumentException("Countdown mode must be seeded-targets or policies.",
                nameof(countdownMode));
        using var student = EvaluatedModelPolicy.Create(backend, modelPath);
        const int openingTicks = 120;
        var matches = new DirectModelDuelMatch[scenarioCount * 2];
        for (var index = 0; index < scenarioCount; index++)
        {
            // Split ID 4 is the existing direct-duel stream. Each seed gets
            // both side assignments, including the same symmetric opening.
            var matchSeed = StableRandom.DeriveSeed(seed, 4, index);
            matches[index * 2] = RunDirectModelDuel(index, matchSeed, true,
                openingTicks, maxTicks, countdownMode, student);
            matches[index * 2 + 1] = RunDirectModelDuel(index, matchSeed, false,
                openingTicks, maxTicks, countdownMode, student);
        }
        var completed = matches.Count(match => match.Completed);
        var studentWins = matches.Count(match => match.StudentWon);
        var rightCompleted = matches.Count(match => match.StudentSide == "right" && match.Completed);
        var leftCompleted = matches.Count(match => match.StudentSide == "left" && match.Completed);
        var (lower, upper) = Wilson95(studentWins, completed);
        var (scheduledLower, scheduledUpper) = Wilson95(studentWins, matches.Length);
        return new DirectModelDuelReport(seed.ToString(), student.Backend,
            student.ModelSha256, false, 0, null,
            "splitmix64-v1: scenario seed = derive(master, 4, index); " +
            "opening and optional later targets = derive(scenarioSeed, 21, pointIndex)",
            "Both paddles follow the same seed-specific legal target axis for " +
            "120 fixed ticks before policy takeover.",
            countdownMode,
            countdownMode == "seeded-targets"
                ? "Both paddles follow the same point-keyed target during later countdowns."
                : "The evaluated model policy and Simple own all actions during later countdowns.",
            scenarioCount,
            matches.Length, openingTicks, maxTicks, completed,
            studentWins, completed - studentWins,
            matches.Length - completed, rightCompleted,
            matches.Count(match => match.StudentSide == "right" && match.StudentWon),
            leftCompleted,
            matches.Count(match => match.StudentSide == "left" && match.StudentWon),
            matches.Select(match => match.OpeningCheckpointHash).Distinct().Count(),
            matches.Where(match => match.StudentSide == "right")
                .Select(match => match.PlayingTrajectoryHash).Distinct().Count(),
            matches.Where(match => match.StudentSide == "left")
                .Select(match => match.PlayingTrajectoryHash).Distinct().Count(),
            completed == 0 ? 0 : studentWins / (double)completed,
            lower, upper, studentWins / (double)matches.Length,
            scheduledLower, scheduledUpper,
            "Wilson intervals treat games as independent and are descriptive because " +
            "side-swapped games share a scenario seed. The scheduled interval " +
            "counts capped games as non-wins.",
            matches);
    }

    public static void EvaluateModelDirectToFile(DirectModelEvaluationOptions options)
    {
        var report = EvaluateModelDirect(options.StudentModel, options.Seed,
            options.Matches, options.MaxTicks, options.CountdownMode, options.Backend);
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.OutputFile))!;
        Directory.CreateDirectory(directory);
        WriteJson(options.OutputFile, report);
        Console.WriteLine($"Direct side-swapped duels ({report.Backend}, " +
            $"{report.CountdownMode}): " +
            $"student {report.StudentWins}, " +
            $"Simple {report.SimpleWins}, capped {report.CappedMatches}/" +
            $"{report.Matches.Length}; student right {report.StudentRightWins}/" +
            $"{report.StudentRightCompleted}, left {report.StudentLeftWins}/" +
            $"{report.StudentLeftCompleted}; distinct openings " +
            $"{report.DistinctOpeningCheckpoints}/{report.ScenarioCount}, " +
            $"trajectories right {report.DistinctStudentRightTrajectories}/" +
            $"{report.ScenarioCount}, left {report.DistinctStudentLeftTrajectories}/" +
            $"{report.ScenarioCount}; scheduled win rate " +
            $"{report.StudentWinRateScheduled:P1}, descriptive Wilson 95% " +
            $"[{report.StudentScheduledWilson95Lower:F3}, " +
            $"{report.StudentScheduledWilson95Upper:F3}]. " +
            $"Report: {options.OutputFile}");
    }

    private static ModelProfileSummary SummarizeProfile(string name, ModelEvaluationPair[] pairs)
    {
        var count = pairs.Length;
        var studentWins = pairs.Count(pair => pair.Student.RightWon);
        var simpleWins = pairs.Count(pair => pair.Simple.RightWon);
        var better = pairs.Count(pair => pair.PairedScoreImprovement > 0);
        var worse = pairs.Count(pair => pair.PairedScoreImprovement < 0);
        var (winLower, winUpper) = Wilson95(studentWins, count);
        var (pairedLower, pairedUpper) = Wilson95(better, better + worse);
        return new ModelProfileSummary(name, count, studentWins, simpleWins,
            pairs.Sum(pair => pair.Student.RightScoreMargin),
            pairs.Sum(pair => pair.Simple.RightScoreMargin),
            better, worse, count - better - worse,
            count == 0 ? 0 : studentWins / (double)count,
            winLower, winUpper, pairedLower, pairedUpper);
    }

    public static GenerationManifest Generate(GenerationOptions options)
    {
        ValidateCount(options.TrainMatches, nameof(options.TrainMatches));
        ValidateCount(options.ValidationMatches, nameof(options.ValidationMatches));
        ValidateCount(options.TestMatches, nameof(options.TestMatches));
        ValidateCount(options.GateMatches, nameof(options.GateMatches));
        ValidateCount(options.MaxTicks, nameof(options.MaxTicks));
        ValidateCount(options.SampleEveryTicks, nameof(options.SampleEveryTicks));
        if (options.GateMatches < 24)
            throw new ArgumentOutOfRangeException(nameof(options.GateMatches),
                "Teacher gate requires at least 24 held-out scenarios.");
        if (options.SampleEveryTicks % RightBotObservationV1.InferenceCadenceTicks != 0)
            throw new ArgumentOutOfRangeException(nameof(options.SampleEveryTicks),
                "Rows must be sampled at a positive multiple of the versioned inference cadence.");
        if (options.Behavior is not ("teacher" or "simple" or "student"))
            throw new ArgumentException("Behavior must be teacher, simple, or student.");
        if (options.Behavior == "student" && options.StudentModel is null)
            throw new ArgumentException("Student behavior requires --student-model.");
        if (options.Behavior != "student" && options.StudentModel is not null)
            throw new ArgumentException("--student-model requires student behavior.");
        if (Directory.Exists(options.OutputDirectory) &&
            Directory.EnumerateFileSystemEntries(options.OutputDirectory).Any())
            throw new IOException("Output directory is not empty; choose a fresh directory " +
                "so earlier datasets cannot be mistaken for this run.");

        // This must finish before any label file is opened. A weak teacher cannot
        // silently export a training set just because generation itself succeeds.
        var gate = Evaluate(options.Seed, options.GateMatches, options.MaxTicks);
        if (!gate.Passed)
            throw new InvalidOperationException($"Teacher gate failed before label export: " +
                $"teacher {gate.TeacherWins}/{gate.ScenarioCount}, " +
                $"Simple {gate.SimpleWins}/{gate.ScenarioCount}, " +
                $"paired scores {gate.PairedBetter}:{gate.PairedWorse}:" +
                $"{gate.PairedTies}, Wilson lower " +
                $"{gate.TeacherPairedScoreWilson95Lower:F3}, " +
                $"point improvement {gate.PairedScoreImprovement}. " +
                "Run evaluate for the full scenario report.");

        using var student = options.StudentModel is null ? null : new OnnxStudentPolicy(options.StudentModel);
        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        var parentDirectory = Directory.GetParent(outputDirectory)?.FullName ??
            throw new ArgumentException("Output must not be a filesystem root.");
        Directory.CreateDirectory(parentDirectory);
        var stagingDirectory = Path.Combine(parentDirectory,
            $".{Path.GetFileName(outputDirectory)}.staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var summaries = new SplitSummary[3];
            var metrics = new List<MatchMetrics>();
            var splitNames = new[] { "train", "validation", "test" };
            var splitCounts = new[] { options.TrainMatches, options.ValidationMatches, options.TestMatches };
            for (var splitId = 0; splitId < splitNames.Length; splitId++)
            {
                var name = splitNames[splitId];
                var path = Path.Combine(stagingDirectory, $"{name}.jsonl");
                using var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
                var ids = new string[splitCounts[splitId]];
                var seeds = new string[splitCounts[splitId]];
                var rows = 0;
                for (var index = 0; index < splitCounts[splitId]; index++)
                {
                    var matchSeed = StableRandom.DeriveSeed(options.Seed, splitId, index);
                    var id = $"{name}-{index:D5}";
                    ids[index] = id;
                    seeds[index] = matchSeed.ToString();
                    var match = RunMatch(id, name, matchSeed, index, options.Behavior,
                        options.MaxTicks, options.SampleEveryTicks, writer, student);
                    metrics.Add(match);
                    rows += match.Rows;
                }
                summaries[splitId] = new SplitSummary(name, splitCounts[splitId], rows, ids, seeds);
            }

            var modelSha = options.StudentModel is null ? null :
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.StudentModel))).ToLowerInvariant();
            var manifest = new GenerationManifest(RightBotObservationV1.Version,
                RightBotObservationV1.FeatureNames.ToArray(), RightBotObservationV1.InputName,
                RightBotObservationV1.OutputName, [-1, 0, 1],
                RightBotObservationV1.InferenceCadenceTicks, options.SampleEveryTicks,
                FixedStepSeconds, SeedPolicy, options.Seed.ToString(), options.Behavior,
                modelSha, options.MaxTicks, summaries,
                LeftPolicyProfile.All.Select(profile => profile.Name).ToArray(),
                "evaluation.json", "matches.json");
            WriteJson(Path.Combine(stagingDirectory, "evaluation.json"), gate);
            WriteJson(Path.Combine(stagingDirectory, "matches.json"), metrics);
            WriteJson(Path.Combine(stagingDirectory, "manifest.json"), manifest);
            if (Directory.Exists(outputDirectory))
            {
                if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
                    throw new IOException("Output directory gained files during generation.");
                Directory.Delete(outputDirectory);
            }
            Directory.Move(stagingDirectory, outputDirectory);
            Console.WriteLine($"Wrote {metrics.Count} whole matches and " +
                $"{summaries.Sum(split => split.RowCount)} labeled rows to {outputDirectory}. " +
                $"Teacher gate {gate.TeacherWins}/{gate.ScenarioCount} versus " +
                $"Simple {gate.SimpleWins}/{gate.ScenarioCount}.");
            return manifest;
        }
        finally
        {
            // Only this invocation's uniquely named scratch files are removed.
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, true);
        }
    }

    private static MatchMetrics RunMatch(string matchId, string split, ulong seed, int scenarioIndex,
        string rightPolicyName, int maxTicks, int sampleEveryTicks, StreamWriter? rows,
        ILocalOpponentController? student)
    {
        var game = new GameEngine();
        game.StartMatch();
        var left = new LeftOpponentPolicy(LeftPolicyProfile.ForMatch(scenarioIndex), seed);
        left.Reset();
        var behaviorTeacher = new TeacherPolicy();
        behaviorTeacher.Reset();
        var simple = new TrackerBotPolicy();
        simple.Reset();
        student?.Reset();
        var seenEvents = new HashSet<string>(StringComparer.Ordinal);
        var serves = 0;
        var goals = 0;
        var leftHits = 0;
        var rightHits = 0;
        var walls = 0;
        var rallies = 0;
        var hitsInPoint = 0;
        var writtenRows = 0;
        var ticks = 0;

        while (ticks < maxTicks && game.Phase != GamePhase.GameOver)
        {
            var state = game.CaptureCheckpoint();
            var leftAxis = left.GetAxis(state);
            var rightAxis = rightPolicyName switch
            {
                "teacher" => behaviorTeacher.GetAxis(state),
                "simple" => simple.GetAxis(state),
                "student" => student?.GetAxis(state) ?? throw new InvalidOperationException(
                    "Student behavior requires an ONNX model."),
                _ => throw new ArgumentOutOfRangeException(nameof(rightPolicyName))
            };
            if (leftAxis is < -1 or > 1 || rightAxis is < -1 or > 1)
                throw new InvalidOperationException("A headless policy produced an illegal paddle axis.");

            if (rows is not null && state.Phase == GamePhase.Playing &&
                state.TickNumber % sampleEveryTicks == 0)
            {
                // Student and Simple behavior visit states independently of
                // the teacher. A new teacher labels each present checkpoint,
                // so its answer cannot depend on earlier counterfactual calls.
                var teacherAxis = rightPolicyName == "teacher" ? rightAxis :
                    TeacherAxisForCheckpoint(state);
                var row = new TrainingRow(matchId, state.TickNumber,
                    RightBotObservationV1.Encode(state),
                    RightBotObservationV1.ClassFromAxis(teacherAxis), rightAxis);
                rows.WriteLine(JsonSerializer.Serialize(row, CompactJson));
                writtenRows++;
            }

            game.Advance(FixedStepSeconds, leftAxis, rightAxis);
            ticks++;
            foreach (var gameEvent in game.RecentEvents)
            {
                if (!seenEvents.Add(gameEvent.Id)) continue;
                switch (gameEvent.Kind)
                {
                    case GameEventKind.Serve:
                        serves++;
                        hitsInPoint = 0;
                        break;
                    case GameEventKind.Paddle:
                        hitsInPoint++;
                        if (Math.Abs(gameEvent.X - RightContactX) < 1e-9) rightHits++;
                        else if (Math.Abs(gameEvent.X - LeftContactX) < 1e-9) leftHits++;
                        break;
                    case GameEventKind.Wall:
                        walls++;
                        break;
                    case GameEventKind.Goal:
                        goals++;
                        if (hitsInPoint >= 2) rallies++;
                        hitsInPoint = 0;
                        break;
                }
            }
        }

        if (game.Phase != GamePhase.GameOver)
            throw new InvalidOperationException($"Match {matchId} with seed {seed} " +
                $"did not finish within {maxTicks} fixed ticks: " +
                $"rightPolicy={rightPolicyName}, leftProfile={left.Name}, phase={game.Phase}, " +
                $"score={game.LeftScore}:{game.RightScore}, serves={serves}, " +
                $"goals={goals}, leftHits={leftHits}, rightHits={rightHits}.");
        var final = game.Capture();
        return new MatchMetrics(matchId, split, seed.ToString(), left.Name,
            rightPolicyName, ticks, final.LeftScore, final.RightScore,
            serves, goals, leftHits, rightHits, walls, rallies, writtenRows);
    }

    internal static int TeacherAxisForCheckpoint(GameState state)
    {
        var teacher = new TeacherPolicy();
        teacher.Reset();
        var axis = teacher.GetAxis(state);
        if (axis is < -1 or > 1)
            throw new InvalidOperationException("Teacher produced an illegal paddle axis.");
        return axis;
    }

    private static DirectDuelMatch RunDirectDuel(int scenarioIndex, ulong seed,
        bool teacherRight, int openingTicks, int maxTicks)
    {
        var game = new GameEngine();
        game.StartMatch();
        var teacher = new TeacherPolicy();
        teacher.Reset();
        var simple = new TrackerBotPolicy();
        simple.Reset();
        var ticks = 0;
        while (ticks < maxTicks && game.Phase != GamePhase.GameOver)
        {
            var state = game.CaptureCheckpoint();
            int leftAxis;
            int rightAxis;
            if (state.TickNumber < openingTicks)
            {
                // Both policies receive the same reachable paddle movement
                // generated by one legal axis before they take control. This
                // avoids confounding policy identity with opening position.
                leftAxis = rightAxis = DirectOpeningAxis(seed, state.TickNumber);
            }
            else
            {
                leftAxis = teacherRight
                    ? simple.GetAxis(MirrorHorizontally(state))
                    : teacher.GetAxis(MirrorHorizontally(state));
                rightAxis = teacherRight ? teacher.GetAxis(state) : simple.GetAxis(state);
            }
            game.Advance(FixedStepSeconds, leftAxis, rightAxis);
            ticks++;
        }
        return new DirectDuelMatch(scenarioIndex, seed.ToString(),
            teacherRight ? "right" : "left", game.Phase == GamePhase.GameOver,
            ticks, game.LeftScore, game.RightScore);
    }

    private static DirectModelDuelMatch RunDirectModelDuel(int scenarioIndex, ulong seed,
        bool studentRight, int openingTicks, int maxTicks, string countdownMode,
        EvaluatedModelPolicy student)
    {
        const ulong hashOffset = 14695981039346656037UL;
        var game = new GameEngine();
        game.StartMatch();
        student.Reset();
        var simple = new TrackerBotPolicy();
        simple.Reset();
        var ticks = 0;
        var openingHash = hashOffset;
        var trajectoryHash = hashOffset;
        while (ticks < maxTicks && game.Phase != GamePhase.GameOver)
        {
            var state = game.CaptureCheckpoint();
            if (state.TickNumber == openingTicks)
                openingHash = HashDirectStep(hashOffset, state, 0, 0);
            int leftAxis;
            int rightAxis;
            if (state.TickNumber < openingTicks)
            {
                // The same seed-specific target and legal axis are applied to
                // both paddles for the full 120-tick prefix. This reaches many
                // distinct policy-takeover checkpoints while remaining fair
                // under the side swap.
                var target = DirectModelPointTarget(seed, 0);
                leftAxis = rightAxis = AxisToward(state.LeftY, target);
            }
            else if (state.Phase == GamePhase.Countdown && countdownMode == "seeded-targets")
            {
                // Reposition both paddles toward one symmetric, point-keyed
                // target. This supplies fresh legal starting positions after
                // the fixed production-engine serve without altering its ball
                // state. Run each policy's countdown branch to clear held input.
                _ = student.GetAxis(studentRight ? state : MirrorHorizontally(state));
                _ = simple.GetAxis(studentRight ? MirrorHorizontally(state) : state);
                var target = DirectModelPointTarget(seed,
                    state.LeftScore + state.RightScore);
                leftAxis = AxisToward(state.LeftY, target);
                rightAxis = AxisToward(state.RightY, target);
            }
            else
            {
                leftAxis = studentRight
                    ? simple.GetAxis(MirrorHorizontally(state))
                    : student.GetAxis(MirrorHorizontally(state));
                rightAxis = studentRight ? student.GetAxis(state) : simple.GetAxis(state);
            }
            if (leftAxis is < -1 or > 1 || rightAxis is < -1 or > 1)
                throw new InvalidOperationException("A direct model duel produced an illegal paddle axis.");
            if (state.Phase == GamePhase.Playing && state.TickNumber >= openingTicks)
                trajectoryHash = HashDirectStep(trajectoryHash, state, leftAxis, rightAxis);
            game.Advance(FixedStepSeconds, leftAxis, rightAxis);
            ticks++;
        }
        return new DirectModelDuelMatch(scenarioIndex, seed.ToString(),
            studentRight ? "right" : "left", game.Phase == GamePhase.GameOver,
            ticks, game.LeftScore, game.RightScore,
            openingHash.ToString("x16"), trajectoryHash.ToString("x16"),
            false, null);
    }

    private static double DirectModelPointTarget(ulong seed, int pointIndex)
    {
        var random = new StableRandom(StableRandom.DeriveSeed(seed, 21, pointIndex));
        const double inset = 0.05;
        return MinPaddleY + inset +
            random.NextUnit() * (MaxPaddleY - MinPaddleY - 2 * inset);
    }

    private static int AxisToward(double paddleY, double targetY)
    {
        var distance = targetY - paddleY;
        var halfStep = PaddleSpeed * FixedStepSeconds / 2;
        return distance > halfStep ? 1 : distance < -halfStep ? -1 : 0;
    }

    private static ulong HashDirectStep(ulong hash, GameState state,
        int leftAxis, int rightAxis)
    {
        const ulong prime = 1099511628211UL;
        static ulong Bits(double value) => unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        unchecked
        {
            hash = (hash ^ Bits(state.LeftY)) * prime;
            hash = (hash ^ Bits(state.RightY)) * prime;
            hash = (hash ^ Bits(state.BallX)) * prime;
            hash = (hash ^ Bits(state.BallY)) * prime;
            hash = (hash ^ Bits(state.BallVx)) * prime;
            hash = (hash ^ Bits(state.BallVy)) * prime;
            hash = (hash ^ (ulong)state.LeftScore) * prime;
            hash = (hash ^ (ulong)state.RightScore) * prime;
            hash = (hash ^ (ulong)leftAxis) * prime;
            return (hash ^ (ulong)rightAxis) * prime;
        }
    }

    private static int DirectOpeningAxis(ulong seed, long tick)
    {
        var block = checked((int)(tick / 12));
        var opening = new StableRandom(StableRandom.DeriveSeed(seed, 20, block));
        return (int)(opening.NextUInt64() % 3) - 1;
    }

    private static GameState MirrorHorizontally(GameState state) => state with
    {
        LeftY = state.RightY,
        RightY = state.LeftY,
        BallX = 1 - state.BallX,
        BallVx = -state.BallVx,
        LeftScore = state.RightScore,
        RightScore = state.LeftScore,
        ServeDirection = -state.ServeDirection
    };

    private static void ValidateCount(int value, string name)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(name, "Count must be positive.");
    }

    private static (double Lower, double Upper) Wilson95(int successes, int trials)
    {
        if (trials == 0) return (0, 1);
        const double z = 1.959963984540054;
        var rate = successes / (double)trials;
        var zSquared = z * z;
        var denominator = 1 + zSquared / trials;
        var center = (rate + zSquared / (2 * trials)) / denominator;
        var radius = z * Math.Sqrt(rate * (1 - rate) / trials +
            zSquared / (4 * trials * trials)) / denominator;
        return (Math.Max(0, center - radius), Math.Min(1, center + radius));
    }

    private static void WriteJson<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, IndentedJson) + "\n", new UTF8Encoding(false));
}
