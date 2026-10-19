using Corvid.Cli;
using Corvid.Cli.Chat;
using Corvid.Host.Diagnostics;
using Spectre.Console;

// Chat loads GLiNER2, which needs glibc's mmap threshold set before the process starts: re-execute with it set (Linux).
// Nothing has been written yet, so the replacement process starts with a clean terminal.
if (args.Length == 0 || args[0] is "chat" || args[0] is "--session")
{
    if (GlibcMalloc.EnsureMmapThreshold() is { } reason)
        Console.Error.WriteLine($"corvid: {Doctor.MallocVariable} is not set ({reason}); GLiNER2 may use more memory than Starling supports.");
}

var interrupts = new CliInterrupts();
Console.CancelKeyPress += (_, e) =>
{
    // The chat decides: cancel the running turn, or leave at the prompt. A second Ctrl-C while leaving ends the process.
    e.Cancel = true;
    interrupts.Raise();
};
var context = new CliContext(AnsiConsole.Console, new ConsoleChatInput()) { Interrupts = interrupts };
return await CorvidCli.RunAsync(args, context);
