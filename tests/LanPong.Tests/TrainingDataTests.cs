using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanPong.TrainingData;

namespace LanPong.Tests;

public sealed class TrainingDataTests
{
    private const int EvaluationTickLimit = 100_000;

    [Test]
    public async Task MatchSeeds_AreRepeatableAndDisjointAcrossWholeMatchSplits()
    {
        const ulong master = 20261007;
        var seen = new HashSet<ulong>();
        for (var split = 0; split < 4; split++)
        for (var match = 0; match < 32; match++)
        {
            var seed = StableRandom.DeriveSeed(master, split, match);
            await Assert.That(seed).IsEqualTo(StableRandom.DeriveSeed(master, split, match));
            await Assert.That(seen.Add(seed)).IsTrue();
        }
        await Assert.That(seen.Count).IsEqualTo(128);
    }

    [Test]
    public async Task SeededLeftProfiles_UseOnlyLegalAxesAndReplayExactEngineStates()
    {
        for (var profileIndex = 0; profileIndex < LeftPolicyProfile.All.Length; profileIndex++)
        {
            var seed = StableRandom.DeriveSeed(20261007, 0, profileIndex);
            var first = new GameEngine();
            var replay = new GameEngine();
            first.StartMatch();
            replay.StartMatch();
            var left = new LeftOpponentPolicy(LeftPolicyProfile.ForMatch(profileIndex), seed);
            var leftReplay = new LeftOpponentPolicy(LeftPolicyProfile.ForMatch(profileIndex), seed);
            left.Reset();
            leftReplay.Reset();
            var right = new TrackerBotPolicy();
            var rightReplay = new TrackerBotPolicy();
            for (var tick = 0; tick < 500; tick++)
            {
                var state = first.CaptureCheckpoint();
                var replayState = replay.CaptureCheckpoint();
                var leftAxis = left.GetAxis(state);
                var replayAxis = leftReplay.GetAxis(replayState);
                var rightAxis = right.GetAxis(state);
                var rightReplayAxis = rightReplay.GetAxis(replayState);
                await Assert.That(leftAxis is >= -1 and <= 1).IsTrue();
                await Assert.That(replayAxis).IsEqualTo(leftAxis);
                await Assert.That(rightReplayAxis).IsEqualTo(rightAxis);
                first.Advance(GameConstants.FixedStepSeconds, leftAxis, rightAxis);
                replay.Advance(GameConstants.FixedStepSeconds, replayAxis, rightReplayAxis);
                await Assert.That(replay.CaptureCheckpoint())
                    .IsEqualTo(first.CaptureCheckpoint());
            }
        }
    }

    [Test]
    public async Task TeacherRelabel_UsesOnlyThePresentCheckpoint()
    {
        var game = new GameEngine();
        game.StartMatch();
        for (var tick = 0; tick < 120; tick++)
            game.Advance(GameConstants.FixedStepSeconds, 0, 0);
        var checkpoint = game.CaptureCheckpoint();
        var first = TrainingDataRunner.TeacherAxisForCheckpoint(checkpoint);

        var other = checkpoint with { RightY = GameConstants.MinPaddleY };
        _ = TrainingDataRunner.TeacherAxisForCheckpoint(other);
        var replay = TrainingDataRunner.TeacherAxisForCheckpoint(checkpoint);

        await Assert.That(replay).IsEqualTo(first);
        await Assert.That(first is >= -1 and <= 1).IsTrue();
        await Assert.That(game.CaptureCheckpoint()).IsEqualTo(checkpoint);
    }

    [Test]
    public async Task PairedEvaluation_ReplaysAndUsesTheSameSeededOpponent()
    {
        var first = TrainingDataRunner.Evaluate(20261007, 2, 20_000);
        var replay = TrainingDataRunner.Evaluate(20261007, 2, 20_000);
        await Assert.That(JsonSerializer.Serialize(replay))
            .IsEqualTo(JsonSerializer.Serialize(first));
        await Assert.That(first.Pairs.Length).IsEqualTo(2);
        foreach (var pair in first.Pairs)
        {
            await Assert.That(pair.Teacher.Seed).IsEqualTo(pair.Simple.Seed);
            await Assert.That(pair.Teacher.LeftPolicy).IsEqualTo(pair.Simple.LeftPolicy);
            await Assert.That(pair.Teacher.RightPolicy).IsEqualTo("teacher");
            await Assert.That(pair.Simple.RightPolicy).IsEqualTo("simple");
            await Assert.That(pair.Teacher.RightWon || pair.Teacher.LeftScore == GameConstants.WinningScore)
                .IsTrue();
            await Assert.That(pair.Simple.RightWon || pair.Simple.LeftScore == GameConstants.WinningScore)
                .IsTrue();
        }
    }

    [Test]
    public async Task DevelopmentSeed_TeacherPassesCompletedMatchScoreGate()
    {
        var report = TrainingDataRunner.Evaluate(20261007, 24, EvaluationTickLimit);
        await Assert.That(report.Passed).IsTrue();
        await Assert.That(report.PairedScoreImprovement).IsGreaterThan(0);
        await Assert.That(report.TeacherPairedScoreWilson95Lower).IsGreaterThan(0.5);
        await Assert.That(report.TeacherWins).IsGreaterThan(report.SimpleWins);
        await Assert.That(report.Pairs.All(pair =>
            pair.Teacher.LeftScore == GameConstants.WinningScore || pair.Teacher.RightWon))
            .IsTrue();
    }

    [Test]
    public async Task Generate_SmallDatasetIsWholeMatchAndByteIdenticalAcrossRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lanpong-data-test-{Guid.NewGuid():N}");
        var firstPath = Path.Combine(root, "first");
        var replayPath = Path.Combine(root, "replay");
        try
        {
            var options = new GenerationOptions(firstPath, 20261007,
                1, 1, 1, 24, EvaluationTickLimit, RightBotObservationV1.InferenceCadenceTicks,
                "teacher", null);
            var first = TrainingDataRunner.Generate(options);
            var replay = TrainingDataRunner.Generate(options with { OutputDirectory = replayPath });
            await Assert.That(first.Splits.Select(split => split.RowCount).Sum())
                .IsGreaterThan(0);
            await Assert.That(replay.Splits.Select(split => split.RowCount).Sum())
                .IsEqualTo(first.Splits.Select(split => split.RowCount).Sum());

            var names = Directory.GetFiles(firstPath).Select(path => Path.GetFileName(path)!)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            await Assert.That(names).IsEquivalentTo(new[]
            {
                "evaluation.json", "manifest.json", "matches.json", "test.jsonl",
                "train.jsonl", "validation.jsonl"
            });
            foreach (var name in names)
            {
                var firstBytes = File.ReadAllBytes(Path.Combine(firstPath, name!));
                var replayBytes = File.ReadAllBytes(Path.Combine(replayPath, name!));
                await Assert.That(firstBytes.SequenceEqual(replayBytes)).IsTrue();
            }

            var matchSeeds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var split in first.Splits)
            {
                await Assert.That(split.MatchCount).IsEqualTo(1);
                await Assert.That(split.MatchIds.Length).IsEqualTo(1);
                await Assert.That(matchSeeds.Add(split.MatchSeeds.Single())).IsTrue();
                var lines = File.ReadAllLines(Path.Combine(firstPath, $"{split.Name}.jsonl"));
                await Assert.That(lines.Length).IsEqualTo(split.RowCount);
                foreach (var line in lines)
                {
                    using var row = JsonDocument.Parse(line);
                    await Assert.That(row.RootElement.GetProperty("matchId").GetString())
                        .IsEqualTo(split.MatchIds.Single());
                    await Assert.That(row.RootElement.GetProperty("tick").GetInt64() %
                        RightBotObservationV1.InferenceCadenceTicks).IsEqualTo(0);
                    await Assert.That(row.RootElement.GetProperty("observation").GetArrayLength())
                        .IsEqualTo(RightBotObservationV1.FeatureCount);
                    await Assert.That(row.RootElement.GetProperty("teacherClass").GetInt32()
                        is >= 0 and <= 2).IsTrue();
                    await Assert.That(row.RootElement.GetProperty("behaviorAxis").GetInt32()
                        is >= -1 and <= 1).IsTrue();
                }
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task StudentOnnxBehavior_ProducesRelabeledReachableRows()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lanpong-student-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var modelPath = Path.Combine(root, "fixed-student.onnx");
        var outputPath = Path.Combine(root, "data");
        try
        {
            File.WriteAllBytes(modelPath, FixedStudentModel());
            using (var student = new OnnxStudentPolicy(modelPath))
            {
                var game = new GameEngine();
                game.StartMatch();
                for (var tick = 0; tick < 99; tick++)
                    game.Advance(GameConstants.FixedStepSeconds, 0, 0);
                await Assert.That(student.GetAxis(game.CaptureCheckpoint())).IsEqualTo(1);
            }

            var manifest = TrainingDataRunner.Generate(new GenerationOptions(outputPath,
                20261007, 1, 1, 1, 24, EvaluationTickLimit,
                RightBotObservationV1.InferenceCadenceTicks, "student", modelPath));
            await Assert.That(manifest.Behavior).IsEqualTo("student");
            await Assert.That(manifest.StudentModelSha256)
                .IsEqualTo(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath)))
                    .ToLowerInvariant());
            foreach (var split in manifest.Splits)
            {
                var lines = File.ReadAllLines(Path.Combine(outputPath, $"{split.Name}.jsonl"));
                await Assert.That(lines.Length).IsEqualTo(split.RowCount);
                foreach (var line in lines)
                {
                    using var row = JsonDocument.Parse(line);
                    await Assert.That(row.RootElement.GetProperty("behaviorAxis").GetInt32())
                        .IsEqualTo(1);
                    var teacherClass = row.RootElement.GetProperty("teacherClass").GetInt32();
                    await Assert.That(teacherClass is >= 0 and <= 2).IsTrue();
                }
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ModelEvaluation_IsPairedSeededAndReportsModelProvenance()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lanpong-model-eval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var modelPath = Path.Combine(root, "fixed-student.onnx");
        try
        {
            File.WriteAllBytes(modelPath, FixedStudentModel());
            var first = TrainingDataRunner.EvaluateModel(modelPath, 20261021, 5, 20_000);
            var replay = TrainingDataRunner.EvaluateModel(modelPath, 20261021, 5, 20_000);
            await Assert.That(JsonSerializer.Serialize(replay))
                .IsEqualTo(JsonSerializer.Serialize(first));
            await Assert.That(first.StudentModelSha256)
                .IsEqualTo(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath)))
                    .ToLowerInvariant());
            await Assert.That(first.Profiles.Length).IsEqualTo(LeftPolicyProfile.All.Length);
            await Assert.That(first.Profiles.Sum(profile => profile.ScenarioCount)).IsEqualTo(5);
            await Assert.That(first.PairedBetter + first.PairedWorse + first.PairedTies)
                .IsEqualTo(5);
            await Assert.That(first.StudentWinWilson95Lower is >= 0 and <= 1).IsTrue();
            await Assert.That(first.StudentWinWilson95Upper is >= 0 and <= 1).IsTrue();
            foreach (var pair in first.Pairs)
            {
                await Assert.That(pair.Student.Seed).IsEqualTo(pair.Simple.Seed);
                await Assert.That(pair.Student.LeftPolicy).IsEqualTo(pair.Simple.LeftPolicy);
                await Assert.That(pair.Student.RightPolicy).IsEqualTo("student");
                await Assert.That(pair.Simple.RightPolicy).IsEqualTo("simple");
                await Assert.That(pair.Student.RightWon ||
                    pair.Student.LeftScore == GameConstants.WinningScore).IsTrue();
                await Assert.That(pair.Simple.RightWon ||
                    pair.Simple.LeftScore == GameConstants.WinningScore).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task DirectModelEvaluation_SwapsSidesAndSeparatesCappedGames()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lanpong-direct-model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var modelPath = Path.Combine(root, "fixed-student.onnx");
        try
        {
            File.WriteAllBytes(modelPath, FixedStudentModel());
            var first = TrainingDataRunner.EvaluateModelDirect(modelPath, 20261022, 2, 8_000);
            var replay = TrainingDataRunner.EvaluateModelDirect(modelPath, 20261022, 2, 8_000);
            await Assert.That(JsonSerializer.Serialize(replay))
                .IsEqualTo(JsonSerializer.Serialize(first));
            await Assert.That(first.StudentModelSha256)
                .IsEqualTo(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath)))
                    .ToLowerInvariant());
            await Assert.That(first.Matches.Length).IsEqualTo(4);
            await Assert.That(first.ScheduledMatches).IsEqualTo(4);
            await Assert.That(first.CompletedMatches + first.CappedMatches).IsEqualTo(4);
            await Assert.That(first.StudentWins + first.SimpleWins)
                .IsEqualTo(first.CompletedMatches);
            await Assert.That(first.StudentRightCompleted + first.StudentLeftCompleted)
                .IsEqualTo(first.CompletedMatches);
            await Assert.That(first.StudentRightWins + first.StudentLeftWins)
                .IsEqualTo(first.StudentWins);
            await Assert.That(first.StudentWilson95Lower is >= 0 and <= 1).IsTrue();
            await Assert.That(first.StudentWilson95Upper is >= 0 and <= 1).IsTrue();
            await Assert.That(first.StudentWinRateScheduled)
                .IsEqualTo(first.StudentWins / 4.0);
            await Assert.That(first.StudentScheduledWilson95Lower is >= 0 and <= 1)
                .IsTrue();
            await Assert.That(first.StudentScheduledWilson95Upper is >= 0 and <= 1)
                .IsTrue();
            await Assert.That(first.DistinctOpeningCheckpoints).IsGreaterThan(1);
            await Assert.That(first.DistinctStudentRightTrajectories).IsGreaterThan(1);
            await Assert.That(first.DistinctStudentLeftTrajectories).IsGreaterThan(1);
            await Assert.That(first.CountdownMode).IsEqualTo("seeded-targets");
            for (var index = 0; index < 2; index++)
            {
                await Assert.That(first.Matches[index * 2].Seed)
                    .IsEqualTo(first.Matches[index * 2 + 1].Seed);
                await Assert.That(first.Matches[index * 2].StudentSide).IsEqualTo("right");
                await Assert.That(first.Matches[index * 2 + 1].StudentSide).IsEqualTo("left");
            }

            var policies = TrainingDataRunner.EvaluateModelDirect(modelPath,
                20261022, 2, 8_000, "policies");
            await Assert.That(policies.CountdownMode).IsEqualTo("policies");
            await Assert.That(policies.CountdownProtocol.Contains("own all actions"))
                .IsTrue();
            await Assert.That(policies.CompletedMatches + policies.CappedMatches)
                .IsEqualTo(policies.ScheduledMatches);
            await Assert.That(policies.StudentWinRateScheduled)
                .IsEqualTo(policies.StudentWins / (double)policies.ScheduledMatches);
            for (var index = 0; index < first.Matches.Length; index++)
                await Assert.That(policies.Matches[index].OpeningCheckpointHash)
                    .IsEqualTo(first.Matches[index].OpeningCheckpointHash);
            await Assert.That(Enumerable.Range(0, first.Matches.Length).Any(index =>
                policies.Matches[index].PlayingTrajectoryHash !=
                first.Matches[index].PlayingTrajectoryHash)).IsTrue();

            var capped = TrainingDataRunner.EvaluateModelDirect(modelPath, 20261022, 2, 1);
            await Assert.That(capped.CappedMatches).IsEqualTo(capped.ScheduledMatches);
            await Assert.That(capped.StudentWins).IsEqualTo(0);
            await Assert.That(capped.StudentWinRateScheduled).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ProductionModelEvaluation_UsesSupportedPolicyAndPreservesExactTrajectories()
    {
        var modelPath = FrozenModelPath();
        await Assert.That(File.Exists(modelPath)).IsTrue();
        var pairedOffline = TrainingDataRunner.EvaluateModel(modelPath,
            20261020, 2, 8_000);
        var pairedProduction = TrainingDataRunner.EvaluateModel(modelPath,
            20261020, 2, 8_000, "production");
        await Assert.That(pairedProduction.Backend).IsEqualTo("production");
        await Assert.That(pairedProduction.StudentModelSha256)
            .IsEqualTo(BotModelV1.ExpectedSha256);
        await Assert.That(pairedProduction.UsedModelThroughout).IsTrue();
        await Assert.That(pairedProduction.FallbackActiveAtLoad).IsFalse();
        await Assert.That(pairedProduction.StudentFallbackMatches).IsEqualTo(0);
        await Assert.That(pairedProduction.FallbackReason).IsNull();
        for (var index = 0; index < pairedOffline.Pairs.Length; index++)
            await Assert.That(pairedProduction.Pairs[index].Student)
                .IsEqualTo(pairedOffline.Pairs[index].Student);

        var directOffline = TrainingDataRunner.EvaluateModelDirect(modelPath,
            20261020, 2, 8_000, "policies");
        var directProduction = TrainingDataRunner.EvaluateModelDirect(modelPath,
            20261020, 2, 8_000, "policies", "production");
        await Assert.That(directProduction.Backend).IsEqualTo("production");
        await Assert.That(directProduction.UsedModelThroughout).IsTrue();
        await Assert.That(directProduction.FallbackActiveAtLoad).IsFalse();
        await Assert.That(directProduction.FallbackGames).IsEqualTo(0);
        await Assert.That(directProduction.FallbackReason).IsNull();
        await Assert.That(directProduction.CappedMatches).IsEqualTo(0);
        for (var index = 0; index < directOffline.Matches.Length; index++)
        {
            await Assert.That(directProduction.Matches[index].PlayingTrajectoryHash)
                .IsEqualTo(directOffline.Matches[index].PlayingTrajectoryHash);
            await Assert.That(directProduction.Matches[index].StudentFallbackActive)
                .IsFalse();
            await Assert.That(directProduction.Matches[index].StudentFallbackReason)
                .IsNull();
        }
    }

    [Test]
    public async Task ProductionModelEvaluation_RejectsInvalidModelBeforeChangingReportFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lanpong-invalid-model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var wrongModel = Path.Combine(root, "wrong.onnx");
            File.WriteAllBytes(wrongModel, FixedStudentModel());
            var pairedReport = Path.Combine(root, "paired-report.json");
            const string existingReport = "existing evaluation evidence";
            File.WriteAllText(pairedReport, existingReport);
            await Assert.That(() => TrainingDataRunner.EvaluateModelToFile(
                new ModelEvaluationOptions(pairedReport, wrongModel, 20261020,
                    1, 8_000, "production"))).Throws<InvalidDataException>();
            await Assert.That(File.ReadAllText(pairedReport)).IsEqualTo(existingReport);

            var directDirectory = Path.Combine(root, "new-report-directory");
            var directReport = Path.Combine(directDirectory, "direct-report.json");
            await Assert.That(() => TrainingDataRunner.EvaluateModelDirectToFile(
                new DirectModelEvaluationOptions(directReport, wrongModel,
                    20261020, 1, 8_000, "policies", "production")))
                .Throws<InvalidDataException>();
            await Assert.That(Directory.Exists(directDirectory)).IsFalse();

            var missingModel = Path.Combine(root, "missing.onnx");
            await Assert.That(() => TrainingDataRunner.EvaluateModelToFile(
                new ModelEvaluationOptions(pairedReport, missingModel, 20261020,
                    1, 8_000, "production"))).Throws<FileNotFoundException>();
            await Assert.That(File.ReadAllText(pairedReport)).IsEqualTo(existingReport);
            await Assert.That(() => TrainingDataRunner.EvaluateModelDirectToFile(
                new DirectModelEvaluationOptions(directReport, missingModel,
                    20261020, 1, 8_000, "policies", "production")))
                .Throws<FileNotFoundException>();
            await Assert.That(Directory.Exists(directDirectory)).IsFalse();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ProductionModelPolicy_PropagatesSessionPreparationFailure()
    {
        var attempts = 0;
        IOnnxInferenceSession RejectPreparation(string path)
        {
            attempts++;
            if (path != FrozenModelPath()) throw new ArgumentException("Unexpected model path.");
            throw new InvalidDataException("Cannot prepare the model session.");
        }

        await Assert.That(() => EvaluatedModelPolicy.CreateProduction(FrozenModelPath(),
            RejectPreparation)).Throws<InvalidDataException>();
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task ProductionModelPolicy_PropagatesInferenceFailureAndDisposesOwnedSessionOnce()
    {
        var session = new FailingModelSession();
        var creations = 0;
        IOnnxInferenceSession CreateSession(string _)
        {
            creations++;
            return session;
        }

        var policy = EvaluatedModelPolicy.CreateProduction(FrozenModelPath(), CreateSession);
        try
        {
            await Assert.That(policy.ModelSha256).IsEqualTo(BotModelV1.ExpectedSha256);
            await Assert.That(policy.GetAxis(new GameState
                { Phase = GamePhase.Playing, TickNumber = 9 })).IsEqualTo(1);
            await Assert.That(() => policy.GetAxis(new GameState
                { Phase = GamePhase.Playing, TickNumber = 18 }))
                .Throws<InvalidDataException>();
            await Assert.That(session.Calls).IsEqualTo(2);
            await Assert.That(creations).IsEqualTo(1);
        }
        finally
        {
            policy.Dispose();
            policy.Dispose();
        }
        await Assert.That(session.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task Generate_RefusesNonemptyOutputWithoutChangingExistingFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lanpong-data-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "keep.txt");
        File.WriteAllText(marker, "existing user data");
        try
        {
            var rejected = false;
            try
            {
                TrainingDataRunner.Generate(new GenerationOptions(directory, 20261007,
                    1, 1, 1, 24, 20_000, RightBotObservationV1.InferenceCadenceTicks,
                    "teacher", null));
            }
            catch (IOException)
            {
                rejected = true;
            }
            await Assert.That(rejected).IsTrue();
            await Assert.That(File.ReadAllText(marker)).IsEqualTo("existing user data");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string FrozenModelPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../../src/LanPong", BotModelV1.RelativeModelPath));

    private sealed class FailingModelSession : IOnnxInferenceSession
    {
        public int Calls { get; private set; }
        public int Disposals { get; private set; }

        public void Run(ReadOnlySpan<float> observation, Span<float> logits)
        {
            if (++Calls > 1) throw new InvalidDataException("Inference failed after a valid action.");
            logits[0] = 0;
            logits[1] = 0;
            logits[2] = 1;
        }

        public void Dispose() => Disposals++;
    }

    // A tiny standard-library-style ONNX protobuf fixture: Gemm([1,10],
    // zero[10,3], bias[0,1,2]) always emits class 2. It proves the fixed
    // observation/logits shape without adding a Python training dependency.
    private static byte[] FixedStudentModel()
    {
        var node = Message(
            Bytes(1, "observation"), Bytes(1, "weight"), Bytes(1, "bias"),
            Bytes(2, "logits"), Bytes(4, "Gemm"));
        var weight = Tensor("weight", [10, 3], new float[30]);
        var bias = Tensor("bias", [3], [0, 1, 2]);
        var graph = Message(Bytes(1, node), Bytes(2, "student_smoke"),
            Bytes(5, weight), Bytes(5, bias),
            Bytes(11, ValueInfo("observation", 1, 10)),
            Bytes(12, ValueInfo("logits", 1, 3)));
        return Message(Integer(1, 7), Bytes(2, "LanPong.Tests"),
            Bytes(7, graph), Bytes(8, Integer(2, 13)));
    }

    private static byte[] Tensor(string name, int[] dimensions, float[] values)
    {
        var data = new byte[values.Length * sizeof(float)];
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(index * sizeof(float)), values[index]);
        return Message(dimensions.Select(dimension => Integer(1, dimension))
            .Concat([Integer(2, 1), Bytes(8, name), Bytes(9, data)]).ToArray());
    }

    private static byte[] ValueInfo(string name, params int[] dimensions)
    {
        var shape = Message(dimensions.Select(dimension =>
            Bytes(1, Integer(1, dimension))).ToArray());
        var tensorType = Message(Integer(1, 1), Bytes(2, shape));
        return Message(Bytes(1, name), Bytes(2, Bytes(1, tensorType)));
    }

    private static byte[] Integer(int field, int value) =>
        Message(Varint((ulong)(field << 3)), Varint((ulong)value));

    private static byte[] Bytes(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    private static byte[] Bytes(int field, byte[] value) =>
        Message(Varint((ulong)((field << 3) | 2)), Varint((ulong)value.Length), value);

    private static byte[] Message(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value > 0x7f)
        {
            bytes.Add((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
        return bytes.ToArray();
    }
}
