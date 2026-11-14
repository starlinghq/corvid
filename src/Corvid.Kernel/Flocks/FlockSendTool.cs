using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Turns;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Flocks;

/// <summary>
/// <c>flock_send</c> (C-042): a session in a flock writes to the others: one member, a few, or all of them. The message goes
/// into the flock's log and returns at once; the process driving each recipient hands it over (a steer into a running turn,
/// else a turn of its own), so no call ever waits on another session. Offered to a member's every turn whatever the
/// shortlist ranks (<see cref="Turns.TurnOptions.AlwaysOffered"/>), and to nobody else: a session outside a flock, a child
/// session and a guest's turn never have it. It changes nothing but Corvid's own log, so it asks no approval; what keeps a
/// tainted turn's message from passing as clean is the taint it carries (plan §10.3): it reaches the others as outside content.
/// </summary>
/// <param name="store">The flocks, read at each call (the host's, made with its runtime).</param>
public sealed class FlockSendTool(Func<FlockStore> store) : ToolFunction(new ToolDescriptor(ToolName,
    "Send a message to the other sessions in your flock (sessions of the same user working on one task with you). Name " +
    "who it is for in 'to' (a member's name, or several), or leave 'to' out for all of them. It returns at once: their " +
    "answers come later as messages to you. Call it with no 'text' to see who is in the flock.",
    Schema, new ToolRisk(SideEffects.None, Network: false, Destructive: false),
    [
        new ToolParameter("text", "The message: say what you need or what you did, self-contained.", false, false),
        new ToolParameter("to", "Who it is for: a member's name, or a list of names. Leave out for every other member.", false, false),
    ]))
{
    public const string ToolName = "flock_send";

    /// <summary>Requests it is shortlisted for, though a member's turns are offered it anyway.</summary>
    public static ImmutableArray<string> Examples { get; } =
    [
        "tell the other session", "let the api session know", "ask the frontend session", "send this to the flock",
        "coordinate with the others", "reply to the other agent",
    ];

    private const string Schema = """
        {"type":"object","properties":{"text":{"type":"string","description":"The message."},"to":{"anyOf":[{"type":"string"},{"type":"array","items":{"type":"string"}}],"description":"A member's name, or several; leave out for everyone."}}}
        """;

    private readonly Func<FlockStore> _store = store ?? throw new ArgumentNullException(nameof(store));

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var context = ToolCallContext.Require(arguments, ToolName);
        var element = JsonValues.Object(arguments);
        var flocks = _store();
        if (flocks.FlockOf(context.SessionId) is not { } flock)
            return ValueTask.FromResult<object?>("This session is not in a flock, so there is nobody to send to.");
        var self = flock.Member(context.SessionId)!;
        var text = element.TryGetProperty("text", out var given) && given.ValueKind == JsonValueKind.String ? given.GetString()!.Trim() : "";
        if (text.Length == 0)
            return ValueTask.FromResult<object?>(Roster(flock, self));
        var names = element.TryGetProperty("to", out var to) ? to.ValueKind switch
        {
            JsonValueKind.String => [to.GetString()!],
            JsonValueKind.Array => [.. to.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)],
            JsonValueKind.Null => [],
            _ => throw new ToolArgumentException("'to' must be a member's name or a list of names", "to"),
        } : ImmutableArray<string>.Empty;
        var recipients = new List<FlockMember>();
        foreach (var name in names.Select(name => name.Trim().TrimStart('@')).Where(name => name.Length > 0))
        {
            if (flock.Find(name) is not { } member || member.SessionId == self.SessionId)
                throw new ToolArgumentException($"{name} is not another member of the flock. {Roster(flock, self)}", "to");
            recipients.Add(member);
        }
        try
        {
            flocks.Say(flock.Id, context.SessionId, text, [.. recipients.Select(member => member.SessionId)], context.Tainted);
        }
        catch (FlockException refused)
        {
            return ValueTask.FromResult<object?>("Not sent: " + refused.Message);
        }
        var sent = recipients.Count > 0 ? recipients : [.. flock.Members.Where(member => member.SessionId != self.SessionId)];
        return ValueTask.FromResult<object?>($"Sent to {string.Join(", ", sent.Select(member => member.Name))}. Their answers come as messages to you; "
            + "do not wait for them in this turn.");
    }

    private static string Roster(FlockState flock, FlockMember self)
    {
        var others = flock.Members.Where(member => member.SessionId != self.SessionId).Select(member => member.Name).ToList();
        return others.Count == 0
            ? $"You are {self.Name} in the flock {flock.Name}, and nobody else is in it yet."
            : $"You are {self.Name} in the flock {flock.Name}. The others: {string.Join(", ", others)}.";
    }
}
