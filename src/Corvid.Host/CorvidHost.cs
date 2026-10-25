using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Host.Diagnostics;
using Corvid.Host.Models;
using Corvid.Host.Telemetry;
using Corvid.Models.Starling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Corvid.Host;

/// <summary>
/// The composition root (plan §3.4): a generic host whose configuration and services every executable (the CLI now, the
/// gateway and ACP later) shares.
/// </summary>
public static class CorvidHost
{
    /// <summary>
    /// A host builder with Corvid's configuration sources (defaults, the user file, environment, then <paramref name="overrides"/>)
    /// and services. Logging is off by default: a terminal surface owns the console.
    /// </summary>
    /// <param name="overrides">Configuration keys such as <c>Corvid:Models:Slots:generator:Model</c>, from the command line.</param>
    public static HostApplicationBuilder CreateBuilder(IReadOnlyDictionary<string, string?>? overrides = null, string? configFile = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "corvid",
            ContentRootPath = AppContext.BaseDirectory,
        });
        var file = configFile ?? CorvidPaths.UserConfigFile;
        builder.Services.AddSingleton(new CorvidConfigFile(file));
        builder.Configuration
            .AddJsonFile(file, optional: true, reloadOnChange: false)
            // CORVID__Models__Cache becomes Corvid:Models:Cache (keys ignore case).
            .AddEnvironmentVariables()
            .AddInMemoryCollection(overrides ?? new Dictionary<string, string?>());
        builder.Services.AddCorvid(builder.Configuration);
        return builder;
    }

    /// <summary>
    /// Corvid's services: options validated at start, the model catalogue with Starling as the default provider, the
    /// runtime factory, the slash commands, the doctor, and OpenTelemetry export when an endpoint is configured.
    /// </summary>
    public static IServiceCollection AddCorvid(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<CorvidOptions>().Bind(configuration.GetSection(CorvidOptions.Section)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CorvidOptions>, CorvidOptionsValidator>());

        // Starling bindings by default (plan §1). Tests and later providers (C-015) add or replace IModelProvider registrations.
        // Read from configuration, not the validated options: the validator needs the catalogue, which needs this store.
        services.TryAddSingleton(provider => new StarlingModelStore(
            provider.GetRequiredService<IConfiguration>()[$"{CorvidOptions.Section}:Models:Cache"] is { Length: > 0 } cache
                ? CorvidPaths.ExpandHome(cache)
                : null));
        // C-029: placement read the same lenient way; CorvidOptionsValidator refuses what is wrong with it.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelProvider, StarlingModelProvider>(provider => new StarlingModelProvider(
            provider.GetRequiredService<StarlingModelStore>(), ReadPlacement(provider.GetRequiredService<IConfiguration>()))));
        // Remote models from Corvid:Models:Remote (C-015): local servers and cloud providers, keyed only through Vault.
        services.TryAddSingleton<RemoteModelProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelProvider, RemoteModelProvider>(provider => provider.GetRequiredService<RemoteModelProvider>()));
        services.TryAddSingleton(provider => new ModelCatalogue(provider.GetServices<IModelProvider>()));

        services.TryAddSingleton<ModelMetrics>();
        services.TryAddSingleton<TurnFeed>();
        services.TryAddSingleton<CorvidRuntimeFactory>();
        services.TryAddSingleton<Doctor>();
        // The one way to change corvid.json: validated as the next start would read it, then written atomically.
        services.TryAddSingleton(provider => new CorvidConfigWriter(
            provider.GetService<CorvidConfigFile>() ?? new CorvidConfigFile(CorvidPaths.UserConfigFile),
            provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<IValidateOptions<CorvidOptions>>()));
        services.TryAddSingleton(_ => CoreCommands.Register(new SlashCommandRegistry()));
        services.AddLogging(logging => logging.ClearProviders());

        var endpoint = configuration[$"{CorvidOptions.Section}:Telemetry:OtlpEndpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint) || !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            var service = configuration[$"{CorvidOptions.Section}:Telemetry:ServiceName"] is { Length: > 0 } name ? name : "corvid";
            services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(service))
                .WithTracing(tracing =>
                {
                    tracing.AddSource([.. HostTelemetry.Sources]);
                    // Without an endpoint here the exporter reads OTEL_EXPORTER_OTLP_ENDPOINT itself.
                    tracing.AddOtlpExporter(exporter =>
                    {
                        if (!string.IsNullOrWhiteSpace(endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                            exporter.Endpoint = uri;
                    });
                });
        }
        return services;
    }

    /// <summary>
    /// <c>Corvid:Models:Placement</c> without failing: an unparseable fallback reads as <see cref="PlacementFallback.Fail"/>
    /// and an unparseable device is passed on for the provider to ignore, because the validator reports both.
    /// </summary>
    internal static Dictionary<string, ModelPlacementSettings> ReadPlacement(IConfiguration configuration)
    {
        var placement = new Dictionary<string, ModelPlacementSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in configuration.GetSection($"{CorvidOptions.Section}:Models:Placement").GetChildren())
        {
            placement[model.Key] = new ModelPlacementSettings
            {
                Device = model["Device"] is { Length: > 0 } device ? device : Corvid.Models.ModelPlacement.Cpu,
                Fallback = Enum.TryParse<PlacementFallback>(model["Fallback"], ignoreCase: true, out var fallback) && Enum.IsDefined(fallback)
                    ? fallback
                    : PlacementFallback.Fail,
            };
        }
        return placement;
    }

    /// <summary>
    /// The gateway (C-020) as a hosted service: starting the host starts the runtime and the enabled adapters. Adapters
    /// registered as <see cref="Gateway.GatewayAdapterRegistration"/> in DI replace the built-in ones of the same name.
    /// </summary>
    public static IServiceCollection AddCorvidGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<GatewayService>();
        services.AddHostedService(provider => provider.GetRequiredService<GatewayService>());
        return services;
    }
}
