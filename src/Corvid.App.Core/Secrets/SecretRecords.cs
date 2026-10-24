using System.Collections.Immutable;

namespace Corvid.App.Core.Secrets;

/// <summary>Whether Vault answers, and whether it can serve secrets.</summary>
public enum VaultState
{
    /// <summary>Running and unsealed: secrets are served.</summary>
    Running,
    /// <summary>Running but sealed: nothing is served until it is unsealed.</summary>
    Sealed,
    /// <summary>No answer on the socket: cloud slots and every feature needing a secret are unavailable (F23).</summary>
    Absent,
}

/// <summary>
/// What Vault says about itself, through the read-only summary vault.md §10 allows: its state, version and socket, and
/// today's uses as counts.
/// </summary>
public sealed record VaultStatus(VaultState State)
{
    public string Version { get; init; } = "";

    /// <summary>The socket Corvid talks to it on.</summary>
    public string Socket { get; init; } = "";

    public int UsesToday { get; init; }

    /// <summary>Uses that asked through Corvid's approval prompt.</summary>
    public int AskedToday { get; init; }

    /// <summary>Uses Vault confirmed out of band (V-006).</summary>
    public int ConfirmedToday { get; init; }

    /// <summary>Uses Vault refused: an unbound host, a method its policy forbids, a spent budget.</summary>
    public int RefusedToday { get; init; }

    /// <summary>An out-of-band confirm Vault is showing now, in Vault's own words; the app only says one is pending.</summary>
    public string? PendingConfirm { get; init; }

    /// <summary>
    /// What Vault's audit summary says when it cannot give the day's counts above (V-n): its counts since its audit log
    /// began, in a sentence. When set, the page shows it in place of the "today" counts, which are then not known.
    /// </summary>
    public string? AuditSummary { get; init; }
}

/// <summary>How a method may use a secret: silently, with Corvid's approval prompt, or with Vault's own confirm (vault.md §6).</summary>
public enum MethodPolicy
{
    Auto,
    Ask,
    Confirm,
    Deny,
}

/// <summary>One method's policy ("POST ask").</summary>
public sealed record MethodRule(string Method, MethodPolicy Policy);

/// <summary>
/// A secret as Vault describes it: its name and policy, never its value, length or prefix (vault.md §4). There is no
/// member here that could hold a value, and none must be added: the app never holds or shows one (docs/plan/ui.md §4.11).
/// Every string is Vault's configuration, so it is data: cleaned for display, revealed where a person binds the name.
/// </summary>
public sealed record SecretInfo(string Name)
{
    public string Description { get; init; } = "";

    /// <summary>The hosts it may be sent to; a request to any other is refused.</summary>
    public ImmutableArray<string> Hosts { get; init; } = [];

    public ImmutableArray<MethodRule> Methods { get; init; } = [];

    /// <summary>The toolsets that may use it ("git", "github", "model-provider").</summary>
    public ImmutableArray<string> Toolsets { get; init; } = [];

    /// <summary>A role that limits who may use it ("model-provider": only Corvid's model slots).</summary>
    public string? Role { get; init; }

    /// <summary>Uses in the budget's window so far, and its ceiling; no ceiling when null.</summary>
    public int BudgetUsed { get; init; }

    public int? BudgetLimit { get; init; }

    /// <summary>"hour", "day".</summary>
    public string BudgetWindow { get; init; } = "hour";

    /// <summary>What a tainted turn's use does ("ask" by default: Vault refuses to be quieter for a tainted turn).</summary>
    public MethodPolicy Tainted { get; init; } = MethodPolicy.Ask;

    public DateTimeOffset? LastUsed { get; init; }

    /// <summary>
    /// Whether Vault reported this secret's method policy, role, budget, tainted-turn policy and last use. Vault's catalogue
    /// gives names, descriptions, hosts and toolsets only (V-n), so the engine sets false and the page says "not reported"
    /// for those rather than show defaults as though Vault had said them.
    /// </summary>
    public bool PolicyReported { get; init; } = true;
}

/// <summary>What waits on a secret: a model slot's cloud model, a gateway adapter, an MCP server, a tool.</summary>
public enum ConsumerKind
{
    Model,
    Gateway,
    Mcp,
    Tool,
}

/// <summary>
/// A feature configured to use a secret by name (corvid.json), and the host it calls with it. It waits when the name is not
/// in Vault, or when the secret is not bound to its host.
/// </summary>
/// <param name="Id">Stable; a binding names it.</param>
/// <param name="Name">The feature's name ("gemini", "slack").</param>
/// <param name="Detail">What it is for ("generator, summarizer", "adapter").</param>
/// <param name="Secret">The secret name it is configured with.</param>
/// <param name="Host">The host it sends the secret to.</param>
public sealed record SecretConsumer(string Id, ConsumerKind Kind, string Name, string Detail, string Secret, string Host);

/// <summary>Everything the Secrets page shows, read at once.</summary>
public sealed record SecretsSnapshot(VaultStatus Vault, ImmutableArray<SecretInfo> Secrets, ImmutableArray<SecretConsumer> Consumers);

/// <summary>
/// What asking to set a secret did. Setting is the one flow that leaves the app (U-6): the Vault helper, or until it
/// exists a terminal running <see cref="Command"/>. The value goes from the person to Vault; the app learns only that the
/// secret now exists.
/// </summary>
/// <param name="Launched">A terminal or the helper was opened; false when nothing could be (or, in demo mode, was) started.</param>
/// <param name="Command">The exact command to run: <c>corvid-vault set NAME</c>.</param>
/// <param name="Note">What happened, in a sentence.</param>
public sealed record SetterLaunch(bool Launched, string Command, string Note);
