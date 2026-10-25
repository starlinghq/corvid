using System.Globalization;
using Corvid.Host.Composition;
using Corvid.Kernel.Sessions;
using Corvid.Models;

namespace Corvid.Host.Commands;

/// <summary>
/// <c>/fork</c> (G-15, docs/plan/tui.md §4.5): a new session from this one's log, at its head or a position. While a turn
/// runs it forks at the turn's last completed step without waiting, and the turn goes on here; a surface that reads lines
/// during a turn (<c>corvid chat</c>) runs it then. The fork starts with this session's taint and its model choices, or
/// models the person names. This process lets go of the fork at once, so another terminal or the app can drive it.
/// </summary>
internal static class ForkCommand
{
    public const string Usage = "[<position>] [<slot>=<model> ...] [--mix <name>]";

    public static async ValueTask<CommandOutput> RunAsync(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        var runtime = context.Runtime;
        long? through = null;
        ModelMix? mix = null;
        var picks = new List<KeyValuePair<string, ModelMixSlot>>();
        for (var i = 0; i < invocation.Arguments.Length; i++)
        {
            var argument = invocation.Arguments[i];
            if (argument == "--mix")
            {
                if (i + 1 >= invocation.Arguments.Length)
                    return CommandOutput.Error("Usage: /fork " + Usage + ".");
                var name = invocation.Arguments[++i];
                mix = runtime.Options.Agents.Mix(name)
                    ?? throw new ArgumentException($"No mix named '{TerminalText.Visible(name)}' is configured (Corvid:Agents:Mixes).");
            }
            else if (argument.IndexOf('=', StringComparison.Ordinal) is > 0 and var equals)
            {
                picks.Add(KeyValuePair.Create(argument[..equals], new ModelMixSlot(argument[(equals + 1)..])));
            }
            else if (through is null && long.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var position) && position > 0)
            {
                through = position;
            }
            else
            {
                return CommandOutput.Error($"/fork does not understand '{TerminalText.Visible(argument)}'. Usage: /fork {Usage}.");
            }
        }
        // A configured mix names whole slots (ladders too); a fork takes their models only, and a model named here wins.
        var chosen = (mix?.Slots ?? ModelMix.Empty.Slots).Select(pair => KeyValuePair.Create(pair.Key, new ModelMixSlot(pair.Value.Model)))
            .Where(pair => picks.All(pick => pick.Key != pair.Key)).Concat(picks).ToList();

        ForkResult fork;
        try
        {
            fork = await runtime.ForkAsync(context.Session.Id, new()
            {
                Through = through,
                Surface = context.Session.Surface,
                Mix = chosen.Count > 0 ? ModelMix.Create(chosen) : null,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionForkException exception)
        {
            return CommandOutput.Error("Not forked: " + exception.Message);
        }
        catch (ModelMixException exception)
        {
            return CommandOutput.Error("Not forked: " + exception.Message);
        }

        var id = fork.Session.Id;
        var lines = new List<string>
        {
            $"Forked into {id} through position {fork.Through}" + (fork.DuringTurn
                ? ", the running turn's last completed step; the turn goes on here."
                : "."),
        };
        if (fork.Tainted)
            lines.Add("The fork holds outside content, as this session does, so it starts tainted.");
        var refused = false;
        foreach (var model in fork.Models)
        {
            var to = TerminalText.Visible(model.To?.Id ?? "none");
            switch (model.Status)
            {
                case SwitchStatus.Switched or SwitchStatus.Unchanged:
                    lines.Add($"{model.Slot}: {to} for the fork's session" + (model.Pinned ? ", pinned." : "."));
                    break;
                default:
                    refused = true;
                    lines.Add($"{model.Slot}: stays on {TerminalText.Visible(model.From.Id)}; {to} was refused" +
                        (model.Detail is { Length: > 0 } detail ? ": " + TerminalText.Visible(detail) : "") + ".");
                    break;
            }
        }
        // Nothing of the fork runs here: let go of it, so `corvid chat --session` or the app can drive it. Its model choices
        // are in its log and come back when another process opens it (A5), except a cloud one, which is chosen again there
        // through the privacy gate; a fork with a cloud choice stays open here, where it was chosen.
        var cloud = fork.Models.Any(model => model.Status is SwitchStatus.Switched or SwitchStatus.Unchanged
            && model.To?.Privacy == PrivacyClass.Cloud);
        var released = !cloud && await runtime.ReleaseDriverAsync(id).ConfigureAwait(false);
        lines.Add(released
            ? $"Continue it with corvid chat --session {id}, or open it in the app."
            : cloud
                ? $"It stays open in this process with its cloud model; opened by another process after this one leaves (corvid chat --session {id}), its cloud choice is chosen again there through the privacy gate."
                : $"It is open in this process; continue it with corvid chat --session {id} once this one leaves.");
        return refused
            ? new CommandOutput { Lines = [.. lines], Tone = OutputTone.Warning }
            : new CommandOutput { Lines = [.. lines], Tone = OutputTone.Success };
    }
}
