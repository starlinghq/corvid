using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Corvid.Approvals;
using Corvid.Host.Commands;
using Corvid.Host.Telemetry;
using Corvid.Kernel.Sessions;
using Corvid.Kernel.Turns;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Corvid.Cli.Chat;

/// <summary>
/// The one writer to the terminal during a chat (scrollback-first, plan §12): streamed answer text, a live tool panel while
/// tools run, inline approval prompts, and command output. Everything is printed into the scrollback; the only region that
/// redraws is the tool panel, and it is closed (left in the scrollback as it last looked) before anything else is written.
/// Events arrive from several threads at once (read-only tools run in parallel), so every write takes one lock.
/// </summary>
internal sealed class ChatRenderer : ITurnObserver
{
    private readonly IAnsiConsole _console;
    private readonly bool _verbose;
    private readonly bool _live;
    private readonly Lock _gate = new();
    private readonly StringBuilder _generation = new();
    private ImmutableList<ToolRow> _tools = [];
    private bool _textOpen;
    private Task? _panel;
    private TaskCompletionSource? _panelStop;

    public ChatRenderer(IAnsiConsole console, bool verbose = false, bool? live = null)
    {
        _console = console;
        _verbose = verbose;
        _live = live ?? (console.Profile.Capabilities.Interactive && console.Profile.Capabilities.Ansi);
    }

    /// <param name="Tail">K-2: the running call's last line of output, cleaned for the terminal.</param>
    private sealed record ToolRow(string CallId, string Tool, bool Approval, long Started, string? Outcome = null, TimeSpan Elapsed = default,
        string? Tail = null)
    {
        public bool Running => Outcome is null;
    }

    public void Banner(string session, string generator, int tools)
    {
        lock (_gate)
        {
            _console.Write(new Rule($"[bold]corvid[/] [grey]· {Markup.Escape(generator)} · {tools} tool(s) · session {Markup.Escape(session)}[/]")
                .LeftJustified().RuleStyle("grey"));
            _console.MarkupLine("[grey]/help lists commands · /model picks a model · Ctrl-C cancels a turn · /exit leaves[/]");
        }
    }

    public void Prompt()
    {
        lock (_gate)
        {
            _console.WriteLine();
            _console.Markup("[bold blue]›[/] ");
        }
    }

    /// <summary>Shows a line the terminal did not echo (piped input), so the transcript reads as typed.</summary>
    public void Echo(string line)
    {
        lock (_gate)
            _console.WriteLine(line);
    }

    public void BeginTurn()
    {
        lock (_gate)
        {
            _generation.Clear();
            _tools = [];
            Volatile.Write(ref _stepBox, null);
            _textOpen = false;
        }
    }

    // ---- ITurnObserver: called from the turn's threads ----

    public void GenerationStarted()
    {
        lock (_gate)
        {
            EndText();
            _generation.Clear();
        }
    }

    public void Text(string delta)
    {
        lock (_gate)
        {
            StopPanel();
            if (!_textOpen)
            {
                _console.WriteLine();
                _textOpen = true;
            }
            _console.Write(new Text(delta));
            _generation.Append(delta);
        }
    }

    public void ToolStarted(string callId, string tool, bool approval)
    {
        lock (_gate)
        {
            EndText();
            _tools = _tools.Add(new ToolRow(callId, tool, approval, Stopwatch.GetTimestamp()));
            if (_live)
                StartPanel();
            else
                _console.MarkupLine($"  [yellow]⏺[/] {Markup.Escape(tool)} [grey]{(approval ? "awaiting approval" : "running")}[/]");
        }
    }

    public void ToolFinished(string callId, string tool, string outcome, TimeSpan elapsed)
    {
        lock (_gate)
        {
            var row = _tools.FirstOrDefault(item => item.CallId == callId && item.Running);
            if (row is not null)
                _tools = _tools.Replace(row, row with { Outcome = outcome, Elapsed = elapsed });
            else
                _tools = _tools.Add(new ToolRow(callId, tool, false, Stopwatch.GetTimestamp(), outcome, elapsed));
            if (!_live)
                _console.MarkupLine($"  {Mark(outcome)} {Markup.Escape(tool)} [grey]{Markup.Escape(Describe(outcome))} · {Duration(elapsed)}[/]");
            else if (_tools.All(item => !item.Running))
                StopPanel();
        }
    }

    /// <summary>Turn progress: the live panel's header says the step ("step 2 of 8").</summary>
    public void Progress(int step, int maxSteps)
    {
        Volatile.Write(ref _stepBox, new StepBox(step, maxSteps));
    }

    /// <summary>
    /// K-2: a running call's output. The live panel shows its last line under the call; without a live panel nothing is
    /// printed (the scrollback gets the result, not every line twice).
    /// </summary>
    public void ToolOutput(string callId, string tool, string tail)
    {
        if (!_live)
            return;
        lock (_gate)
        {
            var row = _tools.FirstOrDefault(item => item.CallId == callId && item.Running);
            if (row is not null)
                _tools = _tools.Replace(row, row with { Tail = LastLine(tail) });
        }
    }

    public void Decided(CheckpointDecisions decisions)
    {
        if (!_verbose)
            return;
        lock (_gate)
        {
            StopPanel();
            EndText();
            var answers = string.Join("  ", decisions.Questions.Select(question =>
                $"{question.Question} {question.Confidence.ToString("0.00", CultureInfo.InvariantCulture)} {(question.Acted ? "→ " : "↓ ")}{question.Action}"));
            _console.MarkupLine($"  [grey]{Markup.Escape(decisions.Checkpoint)} ({Markup.Escape(decisions.Decider)}): {Markup.Escape(answers)}[/]");
        }
    }

    // ---- the end of a turn ----

    public void EndTurn(Delivery delivery)
    {
        lock (_gate)
        {
            StopPanel();
            EndText();
            if (delivery.Error is { } error)
            {
                _console.MarkupLine($"[red]The turn failed:[/] {Markup.Escape(error.Message)}");
                return;
            }
            var result = delivery.Result!;
            // Text the generator streamed is already on screen; anything else (a fixed message, a reply produced while tools
            // were on offer and not streamed) is printed now.
            if (result.Text is { } text && !string.Equals(Normalize(text), Normalize(_generation.ToString()), StringComparison.Ordinal))
            {
                _console.WriteLine();
                _console.Write(new Text(text, result.Status == TurnStatus.Failed ? new Style(Color.Red) : Style.Plain));
                _console.WriteLine();
            }
            // K-6: a clarifying question's choices, answered on the next line by number or in words.
            if (result.Question is { Options.IsDefaultOrEmpty: false } question)
                _console.MarkupLine("[blue]?[/] " + string.Join(" · ", question.Options.Select((option, i) =>
                    $"[green]{i + 1}[/] {Markup.Escape(Clean(option))}")) + " [grey](a number picks it, or answer in your words)[/]");
            _console.MarkupLine($"[grey]{Markup.Escape(Footer(result))}[/]");
        }
    }

    /// <summary>
    /// K-6: the line typed after a clarifying question with choices: a choice's number becomes that choice (as the model
    /// offered it); anything else is the answer as typed.
    /// </summary>
    public static string Answer(ClarifyingQuestion? question, string line) =>
        question is { Options.IsDefaultOrEmpty: false } asked
        && int.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pick) && pick >= 1 && pick <= asked.Options.Length
            ? asked.Options[pick - 1]
            : line;

    public void Cancelled()
    {
        lock (_gate)
        {
            StopPanel();
            EndText();
            _console.MarkupLine("[yellow]Cancelling the turn…[/]");
        }
    }

    // ---- approvals and commands ----

    public void ShowApproval(ApprovalPrompt prompt)
    {
        lock (_gate)
        {
            StopPanel();
            EndText();
            var body = new List<IRenderable> { new Markup($"[bold]{Markup.Escape(prompt.Analysis.Text)}[/]") };
            foreach (var command in prompt.Analysis.Commands)
            {
                var targets = command.Targets.IsDefaultOrEmpty ? "" : " → " + string.Join(", ", command.Targets.Select(target => target.Value));
                body.Add(new Markup($"[grey]{Markup.Escape(command.Verb + (command.Subcommand is { } sub ? " " + sub : ""))}: " +
                    $"{Markup.Escape(command.Effects.ToString().ToLowerInvariant())}{Markup.Escape(targets)}[/]"));
            }
            foreach (var reason in prompt.Reasons)
                body.Add(new Markup($"[yellow]•[/] {Markup.Escape(reason.Detail)} [grey]({Markup.Escape(reason.Code)})[/]"));
            body.Add(new Markup($"[grey]Denied automatically at {prompt.Deadline.ToLocalTime():HH:mm:ss} if unanswered.[/]"));
            _console.Write(new Panel(new Rows(body)).Header("[yellow] approval needed [/]").BorderColor(Color.Yellow));
            _console.Markup(Choices(prompt.Offered) + " ");
        }
    }

    public void ApprovalAnswered(string text)
    {
        lock (_gate)
            _console.MarkupLine($"[grey]{Markup.Escape(text)}[/]");
    }

    public void Output(CommandOutput output)
    {
        lock (_gate)
        {
            StopPanel();
            EndText();
            Render(_console, output);
        }
    }

    /// <summary>Asks for one of a command's numbered choices, answered on the next line like an approval.</summary>
    public void Choose(CommandOutput output)
    {
        lock (_gate)
            _console.Markup($"{Markup.Escape(output.ChoicePrompt ?? "Pick")} " +
                string.Join(" · ", output.Choices.Select(choice => $"[green]{Markup.Escape(choice.Key)}[/] {Markup.Escape(choice.Label)}")) +
                " [grey](Enter keeps it)[/] › ");
    }

    public void Notice(string markup)
    {
        lock (_gate)
            _console.MarkupLine(markup);
    }

    /// <summary>Command output for any surface that writes to a Spectre console (chat, <c>corvid model</c>).</summary>
    public static void Render(IAnsiConsole console, CommandOutput output)
    {
        var color = output.Tone switch
        {
            OutputTone.Success => "green",
            OutputTone.Warning => "yellow",
            OutputTone.Error => "red",
            _ => null,
        };
        if (output.Title is { } title)
            console.MarkupLine($"[bold]{Markup.Escape(title)}[/]");
        if (output.Table is { } table)
        {
            var grid = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
            foreach (var column in table.Columns)
                grid.AddColumn(new TableColumn(Markup.Escape(column)));
            foreach (var row in table.Rows)
                grid.AddRow([.. table.Columns.Select((_, i) => (IRenderable)new Text(i < row.Length ? row[i] : ""))]);
            console.Write(grid);
        }
        foreach (var line in output.Lines)
            console.MarkupLine(color is null ? Markup.Escape(line) : $"[{color}]{Markup.Escape(line)}[/]");
    }

    internal static string Choices(ImmutableArray<ApprovalScope> offered)
    {
        var choices = new List<string> { "[green]y[/] once" };
        if (offered.Contains(ApprovalScope.Session))
            choices.Add("[green]s[/] this session");
        if (offered.Contains(ApprovalScope.Always))
            choices.Add("[green]a[/] always");
        choices.Add("[red]n[/] deny");
        return "Allow? " + string.Join(" · ", choices) + " ›";
    }

    // ---- internals (call with the lock held) ----

    private void EndText()
    {
        if (_textOpen)
            _console.WriteLine();
        _textOpen = false;
    }

    private void StartPanel()
    {
        if (_panel is not null)
            return;
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _panelStop = stop;
        _panel = _console.Live(BuildPanel(_tools)).AutoClear(false).StartAsync(async context =>
        {
            while (!stop.Task.IsCompleted)
            {
                // Reads the immutable snapshot without the lock: the lock holder may be waiting for this loop to end.
                context.UpdateTarget(BuildPanel(Volatile.Read(ref _tools)));
                context.Refresh();
                await Task.WhenAny(stop.Task, Task.Delay(100)).ConfigureAwait(false);
            }
            context.UpdateTarget(BuildPanel(Volatile.Read(ref _tools)));
            context.Refresh();
        });
    }

    private void StopPanel()
    {
        if (_panel is null)
            return;
        _panelStop!.TrySetResult();
        _panel.GetAwaiter().GetResult();
        _panel = null;
        _panelStop = null;
    }

    private IRenderable BuildPanel(ImmutableList<ToolRow> tools)
    {
        var table = new Table().Border(TableBorder.None).HideHeaders().AddColumn("").AddColumn("").AddColumn("");
        foreach (var tool in tools)
        {
            table.AddRow(
                new Markup(tool.Running ? "[yellow]⏺[/]" : Mark(tool.Outcome!)),
                new Text(tool.Tool),
                new Markup($"[grey]{Markup.Escape(tool.Running
                    ? (tool.Approval ? "awaiting approval · " : "running · ") + Duration(Stopwatch.GetElapsedTime(tool.Started))
                    : Describe(tool.Outcome!) + " · " + Duration(tool.Elapsed))}[/]"));
            // K-2: the running call's last line, dim, under it.
            if (tool.Running && tool.Tail is { Length: > 0 } tail)
                table.AddRow(new Text(""), new Text(""), new Text(tail, new Style(Color.Grey)));
        }
        // Read without the lock, like the rows: a snapshot of a value type.
        var header = Volatile.Read(ref _stepBox) is { } step
            ? string.Create(CultureInfo.InvariantCulture, $"[grey] tools · step {step.Step} of {step.MaxSteps} [/]")
            : "[grey] tools [/]";
        return new Panel(table).Header(header).BorderColor(Color.Grey);
    }

    // Turn progress: the step the turn is on and its budget, for the panel's header; boxed so the panel thread reads it whole.
    private StepBox? _stepBox;

    private sealed record StepBox(int Step, int MaxSteps);

    /// <summary>A tail's last non-empty line for one terminal row: control and format characters dropped, at most 100 characters.</summary>
    internal static string LastLine(string tail)
    {
        var line = tail.Split('\n').LastOrDefault(item => item.Trim().Length > 0) ?? "";
        line = Clean(line).Trim();
        return line.Length > 100 ? line[..99] + "…" : line;
    }

    /// <summary>Untrusted text for the terminal: control characters (an escape sequence's ESC among them) and format characters dropped.</summary>
    private static string Clean(string text) =>
        string.Concat(text.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format));

    private static string Mark(string outcome) => outcome == "succeeded" ? "[green]✓[/]" : outcome == "denied" ? "[yellow]⊘[/]" : "[red]✗[/]";

    private static string Describe(string outcome) => outcome.Replace('_', ' ');

    private static string Duration(TimeSpan elapsed) => CoreCommands.FormatDuration(elapsed);

    private static string Normalize(string text) => text.Trim().ReplaceLineEndings("\n");

    private static string Footer(TurnResult result)
    {
        var parts = new List<string> { TurnRunner.StatusName(result.Status).Replace('_', ' ') };
        if (result.Steps > 0)
            parts.Add($"{result.Steps} step(s), {result.ToolCalls} tool call(s)");
        parts.AddRange(result.Escalations.Select(escalation => escalation.EndsWith(": " + TurnRunner.PrivacyRetreat, StringComparison.Ordinal)
            ? $"kept local on {escalation[..^(TurnRunner.PrivacyRetreat.Length + 2)]}: the privacy policy refused the cloud model"
            : "escalated to " + escalation));
        if (result.MemoryWrite is { } memory)
            parts.Add("memory " + memory + (memory.StartsWith("staged", StringComparison.Ordinal) ? " (/memory pending)" : ""));
        if (result.Tainted)
            parts.Add("tainted: outside content in context");
        return "· " + string.Join(" · ", parts);
    }
}
