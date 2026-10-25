using System.Reflection;
using System.Text;
using Corvid.Abstractions.Events;
using Corvid.Host.Configuration;
using Corvid.Kernel.Events;
using Corvid.Kernel.Traces;

namespace Corvid.Host.Audit;

/// <summary>
/// Trace export (C-027): sessions of the event log as Beacon-schema JSONL, one event per line, moved here from
/// <c>corvid traces export</c> so the CLI and the windowed app's Audit write the same trace. It reads the log and writes
/// nothing to it; a file it creates is the owner's alone and never replaces an existing one.
/// </summary>
public static class TraceExport
{
    // IEventLog.Read pages; one page at a time keeps a long session's memory flat.
    private const int Page = 4096;

    /// <summary>The export options for this user's configuration: the workspace, the exporting program's version and the OS.</summary>
    /// <param name="version">
    /// The version the trace records for the program that exported it: the CLI passes its own assembly's, as <c>corvid
    /// traces export</c> always has; Corvid.Host's when null.
    /// </param>
    public static BeaconExportOptions Options(CorvidOptions options, BeaconRetention retention, bool includeLocalMemory, string? version = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new BeaconExportOptions(CorvidPaths.Workspace(options), version ?? VersionOf(typeof(TraceExport).Assembly), BeaconExportOptions.CurrentOs)
        {
            Retention = retention,
            IncludeLocalMemory = includeLocalMemory,
        };
    }

    /// <summary>Where a trace goes when the caller names no file: <c>traces/</c> under the data directory.</summary>
    public static string Directory(CorvidOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Path.Combine(CorvidPaths.DataDirectory(options), "traces");
    }

    /// <summary>The events of each session in turn, oldest first, through the head captured as each session starts.</summary>
    public static IEnumerable<StoredEvent> Read(IEventLog log, IReadOnlyList<string> sessions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(sessions);
        foreach (var session in sessions)
        {
            // A head captured first keeps paging stable while the agent appends.
            var head = log.GetHead(session, cancellationToken);
            for (long after = 0; after < head;)
            {
                var page = log.Read(session, after, head, Page, cancellationToken);
                if (page.Count == 0)
                    break;
                foreach (var stored in page)
                    yield return stored;
                after = page[^1].Sequence;
            }
        }
    }

    /// <summary>Writes the sessions' trace to <paramref name="output"/>, a line per event; returns how many lines it wrote.</summary>
    public static async Task<int> WriteAsync(IEventLog log, IReadOnlyList<string> sessions, BeaconExportOptions options, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        var count = 0;
        var newline = "\n"u8.ToArray();
        foreach (var line in BeaconExporter.Export(Read(log, sessions, cancellationToken), options))
        {
            await output.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(newline, cancellationToken).ConfigureAwait(false);
            count++;
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    /// <summary>
    /// A new file only its owner can read and write. A trace holds conversation content, and an existing file is never
    /// overwritten (an <see cref="IOException"/> when one is there).
    /// </summary>
    public static FileStream CreateOwnerOnly(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    /// <summary>An assembly's informational version ("1.0.0+abc123"), as a trace records it; empty when it has none.</summary>
    public static string VersionOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
    }
}
