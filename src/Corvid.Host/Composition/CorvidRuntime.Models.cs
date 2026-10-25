using Corvid.Host.Commands;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Models;

namespace Corvid.Host.Composition;

/// <summary>What <see cref="CorvidRuntime.WriteDefaultModel"/> did: the file's write, and whether new sessions here start on it now.</summary>
/// <param name="Write">The write to <c>corvid.json</c>.</param>
/// <param name="Live">New sessions this runtime opens start on it from now on (the registry was rebound).</param>
/// <param name="LiveDetail">Why the running runtime could not take it now (it applies at the next start); null when live or not written.</param>
public sealed record ModelDefaultChange(ModelDefaultWrite Write, bool Live, string? LiveDetail = null)
{
    /// <summary>The answer in a sentence or two for any surface: what was written, and what it changes.</summary>
    public string Describe()
    {
        var write = Write;
        if (!write.Written)
            return write.Detail ?? "Not written.";
        var model = TerminalText.Visible(write.Model);
        var text = write.AlreadySet
            ? $"The {write.Slot} default for new sessions is already {model}."
            : $"The {write.Slot} default for new sessions is {model} ({TerminalText.Visible(write.Path)}).";
        if (Live)
            text += " New sessions start on it now; a session that chose its own keeps that choice.";
        else if (LiveDetail is { } detail)
            text += $" It applies when Corvid next starts: {detail}";
        if (!write.ShadowedKeys.IsEmpty)
            text += $" This run's environment or --set overrides {TerminalText.Visible(string.Join(", ", write.ShadowedKeys))}, so it goes on using that.";
        if (write.NotReady is { } missing)
            text += $" It is not usable yet: {missing.Detail}" + (missing.Acquire is { } acquire ? $" Run `{acquire}`." : "");
        return text;
    }
}

public sealed partial class CorvidRuntime
{
    /// <summary>
    /// A5: the writer for <c>corvid.json</c> the host made (<see cref="CorvidConfigWriter"/>), for the "default for new sessions"
    /// choice on surfaces that have only the runtime (<c>/model --default</c>). Null in a runtime built without one (tests).
    /// </summary>
    public CorvidConfigWriter? ConfigWriter { get; init; }

    /// <summary>
    /// A5: the "default for new sessions" choice (<see cref="ModelDefaults"/>): writes the slot's model to <c>corvid.json</c>
    /// and, when the model is usable now, rebinds the registry (<see cref="SetDefaultModel"/>) so sessions opened from now on
    /// start on it. No session switches; one that chose nothing for the slot returns to it when its turn ends.
    /// </summary>
    /// <exception cref="InvalidOperationException">The runtime has no configuration writer.</exception>
    public ModelDefaultChange WriteDefaultModel(string slot, string model)
    {
        var writer = ConfigWriter ?? throw new InvalidOperationException("This Corvid has no configuration file to write the default to.");
        var write = ModelDefaults.Write(writer, Catalogue, slot, model, takeOffLadder: true);
        if (!write.Written)
            return new(write, Live: false);
        if (write.NotReady is not null)
            return new(write, Live: false, LiveDetail: "it cannot load now.");
        try
        {
            SetDefaultModel(slot, write.Model);
            return new(write, Live: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ModelUnavailableException)
        {
            return new(write, Live: false, LiveDetail: TerminalText.Visible(exception.Message));
        }
    }
}
