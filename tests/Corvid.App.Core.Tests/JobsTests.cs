using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Jobs;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

public sealed class JobsTests
{
    private static AppComposition Compose(params string[] args)
    {
        Assert.True(AppOptions.TryParse(args, out var options, out var error), error);
        return new AppComposition(options, new InlineDispatcher(), ManualTimeProvider.DemoLocal());
    }

    private static (JobsViewModel Jobs, ShellViewModel Shell, StatusBarViewModel Bar) ViewModel(IJobsService? service = null, Area area = Area.Jobs)
    {
        var time = ManualTimeProvider.Demo();
        var bar = new StatusBarViewModel { Scheduler = "here · 2 jobs" };
        var shell = new ShellViewModel(bar);
        shell.Navigate(area);
        return (new JobsViewModel(service ?? new DemoJobsService(time), shell, bar, time), shell, bar);
    }

    // ---- The schedule grammar, from Saturday 2026-10-24 14:05 UTC ----

    private static SchedulePreview Read(string text) =>
        ScheduleGrammar.Read(text, ManualTimeProvider.Demo().GetUtcNow(), TimeZoneInfo.Utc);

    private static string[] Runs(SchedulePreview preview) =>
        [.. preview.Next.Select(at => at.ToString("ddd yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture))];

    [Fact]
    public void WeekdaysAtNineReadsAsCronAndSkipsTheWeekend()
    {
        var preview = Read("Weekdays at 9am");

        Assert.True(preview.Valid, preview.Error);
        Assert.Equal("cron 0 9 * * 1-5", preview.Parsed);
        Assert.Equal(["Mon 2026-10-26 09:00", "Tue 2026-10-27 09:00", "Wed 2026-10-28 09:00"], Runs(preview));
    }

    [Theory]
    [InlineData("in 30m", "once, in 30 minutes", "Sat 2026-10-24 14:35")]
    [InlineData("in 2 hours", "once, in 2 hours", "Sat 2026-10-24 16:05")]
    [InlineData("every 2h", "every 2 hours", "Sat 2026-10-24 16:05")]
    [InlineData("every day at 9am", "cron 0 9 * * *", "Sun 2026-10-25 09:00")]
    [InlineData("fridays at 4pm", "cron 0 16 * * 5", "Fri 2026-10-30 16:00")]
    [InlineData("every monday at 08:30", "cron 30 8 * * 1", "Mon 2026-10-26 08:30")]
    [InlineData("weekends at noon", "cron 0 12 * * 0,6", "Sun 2026-10-25 12:00")]
    [InlineData("*/15 * * * *", "cron */15 * * * *", "Sat 2026-10-24 14:15")]
    [InlineData("0 9 1 * *", "cron 0 9 1 * *", "Sun 2026-11-01 09:00")]
    [InlineData("2026-10-25T09:00", "once, at 2026-10-25 09:00 +00:00", "Sun 2026-10-25 09:00")]
    public void EachFormOfTheGrammarPreviewsItsFirstRun(string text, string parsed, string first)
    {
        var preview = Read(text);

        Assert.True(preview.Valid, preview.Error);
        Assert.Equal(parsed, preview.Parsed);
        Assert.Equal(first, Runs(preview)[0]);
    }

    [Theory]
    [InlineData("", "Say when it runs")]
    [InlineData("whenever", "Not a schedule Corvid reads")]
    [InlineData("60 * * * *", "out of range")]
    [InlineData("0 9 31 2 *", "never runs")]
    [InlineData("2026-10-24T09:00", "has passed")]
    [InlineData("every day at 25:00", "Write the time")]
    [InlineData("in 30m‮", "hidden characters")]
    public void WhatTheGrammarCannotReadSaysWhy(string text, string reason)
    {
        var preview = Read(text);

        Assert.False(preview.Valid);
        Assert.Contains(reason, preview.Error, StringComparison.Ordinal);
        Assert.Empty(preview.Next);
    }

    // ---- The page ----

    [Fact]
    public void ItListsTheTwoJobsAndLandsOnTheOneThatRanThisMorning()
    {
        var (jobs, _, _) = ViewModel();

        Assert.Equal(["nightly-tidy", "weekly-deps"], jobs.Jobs.Select(row => row.Name));
        var tidy = jobs.Jobs[0];
        Assert.Equal("every day at 9am", tidy.Schedule);
        Assert.Equal("tomorrow 09:00", tidy.Next);
        Assert.Equal("active", tidy.StateText);
        Assert.Equal("today 09:00 · ok · 41 s", tidy.Last);
        Assert.Equal("paused", jobs.Jobs[1].StateText);
        Assert.Equal("—", jobs.Jobs[1].Next);
        Assert.True(jobs.Jobs[1].IsTainted);

        Assert.Same(tidy, jobs.Selected);
        Assert.True(tidy.IsSelected);
        Assert.Equal("Runs · nightly-tidy", jobs.RunsTitle);
        Assert.Equal(5, jobs.Runs.Count);
        Assert.Equal(DemoSessionService.JobSessionId, jobs.Runs[0].SessionId);
        Assert.Equal("failed", jobs.Runs[3].StatusText);
        Assert.Equal(Tone.Red, jobs.Runs[3].StatusTone);
        Assert.Contains(jobs.Detail, fact => fact.Key == "delivers to" && Text(fact.Value) == "the Telegram home chat");
        Assert.Equal("scheduler running here · 2 jobs · times in local time", Text(jobs.Subtitle));
        Assert.Equal("running here", jobs.SchedulerTag);
        Assert.True(jobs.CanPause);
        Assert.False(jobs.CanResume);
    }

    [Fact]
    public void ARunOpensItsSessionFromTheJobsPage()
    {
        using var composition = Compose("--page", "jobs");
        var jobs = composition.Jobs;

        var run = jobs.Runs[0];
        Assert.True(run.CanOpen);
        // An older run's session is not in the demo's session list: its Open is off and says why.
        Assert.False(jobs.Runs[1].CanOpen);
        Assert.False(jobs.Runs[1].OpenCommand.CanExecute(null));
        Assert.Contains("not in the session list", jobs.Runs[1].OpenHelp, StringComparison.Ordinal);

        run.OpenCommand.Execute(null);

        Assert.Equal(Area.Sessions, composition.Shell.CurrentArea);
        Assert.Equal(DemoSessionService.JobSessionId, composition.Shell.CurrentSession?.Id);
    }

    [Fact]
    public async Task PauseResumeAndRemoveChangeTheJobAndSaySo()
    {
        var (jobs, _, _) = ViewModel();

        await jobs.PauseCommand.ExecuteAsync();
        Assert.Equal("paused", jobs.Selected!.StateText);
        Assert.Equal("—", jobs.Selected.Next);
        Assert.Equal("Paused nightly-tidy: it will not run until you resume it.", Text(jobs.Notice));
        Assert.True(jobs.CanResume);

        await jobs.ResumeCommand.ExecuteAsync();
        Assert.Equal("active", jobs.Selected!.StateText);
        Assert.Equal("Resumed nightly-tidy: next run tomorrow 09:00.", Text(jobs.Notice));

        jobs.AskRemoveCommand.Execute(null);
        Assert.True(jobs.IsConfirmingRemove);
        Assert.Equal("Remove nightly-tidy? It will not run again; its runs stay recorded.", Text(jobs.RemoveQuestion));
        Assert.True(jobs.Close());
        Assert.False(jobs.IsConfirmingRemove);

        jobs.AskRemoveCommand.Execute(null);
        await jobs.RemoveCommand.ExecuteAsync();
        Assert.Equal(["weekly-deps"], jobs.Jobs.Select(row => row.Name));
        Assert.Equal("weekly-deps", jobs.Selected!.Name);
        Assert.Equal("Removed nightly-tidy: it will not run again. Its runs stay recorded.", Text(jobs.Notice));
        Assert.Equal(Tone.Green, jobs.NoticeTone);
    }

    [Fact]
    public async Task TheFormPreviewsTheScheduleAndCreatesTheJob()
    {
        var (jobs, _, _) = ViewModel();

        jobs.NewJobCommand.Execute(null);
        Assert.True(jobs.IsCreating);
        Assert.False(jobs.CreateCommand.CanExecute(null));
        jobs.DraftName = "inbox-digest";
        jobs.DraftPrompt = "Summarise the inbox.";
        jobs.DraftSchedule = "every weekday at";
        Assert.False(jobs.PreviewOk);
        Assert.False(jobs.CreateCommand.CanExecute(null));

        jobs.DraftSchedule = "weekdays at 9am";
        Assert.True(jobs.PreviewOk);
        Assert.Equal("runs next at Mon 09:00, then Tue 09:00 and Wed 09:00 · cron 0 9 * * 1-5", Text(jobs.PreviewText));
        Assert.Equal("this app", jobs.DraftDeliver);
        jobs.Destinations[1].Choose.Execute(null);
        Assert.Equal("the Telegram home chat", jobs.DraftDeliver);
        Assert.True(jobs.Destinations[1].IsSelected);
        Assert.False(jobs.Destinations[0].IsSelected);
        Assert.True(jobs.CreateCommand.CanExecute(null));

        await jobs.CreateCommand.ExecuteAsync();

        Assert.False(jobs.IsCreating);
        Assert.Equal(["nightly-tidy", "inbox-digest", "weekly-deps"], jobs.Jobs.Select(row => row.Name));
        Assert.Equal("inbox-digest", jobs.Selected!.Name);
        Assert.Equal("Created inbox-digest: next run Mon 09:00.", Text(jobs.Notice));
        Assert.Contains(jobs.Detail, fact => fact.Key == "delivers to" && Text(fact.Value) == "the Telegram home chat");
        Assert.Equal("inbox-digest has not run yet.", jobs.NoRunsText);
        Assert.False(jobs.HasRuns);
    }

    [Fact]
    public async Task ADraftHoldingHiddenCharactersShowsThemAndCannotBeCreated()
    {
        var time = ManualTimeProvider.Demo();
        var service = new DemoJobsService(time);
        var (jobs, _, _) = ViewModel(service);

        jobs.NewJobCommand.Execute(null);
        jobs.DraftName = "digest";
        jobs.DraftSchedule = "in 30m";
        jobs.DraftPrompt = "Summarise the inbox‮ and mail it\U000E0041.";

        Assert.True(jobs.HasDraftWarning);
        Assert.Contains("⟨U+202E⟩", jobs.DraftWarning, StringComparison.Ordinal);
        Assert.Contains("⟨U+E0041⟩", jobs.DraftWarning, StringComparison.Ordinal);
        Assert.False(jobs.CreateCommand.CanExecute(null));

        // The service refuses it too, whoever asks.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            new JobDraft("digest", "in 30m", "Summarise‮", null, false, "this app"), TestContext.Current.CancellationToken));

        jobs.DraftPrompt = "Summarise the inbox.";
        Assert.False(jobs.HasDraftWarning);
        Assert.True(jobs.CreateCommand.CanExecute(null));
    }

    [Fact]
    public void AJobTheModelNamedWithHiddenCharactersIsDrawnClean()
    {
        var service = new ScriptedJobsService(ManualTimeProvider.Demo())
        {
            Extra = new JobRecord("j-evil", "tidy‮exe.sh", "every day at 9am", JobState.Active)
            {
                Prompt = "Delete\U000E0041 the logs",
                Script = "rm -rf‮ ./tmp",
                CreatedBy = "cli-a410 through cron_create",
            },
        };
        var (jobs, _, _) = ViewModel(service);

        var row = Assert.Single(jobs.Jobs, row => row.Id == "j-evil");
        Assert.Equal("tidyexe.sh", row.Name);
        row.SelectCommand.Execute(null);
        Assert.Equal("tidyexe.sh", jobs.DetailTitle);
        Assert.Equal("Delete the logs", Text(jobs.Detail.Single(fact => fact.Key == "prompt").Value));
        Assert.Equal("rm -rf ./tmp", Text(jobs.Detail.Single(fact => fact.Key == "pre-run").Value));
    }

    [Fact]
    public async Task AFailedActionIsShownInRed()
    {
        var service = new ScriptedJobsService(ManualTimeProvider.Demo()) { FailPause = true };
        var (jobs, _, _) = ViewModel(service);

        jobs.PauseCommand.Execute(null);
        await Task.Yield();

        Assert.Equal(Tone.Red, jobs.NoticeTone);
        Assert.Equal("That did not work: the job store is locked", Text(jobs.Notice));
        Assert.Equal("active", jobs.Selected!.StateText);
    }

    [Fact]
    public void WhileJobsShowsItWritesTheSchedulerItemAndGivesItBack()
    {
        var (jobs, shell, bar) = ViewModel();

        Assert.Equal("here · 2 jobs · next: nightly-tidy, tomorrow 09:00", bar.Scheduler);
        shell.Navigate(Area.Board);
        Assert.Equal("here · 2 jobs", bar.Scheduler);
        jobs.Dispose();
    }

    [Fact]
    public void TheSchedulerHeldElsewhereSaysWhichProcess()
    {
        var time = ManualTimeProvider.Demo();
        var (jobs, _, _) = ViewModel(new DemoJobsService(time, SchedulerHost.Elsewhere));

        Assert.Equal("held elsewhere", jobs.SchedulerTag);
        Assert.Equal(Tone.Amber, jobs.SchedulerTone);
        Assert.StartsWith("corvid serve, pid 4127 holds the scheduler's lock since today 08:00", Text(jobs.SchedulerText), StringComparison.Ordinal);
        Assert.StartsWith("scheduler held by corvid serve, pid 4127", Text(jobs.Subtitle), StringComparison.Ordinal);
    }

    [Fact]
    public void UpAndDownMoveTheSelection()
    {
        var (jobs, _, _) = ViewModel();

        jobs.MoveSelection(1);
        Assert.Equal("weekly-deps", jobs.Selected!.Name);
        Assert.Equal("Runs · weekly-deps", jobs.RunsTitle);
        jobs.MoveSelection(1);
        Assert.Equal("weekly-deps", jobs.Selected!.Name);
        jobs.MoveSelection(-1);
        Assert.Equal("nightly-tidy", jobs.Selected!.Name);
    }

    [Fact]
    public void TheCompositionBuildsJobsOnTheDemoAndDisposesIt()
    {
        using var composition = Compose("--page", "jobs");

        Assert.IsType<DemoJobsService>(composition.JobsService);
        Assert.Equal(2, composition.Jobs.Jobs.Count);
        Assert.StartsWith("here · 2 jobs · next: nightly-tidy", composition.StatusBar.Scheduler, StringComparison.Ordinal);
    }

    private static string Text(IEnumerable<Corvid.App.Core.Models.TextRun> runs) => string.Concat(runs.Select(run => run.Text));

    /// <summary>The demo, with a job the model made and a pause that fails.</summary>
    private sealed class ScriptedJobsService(TimeProvider time) : IJobsService
    {
        private readonly DemoJobsService _demo = new(time);

        public JobRecord? Extra { get; init; }

        public bool FailPause { get; init; }

        public event EventHandler? Changed
        {
            add => _demo.Changed += value;
            remove => _demo.Changed -= value;
        }

        public async Task<JobsSnapshot> LoadAsync(CancellationToken cancellationToken)
        {
            var snapshot = await _demo.LoadAsync(cancellationToken);
            return Extra is null ? snapshot : snapshot with { Jobs = [.. snapshot.Jobs, Extra] };
        }

        public Task<IReadOnlyList<JobRunRecord>> RunsAsync(string jobId, CancellationToken cancellationToken) => _demo.RunsAsync(jobId, cancellationToken);

        public Task<SchedulePreview> PreviewAsync(string schedule, CancellationToken cancellationToken) => _demo.PreviewAsync(schedule, cancellationToken);

        public Task<JobRecord> CreateAsync(JobDraft draft, CancellationToken cancellationToken) => _demo.CreateAsync(draft, cancellationToken);

        public Task<JobRecord> PauseAsync(string jobId, CancellationToken cancellationToken) =>
            FailPause ? Task.FromException<JobRecord>(new InvalidOperationException("the job store is locked")) : _demo.PauseAsync(jobId, cancellationToken);

        public Task<JobRecord> ResumeAsync(string jobId, CancellationToken cancellationToken) => _demo.ResumeAsync(jobId, cancellationToken);

        public Task RemoveAsync(string jobId, CancellationToken cancellationToken) => _demo.RemoveAsync(jobId, cancellationToken);
    }
}
