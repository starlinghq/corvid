namespace Corvid.Kernel.Turns;

/// <summary>
/// A turn <c>lesson_opportunity</c> said holds something a future session in the project should know (C-025): the events
/// from its user message to its answer.
/// </summary>
/// <param name="Tainted">The turn held outside content (§10.3): a lesson drafted from it is always left for review.</param>
/// <param name="LocalOnly">The turn's prompt held local-only memory (§10.4): a lesson drafted from it is local-only too.</param>
public sealed record LessonOpportunity(string SessionId, string ProjectId, long FromSequence, long ThroughSequence, string DecisionId,
    string Request, bool Tainted = false, bool LocalOnly = false);

/// <summary>Where the turn loop queues lesson opportunities. Drafting runs later, in the background.</summary>
public interface ILessonLearning
{
    void Queue(LessonOpportunity opportunity);
}
