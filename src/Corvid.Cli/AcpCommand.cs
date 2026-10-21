using Corvid.Acp;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Corvid.Cli;

/// <summary>
/// <c>corvid acp</c>: Corvid as an Agent Client Protocol agent on stdin and stdout, for an editor to start (plan §12). Standard
/// output carries protocol messages only; everything else goes to standard error. Models load in the background, so the
/// editor's <c>initialize</c> is answered at once; the workspace is the configured one (by default the directory the editor
/// starts Corvid in), and a session for another directory is refused.
/// </summary>
internal static class AcpCommand
{
    public static async Task<int> RunAsync(IHost host, CliContext context, CancellationToken cancellationToken)
    {
        await CorvidCli.StartAsync(host, cancellationToken).ConfigureAwait(false);
        var factory = host.Services.GetRequiredService<CorvidRuntimeFactory>();
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        var streams = context.Streams ?? CliStreams.Standard();
        return await AcpServer.RunAsync(new AcpServerOptions(factory.StartAsync, commands, streams.Input, streams.Output, streams.Error),
            cancellationToken).ConfigureAwait(false);
    }
}
