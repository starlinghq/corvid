using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Corvid.Kernel.Jobs;

namespace Corvid.Tools.Builtin;

/// <summary>
/// Scheduling tools over the durable job store (plan §13): create, list and remove jobs. The scheduler runs them, each run
/// in a fresh session. Creating and removing are side effects, so the turn loop asks the user first. A job created while
/// the session holds outside content is marked tainted, and so are its runs.
/// </summary>
public sealed partial class CronTools
{
    private readonly JobStore _store;

    public CronTools(JobStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    [Tool("cron_create", Description = "Schedule a task for Corvid to do later or repeatedly. Schedules: " + JobSchedule.Grammar +
        ". An optional pre_run_script runs first, sandboxed and without network; the agent then runs only if its output needs attention.")]
    [SideEffect(SideEffects.Schedule)]
    public JsonNode Create(
        [Description("When to run, for example 'in 30m', 'weekdays at 9am', 'every 2 hours' or '0 9 * * 1-5'.")] string schedule,
        [Description("What Corvid should do at each run, as an instruction to itself.")] string prompt = "",
        [Description("A short name for the job.")] string? name = null,
        [Description("A shell command whose output decides whether the run needs the agent, for example a check for new files.")] string? pre_run_script = null,
        [Description("Run only the pre_run_script and keep its output; no model is used.")] bool no_agent = false,
        ToolCallContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(prompt);
        var title = string.IsNullOrWhiteSpace(name) ? Shorten(prompt.Length > 0 ? prompt : pre_run_script ?? "job") : name.Trim();
        ScheduledJob job;
        try
        {
            job = _store.Add(new NewJob(title, prompt, schedule, pre_run_script, no_agent, context?.SessionId, context?.Tainted ?? false));
        }
        catch (FormatException exception)
        {
            throw new ToolArgumentException(exception.Message, nameof(schedule));
        }
        catch (ArgumentException exception) when (exception is not ToolArgumentException)
        {
            throw new ToolArgumentException(exception.Message, no_agent ? nameof(pre_run_script) : nameof(prompt));
        }

        return Describe(job);
    }

    [Tool("cron_list", Description = "List the scheduled jobs with their next run and how the last run went.")]
    public JsonNode List([Description("Also list finished and removed jobs.")] bool include_finished = false)
    {
        var jobs = new JsonArray();
        foreach (var job in _store.List(include_finished))
        {
            var described = Describe(job);
            if (_store.Runs(job.JobId, 1) is [var last])
            {
                described["last_run"] = new JsonObject
                {
                    ["at"] = Local(last.StartedAt, job),
                    ["status"] = last.Status,
                };
            }

            jobs.Add((JsonNode)described);
        }

        return new JsonObject { ["jobs"] = jobs };
    }

    [Tool("cron_remove", Description = "Remove a scheduled job so it never runs again.")]
    [SideEffect(SideEffects.Schedule)]
    // Removal cannot be undone; the job would have to be created again.
    [Destructive]
    public JsonNode Remove([Description("The job_id from cron_list.")] string job_id)
    {
        if (string.IsNullOrWhiteSpace(job_id))
            throw new ToolArgumentException("Argument 'job_id' must not be empty.", nameof(job_id));
        if (!_store.Remove(job_id))
            throw new ToolArgumentException($"There is no active job '{job_id}'; call cron_list for the ids.", nameof(job_id));
        return new JsonObject { ["job_id"] = job_id, ["status"] = "removed" };
    }

    private static JsonObject Describe(ScheduledJob job)
    {
        var described = new JsonObject
        {
            ["job_id"] = job.JobId,
            ["name"] = job.Name,
            ["schedule"] = job.Schedule,
            ["state"] = job.State.ToString().ToLowerInvariant(),
            ["recurring"] = job.ParsedSchedule.Recurring,
        };
        if (job.NextRunAt is { } next)
            described["next_run"] = Local(next, job);
        if (job.Prompt.Length > 0)
            described["prompt"] = job.Prompt;
        if (job.Script is { } script)
            described["pre_run_script"] = script;
        if (job.NoAgent)
            described["no_agent"] = true;
        if (job.Paused)
            described["paused"] = true;
        return described;
    }

    /// <summary>A time in the job's own zone, as the tools and <c>/cron</c> show it.</summary>
    public static string Local(DateTimeOffset at, ScheduledJob job) =>
        TimeZoneInfo.ConvertTime(at, JobStore.FindZone(job.TimeZoneId)).ToString("yyyy-MM-dd'T'HH:mmzzz", CultureInfo.InvariantCulture);

    private static string Shorten(string text)
    {
        var line = text.Trim().Split('\n')[0];
        return line.Length <= 40 ? line : line[..40].TrimEnd() + "…";
    }
}
