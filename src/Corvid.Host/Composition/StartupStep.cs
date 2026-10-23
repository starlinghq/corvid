using System.Diagnostics;

namespace Corvid.Host.Composition;

/// <summary>
/// A stage of <see cref="CorvidRuntimeFactory.StartAsync(IProgress{StartupStep}?, CancellationToken)"/>, so a front end can
/// say what a start is doing: reported when the stage begins, with <see cref="Elapsed"/> null, and again when it ends. A
/// stage that throws is not reported as ended.
/// </summary>
public sealed record StartupStep(string Description, TimeSpan? Elapsed = null)
{
    internal static T Run<T>(IProgress<StartupStep>? progress, string description, Func<T> action)
    {
        if (progress is null)
            return action();
        progress.Report(new StartupStep(description));
        var started = Stopwatch.GetTimestamp();
        var result = action();
        progress.Report(new StartupStep(description, Stopwatch.GetElapsedTime(started)));
        return result;
    }
}
