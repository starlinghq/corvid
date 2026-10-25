namespace Corvid.Host.Composition;

/// <summary>When a setting a running runtime takes without a restart applies (<see cref="CorvidRuntime.LiveSettings"/>).</summary>
public enum LiveSetting
{
    /// <summary>From its next use in every session: the approval deadline from the next prompt.</summary>
    Now,

    /// <summary>In sessions opened from now on; a session already open keeps what it was opened with.</summary>
    NewSessions,
}
