using System.Globalization;

namespace Corvid.App.Core.Skills;

/// <summary>
/// The Skills area's demo at the scenario's moment (2026-10-24 14:05): eight skills, the one <c>cli-9f2e</c> loaded at
/// 14:03 among them (the 900 skill tokens its context panel bills), the nightly-tidy job's sweep it ran at 09:00, a skill
/// imported from a folder (outside content), two agent skills idle past the curator's 90 days; one draft waiting, which
/// <c>cli-77b0</c>'s finished Vault policy task produced at 13:41 and whose replay passed; the store's git history with its
/// diffs; eval runs; and Hermes' four skills, two of which the scanner refuses. Approving, rejecting, reverting, pinning,
/// archiving and importing change this state as the engine would, so flow F12 can be driven end to end. Nothing is
/// persisted, no folder is read and no git runs.
/// </summary>
public sealed class DemoSkillsService : ISkillsService
{
    /// <summary>The draft waiting at the demo's start.</summary>
    public const string DraftId = "d-7";

    private readonly TimeProvider _time;
    private readonly List<SkillRecord> _skills = [];
    private readonly List<SkillDraft> _drafts = [];
    private readonly List<(SkillCommit Commit, List<SkillCommitFile> Files)> _log = [];
    private readonly List<EvalRun> _evals = [];
    private readonly Dictionary<string, string> _rejections = new(StringComparer.Ordinal);
    private readonly HashSet<string> _imported = new(StringComparer.Ordinal);
    private int _nextRevision = 0x41;

    public DemoSkillsService(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        Seed();
    }

    /// <summary>Why each rejected draft was dropped, by id: what the engine would record.</summary>
    public IReadOnlyDictionary<string, string> Rejections => _rejections;

    public Task<SkillsSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SkillsSnapshot([.. _skills], [.. _drafts], "~/.local/share/corvid/skills", []));
    }

    public Task<SkillRecord> ApproveDraftAsync(string draftId, string? editedContent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draft = _drafts.Find(d => d.Id == draftId) ?? throw new InvalidOperationException($"No draft '{draftId}' is waiting for review.");
        var content = editedContent ?? draft.Content;
        if (SafeText.HasHidden(content))
            throw new InvalidOperationException("The SKILL.md holds hidden characters; the scanner refuses it.");
        var edited = editedContent is not null && !string.Equals(editedContent, draft.Content, StringComparison.Ordinal);
        // An edited draft is the user's text: it is written as theirs, which also keeps the curator off it.
        var author = edited ? "user" : "agent";
        var revision = NextRevision();
        var now = _time.GetUtcNow();
        var existing = _skills.FindIndex(s => s.Name == draft.Skill);
        var record = new SkillRecord(draft.Skill, SkillsText.Description(content), author, revision, null, 0, false, !edited, true, 0, content)
        {
            Path = draft.Skill,
            RequiresTools = existing >= 0 ? _skills[existing].RequiresTools : ["read_file", "write_file", "shell"],
        };
        if (existing >= 0)
            _skills[existing] = record with { LastUsed = _skills[existing].LastUsed, Uses = _skills[existing].Uses, Pinned = _skills[existing].Pinned };
        else
            _skills.Add(record);
        _drafts.Remove(draft);
        Commit(new SkillCommit(revision, author, now,
            (existing >= 0 ? "Update " : "Add skill ") + draft.Skill + (edited ? " (edited and approved by the user)" : " (approved by the user)"), [draft.Skill]),
            new SkillCommitFile(draft.Skill + "/SKILL.md", draft.Previous, content));
        return Task.FromResult(existing >= 0 ? _skills[existing] : record);
    }

    public Task RejectDraftAsync(string draftId, string reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draft = _drafts.Find(d => d.Id == draftId) ?? throw new InvalidOperationException($"No draft '{draftId}' is waiting for review.");
        _drafts.Remove(draft);
        _rejections[draftId] = reason;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SkillCommit>> LogAsync(string? skill, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Newest first; commits in the same minute (the frozen clock) by the order they were made.
        IReadOnlyList<SkillCommit> commits = [.. _log.Select((c, index) => (c.Commit, index)).Where(c => skill is null || c.Commit.Skills.Contains(skill))
            .OrderByDescending(c => c.Commit.Date).ThenByDescending(c => c.index).Select(c => c.Commit).Take(limit)];
        return Task.FromResult(commits);
    }

    public Task<SkillCommitDetail> ShowAsync(string revision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = _log.Find(c => c.Commit.Revision == revision);
        if (entry.Commit is null)
            throw new InvalidOperationException($"No revision {revision} in the skills store.");
        return Task.FromResult(new SkillCommitDetail(entry.Commit, [.. entry.Files]));
    }

    public Task<SkillCommit> RevertAsync(string revision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = _log.Find(c => c.Commit.Revision == revision);
        if (entry.Commit is null)
            throw new InvalidOperationException($"No revision {revision} in the skills store.");
        var files = new List<SkillCommitFile>();
        foreach (var file in entry.Files)
        {
            var name = file.Path.Split('/')[0];
            var index = _skills.FindIndex(s => s.Name == name);
            var current = index >= 0 ? _skills[index].Content : null;
            if (!string.Equals(current, file.NewText, StringComparison.Ordinal))
                throw new InvalidOperationException($"{name} changed after {Short(revision)}; revert the later commits first.");
            files.Add(new SkillCommitFile(file.Path, current, file.OldText));
            if (file.OldText is null)
            {
                if (index >= 0)
                    _skills.RemoveAt(index);
            }
            else if (index >= 0)
            {
                // The files it touched are now the user's (/skills revert says so).
                _skills[index] = _skills[index] with
                {
                    Content = file.OldText, Description = SkillsText.Description(file.OldText), Author = "user", AgentOwned = false,
                };
            }
            else
            {
                _skills.Add(new SkillRecord(name, SkillsText.Description(file.OldText), "user", "", null, 0, false, false, true, 0, file.OldText) { Path = name });
            }
        }

        var commit = new SkillCommit(NextRevision(), "user", _time.GetUtcNow(), $"Revert \"{entry.Commit.Subject}\"", entry.Commit.Skills);
        for (var i = 0; i < _skills.Count; i++)
        {
            if (commit.Skills.Contains(_skills[i].Name))
                _skills[i] = _skills[i] with { Revision = commit.Revision };
        }

        Commit(commit, [.. files]);
        return Task.FromResult(commit);
    }

    public Task<IReadOnlyList<EvalRun>> EvalHistoryAsync(string skill, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<EvalRun> runs = [.. _evals.Where(r => r.Skill == skill).OrderByDescending(r => r.At).Take(limit)];
        return Task.FromResult(runs);
    }

    public Task<EvalRun?> RunEvalsAsync(string skill, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = _skills.FindIndex(s => s.Name == skill);
        if (index < 0)
            throw new InvalidOperationException($"There is no skill named '{skill}'.");
        var record = _skills[index];
        if (record.EvalCases == 0)
            return Task.FromResult<EvalRun?>(null);
        // The demo's turns behave as they did last time: the same cases, the same outcomes, at the current revision.
        var last = _evals.Where(r => r.Skill == skill).OrderByDescending(r => r.At).FirstOrDefault();
        var cases = last?.Cases ?? [];
        var run = new EvalRun(skill, record.Revision, _time.GetUtcNow(), cases);
        _evals.Add(run);
        _skills[index] = record with { LastEval = run.Score };
        return Task.FromResult<EvalRun?>(run);
    }

    public Task SetPinnedAsync(string skill, bool pinned, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = _skills.FindIndex(s => s.Name == skill);
        if (index < 0)
            throw new InvalidOperationException($"There is no skill named '{skill}'.");
        _skills[index] = _skills[index] with { Pinned = pinned };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ArchiveAsync(IReadOnlyList<string> skills, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(skills);
        var archived = new List<string>();
        var files = new List<SkillCommitFile>();
        foreach (var name in skills)
        {
            var index = _skills.FindIndex(s => s.Name == name);
            if (index < 0)
                continue;
            var skill = _skills[index];
            if (!skill.AgentOwned || skill.Pinned)
                throw new InvalidOperationException($"{name} is {(skill.Pinned ? "pinned" : "not wholly the agent's")}; the curator archives only unpinned agent skills.");
            _skills.RemoveAt(index);
            archived.Add(name);
            files.Add(new SkillCommitFile(name + "/SKILL.md", skill.Content, null));
        }

        if (archived.Count > 0)
            Commit(new SkillCommit(NextRevision(), "user", _time.GetUtcNow(), "Archive " + string.Join(", ", archived) + " (idle)", archived), [.. files]);
        return Task.FromResult<IReadOnlyList<string>>(archived);
    }

    public Task<ImportScan> ScanImportAsync(SkillImportKind kind, string? folder, string? source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (kind == SkillImportKind.Hermes)
        {
            var location = string.IsNullOrWhiteSpace(folder) ? "~/.hermes" : folder.Trim();
            return Task.FromResult(new ImportScan(kind, location.TrimEnd('/') + "/skills", "hub:hermes", HermesCandidates()));
        }

        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("Give the folder that holds the skill folders, for example ~/skills.", nameof(folder));
        var hub = string.IsNullOrWhiteSpace(source) ? "local" : source.Trim();
        // The demo reads no folder: whatever is named, it finds the one skill folder below.
        return Task.FromResult(new ImportScan(kind, folder.Trim(), "hub:" + hub,
        [
            new ImportCandidate("f-1", "changelog-entry", "changelog-entry", "Write a CHANGELOG entry from the merged pull requests since the last tag.", []),
        ]));
    }

    public Task<SkillImportResult> ImportAsync(ImportScan scan, IReadOnlyList<string> candidateIds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(candidateIds);
        var imported = new List<SkillRecord>();
        var refused = new List<string>();
        var files = new List<SkillCommitFile>();
        var revision = NextRevision();
        foreach (var id in candidateIds)
        {
            var candidate = scan.Candidates.FirstOrDefault(c => c.Id == id);
            if (candidate is null)
                continue;
            if (candidate.IsRefused || SafeText.HasHidden(candidate.Name))
            {
                refused.Add($"{candidate.Path}: refused by the scanner");
                continue;
            }

            if (_skills.Any(s => s.Name == candidate.Name) || !_imported.Add(scan.Author + "/" + candidate.Name))
            {
                refused.Add($"{candidate.Path}: a skill named {candidate.Name} is already in the library");
                continue;
            }

            var content = Doc(candidate.Name, candidate.Description, "Imported from " + scan.Location + ".");
            var record = new SkillRecord(candidate.Name, candidate.Description, scan.Author, revision, null, 0, false, false, false, 0, content) { Path = candidate.Name };
            _skills.Add(record);
            imported.Add(record);
            files.Add(new SkillCommitFile(candidate.Name + "/SKILL.md", null, content));
        }

        if (imported.Count == 0)
            return Task.FromResult(new SkillImportResult([], refused, null));
        Commit(new SkillCommit(revision, scan.Author, _time.GetUtcNow(),
            $"Import {string.Join(", ", imported.Select(s => s.Name))} from {scan.Location}", [.. imported.Select(s => s.Name)]), [.. files]);
        return Task.FromResult(new SkillImportResult(imported, refused, revision));
    }

    // ---- the scenario ----

    private void Seed()
    {
        var ciOld = Doc("ci-flaky-triage", "Find why a test fails on CI only, without touching the production launcher.",
            """
            # CI-only test failures

            1. Read the workflow file: note the runner, the image and the environment.
            2. Re-run the one failing test with `--filter`, never the whole suite first.
            3. Compare the runner's environment with a local run and report the difference.
            """, "shell, read_file");
        var ci = Doc("ci-flaky-triage", "Find why a test fails on CI only, without touching the production launcher.",
            """
            # CI-only test failures

            1. Read the workflow file: note the runner, the image and the environment.
            2. Check `TMPDIR` and the working directory before re-running anything; sandboxed tests write there.
            3. Re-run the one failing test with `--filter`, never the whole suite first.
            4. Compare the runner's environment with a local run and report the difference.
            """, "shell, read_file");
        var tidyOld = Doc("nightly-tidy-sweep", "Tidy the workspace overnight: stale branches, build output, the scratch folder.",
            """
            # Nightly tidy

            - List branches merged into main and older than 14 days.
            - Remove `build/` and `artifacts/` outside the sandbox's own folders.
            - Summarise what was removed in the run's notice.
            """, "shell, list_dir");
        var tidy = Doc("nightly-tidy-sweep", "Tidy the workspace overnight: stale branches, build output, the scratch folder.",
            """
            # Nightly tidy

            - List branches merged into main and older than 14 days; never delete one with open work.
            - Remove `build/` and `artifacts/` outside the sandbox's own folders.
            - Stage what is worth keeping as memory, never write it.
            - Summarise what was removed in the run's notice.
            """, "shell, list_dir");
        var dotnet = Doc("dotnet-test-filter", "Run one .NET test, or one class of tests, by name or trait.",
            """
            # Filtering dotnet test

            Use `dotnet test --filter "FullyQualifiedName~<name>"` for a name and `--filter "Category=<trait>"` for a trait.
            Build once with `-c Release`, then pass `--no-build` to every run after it.
            """, "shell");
        var notesOld = Doc("release-notes", "Draft release notes from the merged pull requests since the last tag.",
            """
            # Release notes

            Group the merged pull requests by area: features, fixes, documentation.
            Lead with what a user notices. Link each pull request.
            """);
        var notes = Doc("release-notes", "Draft release notes from the merged pull requests since the last tag.",
            """
            # Release notes

            Start with the version line: `## <version> (<date>)`, the version as the tag gives it.
            Group the merged pull requests by area: features, fixes, documentation.
            Lead with what a user notices. Link each pull request.
            """);
        var vault = Doc("vault-cli", "Read, write and list secrets with the Vault command line.",
            """
            # Vault CLI

            Log in with the token helper, never with a token on the command line.
            `vault kv get`, `vault kv put` and `vault kv list` under the mount the policy grants.
            """, "shell");
        var bisect = Doc("git-bisect-run", "Find the commit that broke a test with git bisect run.",
            """
            # git bisect run

            Mark the known good and bad commits, then `git bisect run` the one failing test. Reset when done.
            """, "shell");
        var venv = Doc("python-venv-setup", "Create a Python virtual environment for a script and install its requirements.",
            """
            # Python venv

            `python -m venv .venv`, then `.venv/bin/pip install -r requirements.txt`. Never install into the system Python.
            """, "shell");
        var compose = Doc("docker-compose-up", "Bring up a compose project and wait for its health checks.",
            """
            # Compose up

            `docker compose up -d`, then poll `docker compose ps` until every service reports healthy.
            """, "shell");

        Add(Skill("ci-flaky-triage", ci, "agent", "a3f9c21e8d40", Today(14, 3), 12, pinned: true, evalCases: 3, last: "cli-9f2e", tools: ["shell", "read_file"]));
        Add(Skill("nightly-tidy-sweep", tidy, "agent", "3e1d07a9b2c4", Today(9, 0), 30, evalCases: 2, last: "job:nightly-tidy:2026-10-24", tools: ["shell", "list_dir"]));
        Add(Skill("dotnet-test-filter", dotnet, "agent", "51c8d2a04e7f", Day(-1, 17, 20), 21, evalCases: 5, last: "cli-9f2e", tools: ["shell"]));
        Add(Skill("release-notes", notes, "user", "7b20e5f1c9aa", Day(-1, 10, 12), 5, evalCases: 2, last: "cli-a410"));
        Add(Skill("vault-cli", vault, "hub:local", "0d94bb7311c2", Day(-4, 16, 2), 3, trusted: false, last: "cli-77b0", tools: ["shell"]));
        Add(Skill("git-bisect-run", bisect, "user", "c41a9e07d5b3", Day(-24, 11, 30), 4, evalCases: 1, tools: ["shell"]));
        Add(Skill("python-venv-setup", venv, "agent", "9f02b7d1e6a8", Day(-116, 15, 45), 2, tools: ["shell"]));
        Add(Skill("docker-compose-up", compose, "agent", "e57c3a10b9d2", Day(-102, 10, 5), 1, tools: ["shell"]));

        Commit(new SkillCommit("9f02b7d1e6a8", "agent", Day(-116, 15, 50), "Add skill python-venv-setup", ["python-venv-setup"]), new SkillCommitFile("python-venv-setup/SKILL.md", null, venv));
        Commit(new SkillCommit("e57c3a10b9d2", "agent", Day(-102, 10, 9), "Add skill docker-compose-up", ["docker-compose-up"]), new SkillCommitFile("docker-compose-up/SKILL.md", null, compose));
        Commit(new SkillCommit("c41a9e07d5b3", "user", Day(-25, 18, 2), "Add skill git-bisect-run", ["git-bisect-run"]), new SkillCommitFile("git-bisect-run/SKILL.md", null, bisect));
        Commit(new SkillCommit("2d8e61f4a0b5", "user", Day(-12, 9, 40), "Add skill release-notes", ["release-notes"]), new SkillCommitFile("release-notes/SKILL.md", null, notesOld));
        Commit(new SkillCommit("f1a0c3d9e2b7", "agent", Day(-5, 16, 11), "Add skill ci-flaky-triage (approved by the user)", ["ci-flaky-triage"]), new SkillCommitFile("ci-flaky-triage/SKILL.md", null, ciOld));
        Commit(new SkillCommit("0d94bb7311c2", "hub:local", Day(-4, 15, 30), "Import vault-cli from ~/skills", ["vault-cli"]), new SkillCommitFile("vault-cli/SKILL.md", null, vault));
        Commit(new SkillCommit("b6d4e2a1c098", "agent", Day(-4, 9, 3), "Add skill nightly-tidy-sweep", ["nightly-tidy-sweep"]), new SkillCommitFile("nightly-tidy-sweep/SKILL.md", null, tidyOld));
        Commit(new SkillCommit("51c8d2a04e7f", "agent", Day(-3, 15, 2), "Add skill dotnet-test-filter (approved by the user)", ["dotnet-test-filter"]), new SkillCommitFile("dotnet-test-filter/SKILL.md", null, dotnet));
        Commit(new SkillCommit("7b20e5f1c9aa", "user", Day(-2, 11, 15), "Edit release-notes: the version line", ["release-notes"]), new SkillCommitFile("release-notes/SKILL.md", notesOld, notes));
        Commit(new SkillCommit("a3f9c21e8d40", "agent", Day(-1, 16, 40), "Update ci-flaky-triage: check TMPDIR before re-running", ["ci-flaky-triage"]), new SkillCommitFile("ci-flaky-triage/SKILL.md", ciOld, ci));
        Commit(new SkillCommit("3e1d07a9b2c4", "agent", Today(9, 2), "Update nightly-tidy-sweep (job:nightly-tidy)", ["nightly-tidy-sweep"]), new SkillCommitFile("nightly-tidy-sweep/SKILL.md", tidyOld, tidy));

        static ReplayResult Pass(params string[] tools) => new(true, tools, tools, "completed");
        Eval("ci-flaky-triage", "f1a0c3d9e2b7", Day(-5, 16, 12),
            ("workflow-env", Pass("read_file", "shell")),
            ("single-test", Pass("shell")),
            ("tmpdir", new ReplayResult(false, ["read_file", "shell"], ["shell", "shell", "read_file"], "completed", "ran the test twice before reading the workflow")));
        Eval("ci-flaky-triage", "a3f9c21e8d40", Day(-1, 16, 41),
            ("workflow-env", Pass("read_file", "shell")), ("single-test", Pass("shell")), ("tmpdir", Pass("read_file", "shell")));
        Eval("nightly-tidy-sweep", "3e1d07a9b2c4", Today(9, 3), ("merged-branches", Pass("shell")), ("build-output", Pass("list_dir", "shell")));
        Eval("dotnet-test-filter", "51c8d2a04e7f", Day(-3, 15, 4),
            ("by-name", Pass("shell")), ("by-class", Pass("shell")), ("by-trait", Pass("shell")), ("no-build", Pass("shell", "shell")),
            ("by-trait-and-name", new ReplayResult(false, ["shell"], ["shell", "shell"], "completed", "ran the whole suite first")));
        Eval("release-notes", "7b20e5f1c9aa", Day(-2, 11, 16), ("since-tag", Pass("shell", "write_file")), ("version-line", Pass("shell", "write_file")));
        Eval("git-bisect-run", "c41a9e07d5b3", Day(-25, 18, 3), ("bisect", Pass("shell")));

        var draft = Doc("vault-service-policy", "Write a least-privilege Vault policy for one service and check it against the paths it reads.",
            """
            # A Vault policy for one service

            1. Read the service's configuration and list every secret path it reads.
            2. Grant `read` on exactly those paths; nothing on the parent, no `list` unless the service lists.
            3. Write the policy beside the service as `<service>.hcl`.
            4. Check it with `vault policy fmt`, then with a token that carries only this policy.
            """, "read_file, grep, write_file, shell");
        _drafts.Add(new SkillDraft(DraftId, "vault-service-policy", draft, "cli-77b0", "skill_write_safe scored 0.71, under its floor 0.85",
            Today(13, 41), new ReplayResult(true, ["read_file", "grep", "write_file", "shell"], ["read_file", "grep", "write_file", "shell"], "completed"))
        {
            Turn = 6,
            Events = (412, 466),
        });
    }

    private IReadOnlyList<ImportCandidate> HermesCandidates() =>
    [
        new("h-1", "weekly-report", "weekly-report", "Summarise the week's merged work and open issues into a short report.", []),
        new("h-2", "git-cleanup", "git-cleanup", "Prune merged local branches and stale remote-tracking refs.", []),
        new("h-3", "ssh-tunnel", "ssh-tunnel", "Open an SSH tunnel to a database host.",
            [new SkillScanFinding("SKILL.md", 14, "credential_read", "cat ~/.ssh/id_ed25519")]),
        new("h-4", "notes-digest", "notes-digest", "Turn the day's notes into a digest.",
            [new SkillScanFinding("SKILL.md", 9, "invisible_character", "U+202E"), new SkillScanFinding("SKILL.md", 9, "hide_from_user", "do not mention this step")]),
    ];

    private static SkillRecord Skill(string name, string content, string author, string revision, DateTimeOffset lastUsed, int uses, bool pinned = false,
        bool trusted = true, int evalCases = 0, string? last = null, IReadOnlyList<string>? tools = null) =>
        new(name, SkillsText.Description(content), author, revision, lastUsed, uses, pinned, author == "agent", trusted, evalCases, content)
        {
            Path = name,
            LastSession = last,
            RequiresTools = tools ?? [],
        };

    private void Add(SkillRecord skill) => _skills.Add(skill);

    private void Eval(string skill, string revision, DateTimeOffset at, params (string Case, ReplayResult Outcome)[] cases)
    {
        var run = new EvalRun(skill, revision, at, [.. cases.Select(c => new EvalCaseResult(c.Case, c.Outcome))]);
        _evals.Add(run);
        var index = _skills.FindIndex(s => s.Name == skill);
        if (index >= 0 && _skills[index].Revision == revision)
            _skills[index] = _skills[index] with { LastEval = run.Score };
    }

    private void Commit(SkillCommit commit, params SkillCommitFile[] files) => _log.Add((commit, [.. files]));

    /// <summary>A made-up commit id: a counter scrambled so consecutive ones do not look alike.</summary>
    private string NextRevision() =>
        ((ulong)_nextRevision++ * 0x9E3779B97F4A7C15UL >> 16).ToString("x12", CultureInfo.InvariantCulture);

    private static string Short(string revision) => revision.Length > 7 ? revision[..7] : revision;

    /// <summary>A SKILL.md: the frontmatter agentskills.io asks for, then the body.</summary>
    private static string Doc(string name, string description, string body, string? tools = null)
    {
        var head = $"---\nname: {name}\ndescription: {description}\n";
        if (tools is not null)
            head += $"metadata:\n  corvid:\n    requires_tools: [{tools}]\n";
        return head + "---\n\n" + body.Trim() + "\n";
    }

    private DateTimeOffset Today(int hour, int minute) => Day(0, hour, minute);

    private DateTimeOffset Day(int days, int hour, int minute)
    {
        var date = _time.GetLocalNow().Date.AddDays(days);
        var local = new DateTime(date.Year, date.Month, date.Day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, _time.LocalTimeZone.GetUtcOffset(local));
    }
}
