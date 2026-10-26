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

/// <summary>A5 review: a "this session" choice a session's log holds: the profile id (<c>qwen3.5-0.8b@cuda0</c>) and its pin.</summary>
public sealed record ModelChoice(string Profile, bool Pinned);

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

    /// <summary>
    /// A5: the person's "this session" choices in a session's log (<see cref="ModelChoiceStates"/>: the latest <c>session</c>
    /// switch per slot with its pin, unless a reset came after it), put back, pinned or not, when the session is
    /// opened again, so a choice lasts for the session across a restart, <c>corvid chat --session</c> or a fork. A model no
    /// longer in the catalogue or not usable now is left out with a notice, and so is a cloud one: a cloud binding is chosen
    /// again through the privacy gate, never brought back without it. Memory's embedder is left to its configuration.
    /// </summary>
    private void RestoreModelChoices(string sessionId, SessionModelSlots slots)
    {
        var choices = ScanModelChoices(sessionId, null, out var pins);
        foreach (var (slot, (id, pinned)) in choices)
            if (Usable(slot, id, "choice") is { } binding)
                slots.RestoreSessionDefault(slot, binding, pinned);
        // A6 fix (A5 left): a slot pinned after its choice was reset stays pinned where it was, with no choice, as it was live.
        foreach (var (slot, id) in pins)
            if (!choices.ContainsKey(slot) && Usable(slot, id, "pinned model") is { } binding)
                slots.RestorePin(slot, binding);

        ModelBinding? Usable(string slot, string id, string what)
        {
            if (slot == ModelSlots.TextEmbedder || !ModelSlots.All.Contains(slot, StringComparer.Ordinal))
                return null;
            var name = id.Split('@')[0];
            if (!Catalogue.TryFind(name, out var model) || !model.Serves(slot))
            {
                RaiseNotice($"{sessionId}: its {slot} {what} {TerminalText.Visible(name)} is not in the catalogue now, so it starts on the default.");
                return null;
            }
            if (model.Profile.Privacy == PrivacyClass.Cloud)
            {
                RaiseNotice($"{sessionId}: its {slot} {what} {TerminalText.Visible(model.Name)} is a cloud model; choose it again (/model) to send through the privacy gate.");
                return null;
            }
            if (!Catalogue.Check(model.Name).IsReady)
            {
                RaiseNotice($"{sessionId}: its {slot} {what} {TerminalText.Visible(model.Name)} is not usable now, so it starts on the default.");
                return null;
            }
            try
            {
                return _composer.Bind(slot, model.Name, Corvid.Kernel.Scheduling.DevicePriority.Interactive);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ModelUnavailableException or IOException)
            {
                RaiseNotice($"{sessionId}: its {slot} {what} {TerminalText.Visible(model.Name)} could not load ({TerminalText.Visible(exception.Message)}), so it starts on the default.");
                return null;
            }
        }

        void RaiseNotice(string line)
        {
            lock (_notices)
            {
                _notices.Enqueue(line);
                while (_notices.Count > 20)
                    _notices.Dequeue();
            }
            if (Notice is { } notice)
                notice(line);
            else
                System.Diagnostics.Trace.WriteLine("corvid: " + line);
        }

    }

    /// <summary>
    /// A5: the person's "this session" choices a session's log holds, by slot, as profile ids (<c>qwen3.5-0.8b@cuda0</c>): the
    /// latest <c>ModelSwitched</c> with the reason <c>session</c> per slot, unless a reset came after it. What a session opened
    /// again starts on; a surface can show it for a session not open here.
    /// </summary>
    public IReadOnlyDictionary<string, string> ModelChoices(string sessionId) =>
        ModelChoiceStates(sessionId).ToDictionary(pair => pair.Key, pair => pair.Value.Profile, StringComparer.Ordinal);

    /// <summary>
    /// A5 review: the choices <see cref="ModelChoices"/> reads, with whether each is pinned, as the log stood at
    /// <paramref name="through"/> (its head when null). A <c>session</c> switch is a choice (its <c>Pinned</c> says whether it
    /// was pinned; a log from before A5's review has none, so unpinned); a <c>de_escalation</c> with <c>Reset</c> forgets it
    /// (and, in an older log, one to another model after it); an <c>unpin</c> or a turn switch that changed the pin sets the pin.
    /// </summary>
    public IReadOnlyDictionary<string, ModelChoice> ModelChoiceStates(string sessionId, long? through = null) =>
        ScanModelChoices(sessionId, through, out _);

    /// <summary>
    /// A6 fix: the slots the log leaves pinned with no choice (a pinned slot whose choice was reset stays pinned on its model),
    /// by slot, as profile ids. Restored with <see cref="SessionModelSlots.RestorePin"/> when the session is opened again.
    /// </summary>
    public IReadOnlyDictionary<string, string> ModelPinsWithoutChoice(string sessionId)
    {
        var choices = ScanModelChoices(sessionId, null, out var pins);
        return pins.Where(pin => !choices.ContainsKey(pin.Key)).ToDictionary(pin => pin.Key, pin => pin.Value, StringComparer.Ordinal);
    }

    private Dictionary<string, ModelChoice> ScanModelChoices(string sessionId, long? through, out Dictionary<string, string> pins)
    {
        var choices = new Dictionary<string, ModelChoice>(StringComparer.Ordinal);
        var pinnedOnly = pins = new Dictionary<string, string>(StringComparer.Ordinal);
        var head = Log.GetHead(sessionId);
        if (head == 0)
            return choices;
        var last = Math.Min(through ?? head, head);
        // Paged: Read returns a page (256 events by default), and a choice can come late in a long session.
        for (var after = 0L; after < last;)
        {
            var page = Log.Read(sessionId, after, last, 512);
            if (page.Count == 0)
                break;
            after = page[^1].Sequence;
            foreach (var item in page)
                Take(item);
        }
        return choices;

        void Take(Corvid.Abstractions.Events.StoredEvent item)
        {
            if (item.Payload is not Corvid.Abstractions.Events.ModelSwitched switched || Profile(switched.NewProfile) is not { } to)
                return;
            if (switched.Reason == SwitchReason.Session.ToWireName())
            {
                choices[switched.Slot] = new ModelChoice(to, switched.Pinned == true);
                pinnedOnly.Remove(switched.Slot);
            }
            else if (switched.Reason == SwitchReason.DeEscalation.ToWireName()
                && choices.TryGetValue(switched.Slot, out var chosen) && (switched.Reset == true || chosen.Profile != to))
            {
                choices.Remove(switched.Slot);
                // A reset of a pinned slot logs its pin (Pinned true): the slot stays pinned where it is, without the choice.
                if (switched.Reset == true && switched.Pinned == true)
                    pinnedOnly[switched.Slot] = to;
            }
            else if (switched.Pinned is { } pinned && choices.TryGetValue(switched.Slot, out var held))
                choices[switched.Slot] = held with { Pinned = pinned };
            else if (switched.Pinned == false)
                pinnedOnly.Remove(switched.Slot);
        }

        static string? Profile(System.Text.Json.JsonElement snapshot)
        {
            try
            {
                return ModelProfile.FromSnapshot(snapshot).Id;
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
    }
}
