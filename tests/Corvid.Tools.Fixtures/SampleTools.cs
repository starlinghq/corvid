using System.ComponentModel;
using Corvid.Vault.Client;

namespace Corvid.Tools.Fixtures;

public enum Priority
{
    Low,
    Normal,
    High,
}

/// <summary>Instance tools: the generated wrappers call this object.</summary>
public sealed partial class NoteTools
{
    private readonly List<(string Text, Priority Priority, string[] Tags)> _notes = [];

    public IReadOnlyList<(string Text, Priority Priority, string[] Tags)> Notes => _notes;

    public CancellationToken LastToken { get; private set; }

    [Tool("add_note", Description = "Save a note for later.")]
    [SideEffect(SideEffects.Memory)]
    public string AddNote(
        [Description("The note text.")] string text,
        [Description("How urgent the note is.")] Priority priority = Priority.Normal,
        string[]? tags = null)
    {
        _notes.Add((text, priority, tags ?? []));
        return $"saved note {_notes.Count}";
    }

    [Tool]
    [Description("List saved notes, oldest first.")]
    public string ListNotes([Description("Most notes to return.")] int limit = 10) =>
        string.Join("\n", _notes.Take(limit).Select(n => n.Text));

    [Tool(Description = "Delete every saved note.")]
    [SideEffect(SideEffects.Memory)]
    [Destructive]
    public async Task<int> ClearNotesAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        LastToken = cancellationToken;
        int count = _notes.Count;
        _notes.Clear();
        return count;
    }
}

/// <summary>Static tools, including a <see cref="SecretRef"/> parameter and every supported scalar type.</summary>
public static partial class ApiTools
{
    [Tool("call_api", Description = "Call an HTTP API, authenticating with a secret from Vault.")]
    [Network]
    [SideEffect(SideEffects.Remote | SideEffects.Messaging)]
    public static string CallApi(
        [Description("The Vault secret to authenticate with.")] SecretRef secret,
        [Description("The endpoint.")] Uri url,
        SecretRef? fallback = null) =>
        $"{secret.Name} {url} {fallback?.Name ?? "-"}";

    [Tool("describe_values", Description = "Echo typed values back.")]
    public static string DescribeValues(
        long big,
        double ratio,
        float scale,
        bool flag,
        Guid id,
        DateTimeOffset when,
        DateTime day,
        List<int> numbers,
        IReadOnlyList<Priority> priorities,
        int? maybe,
        string? note,
        decimal amount = 1.5m,
        Priority? level = Priority.High) =>
        string.Join("|", big, ratio, scale, flag, id, when.ToUnixTimeSeconds(), day.Year, string.Join(",", numbers),
            string.Join(",", priorities), maybe?.ToString() ?? "null", note ?? "null", amount, level?.ToString() ?? "null");

    [Tool("ping", Description = "Check the tool host is alive.")]
    public static void Ping()
    {
    }
}
