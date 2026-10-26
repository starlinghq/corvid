namespace Corvid.App.Core.Remote;

/// <summary>
/// Attached mode's wire (G-1, docs/plan/ui.md §2, §8): JSON-RPC 2.0, one message per WebSocket text message, carrying the
/// session seam (<see cref="Sessions.ISessionService"/>) between an app and a Corvid that runs elsewhere
/// (<c>corvid serve --attach</c>). Methods are ACP's where ACP has the request (<c>initialize</c>, <c>session/list</c>,
/// <c>session/new</c>, <c>session/load</c>, <c>session/prompt</c>, <c>session/cancel</c>, <c>session/fork</c>,
/// <c>session/close</c>) and <c>_corvid/*</c> for Corvid's own; updates come as <c>session/update</c> (ACP's kinds) or
/// <c>_corvid/update</c> (Corvid's), both with the subscription they belong to.
/// <para>
/// The first message on a connection is <see cref="Initialize"/> with a token, or <see cref="Pair"/> with a pairing code
/// the owner made on the machine (<c>corvid serve pair</c>); anything else, a bad token or a slow start closes it. There is
/// no guest role: a connection is the owner's (the owner token) or a device the owner paired, or it is refused, so an
/// unpaired client never sees a session, an approval or a question.
/// </para>
/// </summary>
public static class AttachedProtocol
{
    /// <summary>The protocol's version; <see cref="Initialize"/> refuses another.</summary>
    public const int Version = 1;

    /// <summary>The path the WebSocket is served on.</summary>
    public const string Path = "/attach";

    /// <summary>How long a connection may take to authenticate before it is closed.</summary>
    public static readonly TimeSpan AuthenticationWindow = TimeSpan.FromSeconds(10);

    /// <summary>The largest message the server reads: requests are small, so anything bigger is refused and closes the connection.</summary>
    public const int MaxRequestBytes = 256 * 1024;

    /// <summary>The largest message the client reads: a long session's history arrives in one <see cref="Load"/> result.</summary>
    public const int MaxResponseBytes = 64 * 1024 * 1024;

    /// <summary>Sessions one connection may have open at once.</summary>
    public const int MaxSubscriptions = 32;

    /// <summary>The longest id (session, approval, question, queued message, subscription) a request may name.</summary>
    public const int MaxIdLength = 256;

    /// <summary>Requests one connection may have running at once; more are refused until some finish.</summary>
    public const int MaxConcurrentRequests = 16;

    /// <summary>Bytes queued for a client that does not read; past this the server drops the connection.</summary>
    public const long MaxQueuedBytes = 128L * 1024 * 1024;

    public const string Initialize = "initialize";
    public const string Pair = "_corvid/pair";
    public const string List = "session/list";
    public const string New = "session/new";
    public const string Load = "session/load";
    public const string Close = "session/close";
    public const string Prompt = "session/prompt";
    public const string Cancel = "session/cancel";
    public const string Fork = "session/fork";
    public const string Queue = "_corvid/queue";
    public const string QueueRemove = "_corvid/queue_remove";
    public const string QueueMove = "_corvid/queue_move";
    public const string QueueTakeBack = "_corvid/queue_take_back";
    public const string Steer = "_corvid/steer";
    public const string AnswerApproval = "_corvid/answer_approval";
    public const string AnswerQuestion = "_corvid/answer_question";
    public const string Command = "_corvid/command";
    public const string SetMode = "_corvid/set_mode";
    public const string SetGoal = "_corvid/set_goal";
    public const string Compact = "_corvid/compact";
    public const string StopChild = "_corvid/stop_child";
    public const string Rename = "_corvid/rename";
    public const string LoadDraft = "_corvid/load_draft";
    public const string SaveDraft = "_corvid/save_draft";
    public const string SetPinned = "_corvid/set_pinned";
    public const string Viewed = "_corvid/viewed";
    public const string Recap = "_corvid/recap";

    /// <summary>The owner only: makes a pairing code for another device, shown on the owner's screen (<c>corvid serve pair</c>).</summary>
    public const string PairingCode = "_corvid/pairing_code";

    /// <summary>Server to client: an update of an open session, of one of ACP's kinds.</summary>
    public const string SessionUpdate = "session/update";

    /// <summary>Server to client: an update of an open session, of one of Corvid's kinds.</summary>
    public const string CorvidUpdate = "_corvid/update";

    /// <summary>Server to client: a session's summary changed or a session appeared.</summary>
    public const string SessionChanged = "_corvid/session_changed";

    /// <summary>Server to client: the whole list may differ (the engine started after the client attached).</summary>
    public const string ListChanged = "_corvid/list_changed";

    /// <summary>The methods a client may call once authenticated; every other name is refused.</summary>
    public static IReadOnlySet<string> SessionMethods { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        List, New, Load, Close, Prompt, Cancel, Fork, Queue, QueueRemove, QueueMove, QueueTakeBack, Steer, AnswerApproval,
        AnswerQuestion, Command, SetMode, SetGoal, Compact, StopChild, Rename, LoadDraft, SaveDraft, SetPinned, Viewed, Recap,
        PairingCode,
    };

    /// <summary>JSON-RPC error codes: the standard ones, and Corvid's for the exceptions the seam documents.</summary>
    public static class Errors
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int Internal = -32603;

        /// <summary>Not authenticated, or the token or code was refused.</summary>
        public const int Unauthorized = -32000;

        /// <summary><see cref="KeyNotFoundException"/>: no session has that id.</summary>
        public const int NotFound = -32001;

        /// <summary><see cref="Composition.ServiceUnavailableException"/>: the engine refuses the request with a reason.</summary>
        public const int Unavailable = -32002;

        /// <summary><see cref="InvalidOperationException"/> or <see cref="ArgumentException"/>: the request was refused.</summary>
        public const int Refused = -32003;

        /// <summary>Too many requests or sessions open on this connection.</summary>
        public const int Busy = -32004;
    }
}

/// <summary>Who a connection is, once authenticated.</summary>
public enum AttachedRole
{
    /// <summary>The owner token: the machine's own user.</summary>
    Owner,

    /// <summary>A device the owner paired with a code made on the machine.</summary>
    Device,
}
