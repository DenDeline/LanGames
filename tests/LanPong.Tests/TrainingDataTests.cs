using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanPong.TrainingData;

namespace LanPong.Tests;

public sealed class TrainingDataTests
{
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
            var right = new SimpleLocalOpponentController();
            var rightReplay = new SimpleLocalOpponentController();
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
        var report = TrainingDataRunner.Evaluate(20261007, 24, 20_000);
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
                1, 1, 1, 24, 20_000, RightBotObservationV1.InferenceCadenceTicks,
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
                20261007, 1, 1, 1, 24, 20_000,
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
