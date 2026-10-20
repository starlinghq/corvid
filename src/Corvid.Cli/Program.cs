using Corvid.Cli;
using Corvid.Cli.Chat;
using Corvid.Host.Diagnostics;
using Spectre.Console;

// Chat loads GLiNER2, which needs glibc's mmap threshold set before the process starts: re-execute with it set (Linux).
// Decided from the parsed command, since options may come before it. Nothing has been written yet, so the replacement
// process starts with a clean terminal.
bool loadsModels;
try
{
    loadsModels = CliArguments.Parse(args) is { Command: "chat", Help: false, Version: false };
}
catch (ArgumentException)
{
    // RunAsync reports the usage error.
    loadsModels = false;
}
if (loadsModels && GlibcMalloc.EnsureMmapThreshold() is { } reason)
    Console.Error.WriteLine($"corvid: {Doctor.MallocVariable} is not set ({reason}); GLiNER2 may use more memory than Starling supports.");

var interrupts = new CliInterrupts();
Console.CancelKeyPress += (_, e) =>
{
    // While a chat is open it decides: cancel the running turn, or leave at the prompt. Otherwise (loading, shutting down,
    // the other commands) Ctrl-C ends the process as usual.
    e.Cancel = interrupts.Raise();
};
var context = new CliContext(AnsiConsole.Console, new ConsoleChatInput()) { Interrupts = interrupts };
return await CorvidCli.RunAsync(args, context);
