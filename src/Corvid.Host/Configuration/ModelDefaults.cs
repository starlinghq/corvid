using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Corvid.Host.Commands;
using Corvid.Host.Models;
using Corvid.Models;
using Microsoft.Extensions.Configuration;

namespace Corvid.Host.Configuration;

/// <summary>What <see cref="ModelDefaults.Write"/> did: written or refused, with the file and any key this run's environment overrides.</summary>
/// <param name="Written">The file now names the model for the slot (or already did: <see cref="AlreadySet"/>).</param>
/// <param name="Slot">The slot.</param>
/// <param name="Model">The catalogue's name for the model (as the file has it); the name asked for when refused before it was found.</param>
/// <param name="Detail">Why it was refused, in a sentence; null when written.</param>
public sealed record ModelDefaultWrite(bool Written, string Slot, string Model, string? Detail = null)
{
    /// <summary>The configuration file.</summary>
    public string Path { get; init; } = "";

    /// <summary>The file already named the model for the slot: nothing was written.</summary>
    public bool AlreadySet { get; init; }

    /// <summary>The model is not usable offline yet (not downloaded, no key): new sessions need it first.</summary>
    public ModelAvailability? NotReady { get; init; }

    /// <summary>Keys this run's environment or <c>--set</c> overrides, so the next start goes on using those.</summary>
    public ImmutableArray<string> ShadowedKeys { get; init; } = [];
}

/// <summary>
/// A5: the "default for new sessions" choice, shared by <c>corvid model switch</c>, <c>/model --default</c> (the CLI, ACP and
/// the app's composer), the Models page and the Alt+P palette. It writes <c>Corvid:Models:Slots:&lt;slot&gt;:Model</c> to
/// <c>corvid.json</c> through <see cref="CorvidConfigWriter"/> (validated as the next start would read it), taking the model off
/// the slot's escalation ladder when it is on it (a model is not its own escalation). It never switches a session: "this
/// session" is <see cref="Composition.CorvidSession.SetModelAsync"/>, a separate choice. A running runtime starts new sessions
/// on it at once through <see cref="Composition.CorvidRuntime.SetDefaultModel"/> (<see cref="Composition.CorvidRuntime.WriteDefaultModel"/>
/// does both). Model names are catalogue text; messages escape them for a terminal.
/// </summary>
public static class ModelDefaults
{
    /// <summary>The configuration key a slot's default model is written to.</summary>
    public static string Key(string slot) => $"{CorvidOptions.Section}:Models:Slots:{slot}:Model";

    /// <summary>Validates and writes the slot's default model; never throws for a bad slot or model (the answer says why).</summary>
    /// <param name="takeOffLadder">
    /// Take the model off the slot's escalation ladder when it is on it (the app, <c>/model --default</c>); false leaves the
    /// ladder, so the validator refuses a default that is also its own escalation (<c>corvid model switch</c>).
    /// </param>
    public static ModelDefaultWrite Write(CorvidConfigWriter writer, ModelCatalogue catalogue, string slot, string model, bool takeOffLadder = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(model);
        if (!ModelSlots.All.Contains(slot, StringComparer.Ordinal))
            return new(false, slot, model, $"'{TerminalText.Visible(slot)}' is not a slot; the slots are {string.Join(", ", ModelSlots.All)}.") { Path = writer.Path };
        if (!catalogue.TryFind(model, out var descriptor))
            return new(false, slot, model, $"No model is named '{TerminalText.Visible(model)}'. `corvid model` lists them.") { Path = writer.Path };
        // Whether it can serve the slot is the validator's to say, in its words, with the rest of the next start's checks.
        var availability = catalogue.Check(descriptor.Name);
        var notReady = availability.IsReady ? null : availability;
        CorvidOptions next;
        try
        {
            next = writer.NextStart().GetSection(CorvidOptions.Section).Get<CorvidOptions>() ?? new CorvidOptions();
        }
        catch (InvalidDataException exception)
        {
            return new(false, slot, descriptor.Name, exception.Message) { Path = writer.Path };
        }
        var settings = next.Models.EffectiveSlots().GetValueOrDefault(slot);
        if (settings is not null && string.Equals(settings.Model, descriptor.Name, StringComparison.OrdinalIgnoreCase))
            return new(true, slot, descriptor.Name) { Path = writer.Path, AlreadySet = true, NotReady = notReady };

        var edits = new List<ConfigEdit> { ConfigEdit.Set(Key(slot), descriptor.Name) };
        // A model cannot be its slot's own escalation: one on the ladder leaves it as it becomes the default.
        if (takeOffLadder && settings is not null && settings.Escalation.Contains(descriptor.Name, StringComparer.OrdinalIgnoreCase))
        {
            var ladder = settings.Escalation.Where(step => !string.Equals(step, descriptor.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var key = $"{CorvidOptions.Section}:Models:Slots:{slot}:Escalation";
            edits.Add(ladder.Length > 0 ? new ConfigEdit(key, new JsonArray([.. ladder.Select(step => (JsonNode)JsonValue.Create(step))])) : ConfigEdit.Remove(key));
        }
        var result = writer.Apply(edits);
        if (!result.Written)
            return new(false, slot, descriptor.Name, "Not written: " + result.FailureText) { Path = result.Path };
        return new(true, slot, descriptor.Name) { Path = result.Path, NotReady = notReady, ShadowedKeys = result.ShadowedKeys };
    }
}
