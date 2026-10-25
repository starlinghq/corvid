using System.Reflection;
using Corvid.Cli.Chat;
using Corvid.Host;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace Corvid.Cli;

/// <summary>Raised on Ctrl-C. The chat cancels the running turn, or leaves when no turn runs.</summary>
public sealed class CliInterrupts
{
    public event Action? Interrupted;

    /// <summary>Returns whether anything handled the interrupt; when nothing listens, Ctrl-C should end the process.</summary>
    public bool Raise()
    {
        if (Interrupted is not { } handlers)
            return false;
        handlers();
        return true;
    }
}

/// <summary>What a command runs against: the terminal, its input, and (for tests) changes to the host's services.</summary>
public sealed record CliContext(IAnsiConsole Console, IChatInput Input)
{
    public CliInterrupts Interrupts { get; init; } = new();

    /// <summary>Applied after Corvid's own registrations: tests replace the model providers here.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>The process's byte streams, for <c>corvid acp</c>; null means standard input, output and error.</summary>
    public CliStreams? Streams { get; init; }
}

/// <summary>Raw standard streams: the ACP transport is bytes on stdin and stdout, with logs on stderr.</summary>
public sealed record CliStreams(Stream Input, Stream Output, TextWriter Error)
{
    public static CliStreams Standard() => new(Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error);
}

public static class ExitCodes
{
    public const int Success = 0;
    /// <summary>Something failed; <c>corvid scan</c> gives it too when a rule could not be evaluated.</summary>
    public const int Failure = Corvid.Host.Audit.LogScan.FailureExitCode;
    public const int Usage = 2;
    /// <summary>A configured model is not in the model cache; the message says how to fetch it.</summary>
    public const int ModelsMissing = 3;
    /// <summary><c>corvid scan</c> found a match at or above <c>--fail-on</c> (C-023).</summary>
    public const int Findings = Corvid.Host.Audit.LogScan.FindingsExitCode;
}

/// <summary>
/// <c>corvid</c> (plan §17 row 1.2): <c>chat</c>, <c>model</c>, <c>doctor</c>, <c>mcp</c>, <c>evals</c>, <c>acp</c>, <c>serve</c>, <c>gateway</c>, <c>traces</c>, <c>scan</c> and <c>egress</c>, with typed configuration
/// validated before anything runs.
/// </summary>
public static class CorvidCli
{
    public const string Usage = """
        usage: corvid [command] [options]

        commands:
          chat                       talk to Corvid (the default)      [--session <id>] [--verbose]
          model [list]               models, their slots and whether they are in the cache
          model switch <slot> <model>  bind a slot by default (written to the user config file)
          model download <model>...  fetch models explicitly (--all for every model); the only command that downloads
          doctor                     check models, sandbox, Vault and configuration     [--thorough]
          mcp [list]                 start each configured MCP server and list the tools it adds
          mcp diff <server> <tool>   a held tool definition against its pin, and the command that takes it
          mcp accept <server> <tool|--all> [--hash <hex>]   pin a held definition (starts only that server)
          mcp allow <server> <tool> --hash <hex>   offer a flagged or withheld definition as an untrusted tool
          mcp forget <server|--unconfigured>   drop a server's pins (its next start is trust on first use)
          mcp serve                  Corvid as an MCP server on stdin/stdout: memory search and skills, read-only
          gateway [run]              run the messaging gateway (Telegram, Discord, Slack) until Ctrl-C
          gateway status             enabled adapters, whether one runs, deliveries waiting, pairings
          gateway pair [list]        people waiting to pair, and who is paired
          gateway pair approve <code> --owner|--guest   pair the sender of a code as the owner, or as a guest (outside content)
          gateway pair revoke <adapter> <user>   remove a pairing
          gateway pair tools <adapter> <user> <tool>... | --adapter   a guest's own tool allowlist, or the adapter's again
          evals <command> ...        calibration tooling (corvid-evals): calibrate, report, replay, export-decisions,
                                     export-trajectories; --log defaults to this user's event log
          traces export              the event log as Beacon-schema JSONL on stdout     [--session <id> | --all]
                                     [--retention full|metadata] [--include-local-memory] [--out <file>]
          acp                        an Agent Client Protocol agent on stdin/stdout, for Zed, JetBrains and VS Code
          serve                      the OpenAI-compatible endpoint (/v1/chat/completions, /v1/models) on Corvid:Serve:Url,
                                     loopback only unless Corvid:Serve:TokenSecret names a Vault secret
          scan                       the detection rules over the event log      [--session <id>] [--fail-on <severity>] [--json]
                                     exit 4 when a finding is at or above --fail-on (info, low, medium, high, critical)
          egress [list]              the egress ledger: each request to a gated (cloud) model, newest first   [--session <id>] [--limit <n>]
          egress show <n>            one request as it was sent, after redaction

        options:
          --config <file>            the configuration file (default $XDG_CONFIG_HOME/corvid/corvid.json, or $CORVID_CONFIG)
          --set <key>=<value>        override a setting, e.g. --set Models:Slots:generator:Model=qwen3.5-0.8b
          --help, --version
        """;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CliContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(context);
        var console = context.Console;

        CliArguments parsed;
        try
        {
            parsed = CliArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            console.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            console.WriteLine(Usage);
            return ExitCodes.Usage;
        }
        if (parsed.Help)
        {
            console.WriteLine(Usage);
            return ExitCodes.Success;
        }
        if (parsed.Version)
        {
            console.WriteLine("corvid " + (typeof(CorvidCli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0"));
            return ExitCodes.Success;
        }

        // How this process names itself to another that finds a lock it holds (a session's driver, the scheduler; G-12, U-8).
        ProcessLock.ProcessName = "corvid " + parsed.Command;
        var builder = CorvidHost.CreateBuilder(parsed.Overrides, parsed.ConfigFile);
        // Only `corvid gateway [run]` starts the gateway; its other verbs work on the stores alone.
        if (GatewayCommand.Runs(parsed))
            builder.Services.AddCorvidGateway();
        context.ConfigureServices?.Invoke(builder.Services);
        using var host = builder.Build();
        try
        {
            return parsed.Command switch
            {
                "chat" => await ChatCommand.RunAsync(host, parsed, context, cancellationToken).ConfigureAwait(false),
                "model" or "models" => await ModelCommand.RunAsync(host, parsed, context, cancellationToken).ConfigureAwait(false),
                "doctor" => await DoctorCommand.RunAsync(host, parsed, context, cancellationToken).ConfigureAwait(false),
                "evals" => await EvalsCommand.RunAsync(host, parsed, context, cancellationToken).ConfigureAwait(false),
                "mcp" => await McpCommand.RunAsync(host, parsed, context, cancellationToken).ConfigureAwait(false),
                "acp" => await AcpCommand.RunAsync(host, context, cancellationToken).ConfigureAwait(false),
                "serve" => await ServeCommand.RunAsync(host, context, cancellationToken).ConfigureAwait(false),
                "gateway" => await GatewayCommand.RunAsync(host, parsed, context, cancellationToken).ConfigureAwait(false),
                "scan" => ScanCommand.Run(host, parsed, context, cancellationToken),
                "egress" => EgressCommand.Run(host, parsed, context, cancellationToken),
                "traces" => await TracesCommand.RunAsync(host, parsed, context, cancellationToken).ConfigureAwait(false),
                _ => UnknownCommand(console, parsed.Command),
            };
        }
        catch (Microsoft.Extensions.Options.OptionsValidationException exception)
        {
            console.MarkupLine($"[red]The configuration is not valid[/] ({Markup.Escape(parsed.ConfigFile ?? CorvidPaths.UserConfigFile)}):");
            foreach (var failure in exception.Failures)
                console.MarkupLine($"  [red]•[/] {Markup.Escape(failure)}");
            return ExitCodes.Usage;
        }
    }

    private static int UnknownCommand(IAnsiConsole console, string command)
    {
        console.MarkupLine($"[red]Unknown command '{Markup.Escape(command)}'.[/]");
        console.WriteLine(Usage);
        return ExitCodes.Usage;
    }

    /// <summary>Starts the host: validates the configuration (<c>ValidateOnStart</c>) and starts telemetry export.</summary>
    internal static Task StartAsync(IHost host, CancellationToken cancellationToken) => host.StartAsync(cancellationToken);
}

/// <summary>The parsed command line. Options may come before or after the command.</summary>
public sealed record CliArguments(string Command, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string?> Overrides,
    string? ConfigFile, bool Help, bool Version)
{
    public bool Flag(string name) => Arguments.Contains(name, StringComparer.Ordinal);

    public string? Option(string name)
    {
        var index = Arguments.ToList().IndexOf(name);
        return index >= 0 && index + 1 < Arguments.Count ? Arguments[index + 1] : null;
    }

    /// <summary>Arguments that are not flags or option values.</summary>
    public string[] Positional(params string[] valued)
    {
        var positional = new List<string>();
        for (var i = 0; i < Arguments.Count; i++)
        {
            if (valued.Contains(Arguments[i]))
                i++;
            else if (!Arguments[i].StartsWith("--", StringComparison.Ordinal))
                positional.Add(Arguments[i]);
        }
        return [.. positional];
    }

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var rest = new List<string>();
        string? command = null;
        string? config = null;
        bool help = false, version = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                // corvid-evals has its own --help.
                case "--help" or "-h" when command != "evals":
                    help = true;
                    break;
                // Chat's option may come without the command (`corvid --session x` resumes a chat).
                case "--session" when command is null or "chat":
                    rest.Add(arg);
                    rest.Add(Value(args, ref i, arg));
                    break;
                case "--version" when command is null:
                    version = true;
                    break;
                case "--config":
                    config = Value(args, ref i, arg);
                    break;
                case "--set":
                    var setting = Value(args, ref i, arg);
                    var equals = setting.IndexOf('=');
                    if (equals <= 0)
                        throw new ArgumentException($"--set takes key=value, not '{setting}'.");
                    var key = setting[..equals].Trim();
                    if (!key.StartsWith(CorvidOptions.Section + ":", StringComparison.OrdinalIgnoreCase))
                        key = CorvidOptions.Section + ":" + key;
                    overrides[key] = setting[(equals + 1)..];
                    break;
                default:
                    if (command is null && !arg.StartsWith('-'))
                        command = arg;
                    else
                        rest.Add(arg);
                    break;
            }
        }
        return new CliArguments(command ?? "chat", rest, overrides, config is null ? null : CorvidPaths.ExpandHome(config), help, version);
    }

    private static string Value(IReadOnlyList<string> args, ref int i, string option) =>
        ++i < args.Count ? args[i] : throw new ArgumentException($"{option} needs a value.");
}
