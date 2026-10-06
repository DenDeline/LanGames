namespace LanPong.Tests;

public sealed class LocalControllerInputsTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task GetAxis_UsesNewestFreshNonzeroController()
    {
        var inputs = new LocalControllerInputs();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var passive = Guid.NewGuid();

        inputs.Set(first, -1, Start);
        inputs.Set(second, 1, Start.AddMilliseconds(1));
        inputs.Set(passive, 0, Start.AddMilliseconds(2));
        await Assert.That(inputs.GetAxis(Start.AddMilliseconds(2))).IsEqualTo(1);

        inputs.Set(first, -1, Start.AddMilliseconds(3));
        await Assert.That(inputs.GetAxis(Start.AddMilliseconds(3))).IsEqualTo(-1);

        inputs.Remove(first);
        await Assert.That(inputs.GetAxis(Start.AddMilliseconds(3))).IsEqualTo(1);
    }

    [Test]
    public async Task GetAxis_ExpiresOnlyAfterStaleThreshold()
    {
        var inputs = new LocalControllerInputs();
        inputs.Set(Guid.NewGuid(), 1, Start);

        var deadline = Start + NetworkConstants.InputStaleAfter;
        await Assert.That(inputs.GetAxis(deadline)).IsEqualTo(1);
        await Assert.That(inputs.GetAxis(deadline.AddTicks(1))).IsEqualTo(0);
    }

    [Test]
    public async Task Set_ClampsAxisAndRefreshesExistingController()
    {
        var inputs = new LocalControllerInputs();
        var controller = Guid.NewGuid();
        inputs.Set(controller, 7, Start);
        await Assert.That(inputs.GetAxis(Start)).IsEqualTo(1);

        var refreshedAt = Start.AddMilliseconds(300);
        inputs.Set(controller, -7, refreshedAt);
        await Assert.That(inputs.GetAxis(Start + NetworkConstants.InputStaleAfter + TimeSpan.FromTicks(1)))
            .IsEqualTo(-1);

        inputs.Clear();
        await Assert.That(inputs.GetAxis(refreshedAt)).IsEqualTo(0);
    }

    [Test]
    public async Task GetAxis_EqualTimestampsKeepFirstController()
    {
        var inputs = new LocalControllerInputs();
        inputs.Set(Guid.NewGuid(), -1, Start);
        inputs.Set(Guid.NewGuid(), 1, Start);

        await Assert.That(inputs.GetAxis(Start)).IsEqualTo(-1);
    }
}
