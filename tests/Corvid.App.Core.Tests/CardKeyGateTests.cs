using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Tests;

/// <summary>A4 verification: the Enter that focused a card, held down or followed by a digit, never answers it.</summary>
public sealed class CardKeyGateTests
{
    [Fact]
    public void AHeldEntersRepeatsNeverAnswerAndKeepTheCardQuiet()
    {
        var time = ManualTimeProvider.Demo();
        var gate = new CardKeyGate(time);
        gate.Focused();

        // The platform's repeat starts after its delay, then repeats every few tens of milliseconds, flagged or not.
        time.Advance(0.6);
        Assert.Equal(CardKey.Ignore, gate.Decide(digit: false, repeated: true));
        for (var i = 0; i < 40; i++)
        {
            time.Advance(0.04);
            Assert.Equal(CardKey.Ignore, gate.Decide(digit: false, repeated: false));
        }

        // Released: a moment later a fresh Enter answers.
        time.Advance(CardKeyGate.Quiet);
        Assert.Equal(CardKey.Answer, gate.Decide(digit: false, repeated: false));
        // A repeated key-down never answers, even on an armed card.
        Assert.Equal(CardKey.Ignore, gate.Decide(digit: false, repeated: true));
    }

    [Fact]
    public void ADigitTypedStraightAfterEnterGoesToTheComposerAndOneTypedLaterAnswers()
    {
        var time = ManualTimeProvider.Demo();
        var gate = new CardKeyGate(time);
        gate.Focused();

        time.Advance(0.1);
        Assert.Equal(CardKey.ToComposer, gate.Decide(digit: true, repeated: false));

        gate.Focused();
        time.Advance(CardKeyGate.Quiet);
        Assert.Equal(CardKey.Answer, gate.Decide(digit: true, repeated: false));
    }

    [Fact]
    public void AnArrowArmsTheCardAtOnceForAKeyboardUser()
    {
        var time = ManualTimeProvider.Demo();
        var gate = new CardKeyGate(time);
        gate.Focused();

        gate.Arm();

        Assert.Equal(CardKey.Answer, gate.Decide(digit: true, repeated: false));
        Assert.Equal(CardKey.Answer, gate.Decide(digit: false, repeated: false));
    }

    [Fact]
    public void ACardFocusedOtherwiseThanByThePageAnswersAtOnce()
    {
        // Clicked or tabbed to: the page never moved focus there, so nothing is quiet.
        var gate = new CardKeyGate(ManualTimeProvider.Demo());

        Assert.Equal(CardKey.Answer, gate.Decide(digit: true, repeated: false));
    }
}
