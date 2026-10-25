namespace Corvid.App.Core.Sessions;

/// <summary>What a key on a focused approval card does (<see cref="CardKeyGate"/>).</summary>
public enum CardKey
{
    /// <summary>The card answers it as before (Enter presses the focused answer, a digit picks one).</summary>
    Answer,
    /// <summary>The key is swallowed: it answers nothing.</summary>
    Ignore,
    /// <summary>The key is typing meant for the composer: focus goes back there with it.</summary>
    ToComposer,
}

/// <summary>
/// A4 verification: the Enter that sends a message while a card waits moves focus to the card, and the same key held down
/// (its auto-repeat) or a digit typed straight after it must not answer the card. So the card is quiet when focus moves to
/// it, until no Enter or digit has reached it for <see cref="Quiet"/> (longer than a keyboard's usual repeat delay, and a
/// held key's repeats keep it quiet); a navigation key (an arrow, Tab) arms it at once, since the person is plainly on the
/// card. While quiet an Enter is swallowed and a digit goes back to the composer as typing. A repeated key-down (the
/// platform says the key was already down) never answers. Keyboard users answer after a moment, or after an arrow.
/// </summary>
public sealed class CardKeyGate(TimeProvider time)
{
    /// <summary>How long the card stays quiet after focus moves to it, or after a key it swallowed.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(750);

    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private DateTimeOffset _quietUntil;

    /// <summary>Focus moved to the card by the page (the Enter that sent, a card that opened): it is quiet for a moment.</summary>
    public void Focused() => _quietUntil = _time.GetUtcNow() + Quiet;

    /// <summary>A navigation key on the card: the person is on it, so it answers at once.</summary>
    public void Arm() => _quietUntil = default;

    /// <summary>What an Enter (<paramref name="digit"/> false) or a digit does on the card now.</summary>
    /// <param name="repeated">The platform reports the key as already down (auto-repeat).</param>
    public CardKey Decide(bool digit, bool repeated)
    {
        var now = _time.GetUtcNow();
        if (!repeated && now >= _quietUntil)
            return CardKey.Answer;
        // A held Enter's repeats, or typing straight after focus moved, keep the card quiet a moment longer.
        _quietUntil = now + Quiet;
        return digit && !repeated ? CardKey.ToComposer : CardKey.Ignore;
    }
}
