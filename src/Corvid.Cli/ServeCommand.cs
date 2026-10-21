using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace Corvid.Cli;

/// <summary>
/// <c>corvid serve</c>: the OpenAI-compatible endpoint (plan §12) until Ctrl-C. Loopback only unless a token secret is
/// configured; the token is checked through Corvid Vault, which must hold it when the endpoint starts.
/// </summary>
internal static class ServeCommand
{
    public static async Task<int> RunAsync(IHost host, CliContext context, CancellationToken cancellationToken)
    {
        var console = context.Console;
        await CorvidCli.StartAsync(host, cancellationToken).ConfigureAwait(false);
        var options = host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value;
        var settings = options.Serve;

        VaultTokenVerifier? verifier = null;
        if (settings.TokenSecret is { Length: > 0 } secret)
        {
            verifier = new VaultTokenVerifier(secret, options.Vault.SocketPath is { Length: > 0 } socket ? CorvidPaths.ExpandHome(socket) : null);
            bool exists;
            try
            {
                exists = await verifier.SecretExistsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                console.MarkupLine($"[red]Corvid Vault is not answering ({Markup.Escape(exception.Message)}); the token cannot be checked, so nothing is served.[/]");
                return ExitCodes.Failure;
            }
            if (!exists)
            {
                console.MarkupLine($"[red]Corvid Vault holds no secret named '{Markup.Escape(secret)}' (Corvid:Serve:TokenSecret).[/] " +
                    $"Store one with [bold]corvid-vault set {Markup.Escape(secret)}[/] (a random value of at least {VaultTokenVerifier.MinimumBytes} bytes).");
                return ExitCodes.Failure;
            }
        }

        CorvidRuntime runtime;
        try
        {
            runtime = await host.Services.GetRequiredService<CorvidRuntimeFactory>().StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ModelsUnavailableException exception)
        {
            ModelCommand.ReportMissing(console, exception.Missing);
            return ExitCodes.ModelsMissing;
        }

        await using (runtime.ConfigureAwait(false))
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            void OnInterrupt() => stop.Cancel();
            context.Interrupts.Interrupted += OnInterrupt;
            try
            {
                var app = OpenAIEndpoint.Build(runtime, new OpenAIEndpointOptions(settings, verifier));
                await using (app.ConfigureAwait(false))
                {
                    try
                    {
                        await app.StartAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException exception)
                    {
                        console.MarkupLine($"[red]Cannot listen on {Markup.Escape(settings.Url)}: {Markup.Escape(exception.Message)}[/]");
                        return ExitCodes.Failure;
                    }
                    var address = OpenAIEndpoint.ListeningOn(app).ToString().TrimEnd('/');
                    console.MarkupLine($"[bold]corvid serve[/] on [bold]{Markup.Escape(address)}/v1[/] (model '{OpenAIEndpoint.ModelId}', " +
                        $"{(verifier is null ? "loopback callers only" : "bearer token checked through Vault")}, approvals: {settings.Approvals}). " +
                        "Ctrl-C stops.");
                    foreach (var notice in runtime.DeciderNotices)
                        console.MarkupLine($"[yellow]{Markup.Escape(notice)}[/]");
                    try
                    {
                        await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                context.Interrupts.Interrupted -= OnInterrupt;
            }
            await runtime.MaintenanceIdleAsync().ConfigureAwait(false);
        }
        return ExitCodes.Success;
    }
}
