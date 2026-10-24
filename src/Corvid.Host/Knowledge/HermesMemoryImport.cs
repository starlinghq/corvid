using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Corvid.Memory;
using Corvid.Skills;

namespace Corvid.Host.Knowledge;

/// <summary>One entry of a Hermes memory file, as an import offers it: its text, its id, and what the scanner found.</summary>
/// <param name="Index">Its place in the file, from 1.</param>
/// <param name="Id">
/// A hash of its file and text (<see cref="HermesMemoryImport.EntryId"/>), so staging takes exactly what a dry run showed:
/// an entry changed since has another id.
/// </param>
/// <param name="Findings">The skill scanner's findings; any of them refuses the entry, as Hermes blocks a threat pattern.</param>
public sealed record HermesMemoryEntry(string File, int Index, string Text, string Id, ImmutableArray<ScanFinding> Findings)
{
    public bool Flagged => !Findings.IsEmpty;
}

/// <summary>What a Hermes memories directory holds: its files (with why one was refused) and their entries.</summary>
public sealed record HermesMemoryScan(string Directory, ImmutableArray<HermesMemoryFile> Files, ImmutableArray<HermesMemoryEntry> Entries);

/// <summary>
/// The Hermes memory import (D-9, C-027) without its surface: read <c>MEMORY.md</c> and <c>USER.md</c>, screen each entry with
/// the skill scanner, and stage what passes as a local-only fact proposal for review, never a write. <c>/memory import
/// hermes</c> prints it; the windowed app's Memory page shows the dry run and stages the entries the person picked.
/// </summary>
public static class HermesMemoryImport
{
    /// <summary>The reason a staged entry records: <c>import:hermes MEMORY.md</c>.</summary>
    public static string Reason(string file) => "import:hermes " + file;

    /// <summary>Reads and screens the entries of a memories directory (<c>&lt;hermes home&gt;/memories</c>); nothing is staged.</summary>
    public static HermesMemoryScan Scan(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var files = HermesMemory.Read(directory);
        var entries = ImmutableArray.CreateBuilder<HermesMemoryEntry>();
        foreach (var file in files.Where(file => file.Problem is null))
            for (var i = 0; i < file.Entries.Length; i++)
                entries.Add(new HermesMemoryEntry(file.File, i + 1, file.Entries[i], EntryId(file.File, file.Entries[i]),
                    SkillScanner.Scan(file.File, file.Entries[i])));
        return new HermesMemoryScan(directory, files, entries.ToImmutable());
    }

    /// <summary>Stages one entry the scanner passed, as <see cref="MemoryWriter.StageImportAsync"/> does.</summary>
    /// <exception cref="ArgumentException">The scanner flagged it, or the writer refused it (too long, a credential value).</exception>
    public static Task<MemoryImportResult> StageAsync(MemoryWriter writer, string sessionId, HermesMemoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Flagged)
            throw new ArgumentException("The scanner flagged the entry; it is not imported.", nameof(entry));
        return writer.StageImportAsync(sessionId, entry.Text, Reason(entry.File), cancellationToken);
    }

    /// <summary><c>h-</c> and the first 12 hex characters of the SHA-256 of the file name, a line feed and the text.</summary>
    public static string EntryId(string file, string text) =>
        "h-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file + "\n" + text)))[..12];
}
