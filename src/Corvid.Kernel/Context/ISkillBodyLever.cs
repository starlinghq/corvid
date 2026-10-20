namespace Corvid.Kernel.Context;

/// <summary>
/// Plan §8 lever 2: once a skill has completed, its body (L1) no longer needs to be in the prompt. The turn loop asks this
/// before compacting, because dropping a body the log can reload costs nothing. Skills (C-017) implement it; until then
/// nothing loads a body, so nothing is bound.
/// </summary>
public interface ISkillBodyLever
{
    /// <summary>Drops the bodies of completed skills from what the session renders. True when anything was dropped.</summary>
    ValueTask<bool> DropCompletedAsync(string sessionId, CancellationToken cancellationToken);
}
