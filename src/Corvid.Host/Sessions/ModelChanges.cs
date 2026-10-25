using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Models;

namespace Corvid.Host.Sessions;

/// <summary>A5: <see cref="ModelChanged"/> updates from a switch's result (live) or its <c>ModelSwitched</c> event (replay).</summary>
public static class ModelChanges
{
    /// <summary>The update for a switch made in this process.</summary>
    public static ModelChanged FromResult(SwitchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var to = result.To ?? result.From;
        return new ModelChanged(result.Slot, result.From.Id, to.Id, result.Reason.ToWireName(), result.Sequence)
        {
            SessionDefault = result.SessionDefaultChanged,
            Pinned = result.Pinned,
            Cloud = to.Privacy == PrivacyClass.Cloud,
        };
    }

    /// <summary>The update for a logged switch; a profile the log cannot read shows as <c>unknown</c>.</summary>
    public static ModelChanged FromLog(ModelSwitched switched, long sequence)
    {
        ArgumentNullException.ThrowIfNull(switched);
        var to = Read(switched.NewProfile);
        return new ModelChanged(switched.Slot, Read(switched.OldProfile)?.Id ?? "unknown", to?.Id ?? "unknown", switched.Reason, sequence)
        {
            // A5 review: a reset changes the session's default too (back to the configured one), as its live notice says.
            SessionDefault = switched.Reason == SwitchReason.Session.ToWireName() || switched.Reset == true,
            Pinned = switched.Pinned == true,
            Cloud = to?.Privacy == PrivacyClass.Cloud,
        };
    }

    private static ModelProfile? Read(JsonElement snapshot)
    {
        try
        {
            return ModelProfile.FromSnapshot(snapshot);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
