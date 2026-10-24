using System.Globalization;

namespace Corvid.App.Core.Settings;

/// <summary>
/// The typed settings the page shows, and the checks on them. The <c>corvid.json</c> ones mirror <c>CorvidOptions</c>
/// (src/Corvid.Host/Configuration) with the same defaults, and <see cref="Validate"/> gives the same messages, word for
/// word, as <c>CorvidOptionsValidator</c> for the rules these settings fall under; the engine's service runs the host's
/// validator itself. The app's own preferences come after them.
/// </summary>
public static class SettingsCatalogue
{
    public const string Configuration = "Configuration";
    public const string Appearance = "Appearance";
    public const string Notifications = "Notifications";
    public const string Layout = "Layout";

    public static IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new("Workspace", "Workspace and data", "Workspace", "The folder the file tools and the shell are confined to.", SettingKind.Path, SettingApplies.Restart, ""),
        new("DataDirectory", "Workspace and data", "Data directory", "Where the event log, memory and checkpoints live.", SettingKind.Path, SettingApplies.Restart, ""),

        new("Approvals:Mode", "Approvals", "Default mode", "The approval mode new sessions start in; Shift+Tab changes a session's own.", SettingKind.Choice, SettingApplies.Now, "Smart")
            { Choices = ["Manual", "Smart", "Permissive"] },
        new("Approvals:TimeoutSeconds", "Approvals", "Deadline (seconds)", "How long an approval waits before it is denied.", SettingKind.Number, SettingApplies.NextTurn, "300"),

        new("Turn:MaxSteps", "Turns", "Steps per turn", "The most tool steps one turn may take.", SettingKind.Number, SettingApplies.NextTurn, "8"),
        new("Turn:MaxOutputTokens", "Turns", "Output tokens per step", "The most tokens the generator writes in one step.", SettingKind.Number, SettingApplies.NextTurn, "1024"),
        new("Turn:ReducedCheckpoints", "Turns", "Reduced checkpoints", "Fewer decider checkpoints on a degraded decider.", SettingKind.Choice, SettingApplies.NextTurn, "Auto")
            { Choices = ["Auto", "On", "Off"] },

        new("Privacy:Cloud", "Privacy", "Cloud requests", "What leaves the machine for a cloud model.", SettingKind.Choice, SettingApplies.Now, "Redact")
            { Choices = ["Send", "Redact", "Refuse"] },
        new("Privacy:Tainted", "Privacy", "Tainted sessions", "What leaves the machine once a session read the network.", SettingKind.Choice, SettingApplies.Now, "Redact")
            { Choices = ["Send", "Redact", "Refuse"] },
        new("Privacy:Threshold", "Privacy", "Redaction threshold", "The extractor's confidence above which a value is redacted (0 to 1).", SettingKind.Number, SettingApplies.Now, "0.4"),

        new("Devices:CpuThreads", "Devices and tools", "CPU threads", "Threads for models on the CPU.", SettingKind.Number, SettingApplies.Restart, "1"),
        new("Tools:ShellNetwork", "Devices and tools", "Shell network", "Whether shell calls may reach the network (inside the sandbox).", SettingKind.Toggle, SettingApplies.Restart, "false"),
        new("Detections:Enforce", "Devices and tools", "Enforce detections", "A matched detection forces an ask; off, it is recorded only.", SettingKind.Toggle, SettingApplies.Restart, "true"),
        new("Checkpoints:MaxFiles", "Devices and tools", "Checkpoint file limit", "The most files a checkpoint snapshots.", SettingKind.Number, SettingApplies.Restart, "50000"),

        new("Serve:Url", "Serve and telemetry", "corvid serve address", "Where corvid serve listens; anything but loopback needs a token.", SettingKind.Url, SettingApplies.Restart, "http://127.0.0.1:8411"),
        new("Serve:TokenSecret", "Serve and telemetry", "Token secret", "The Vault secret remote callers must present (its name, never the token).", SettingKind.Text, SettingApplies.Restart, ""),
        new("Telemetry:OtlpEndpoint", "Serve and telemetry", "OTLP endpoint", "Where traces and metrics go; empty sends none.", SettingKind.Url, SettingApplies.Restart, ""),
        new("Telemetry:ServiceName", "Serve and telemetry", "Service name", "The name traces carry.", SettingKind.Text, SettingApplies.Restart, "corvid"),

        App("Density", Appearance, "Density", "Comfortable or compact rows.", SettingKind.Choice, "Comfortable", "Comfortable", "Compact"),
        App("ReducedMotion", Appearance, "Reduced motion", "No spinners or shimmer; elapsed times update on change only. Follows the platform until set.", SettingKind.Choice, "System", "System", "On", "Off"),
        App("FontScale", Appearance, "Text size", "Scales the window's text.", SettingKind.Choice, "100%", "90%", "100%", "115%", "130%"),

        App("NotifyTurnEnd", Notifications, "A turn ends", "", SettingKind.Toggle, "true"),
        App("NotifyApproval", Notifications, "An approval waits", "", SettingKind.Toggle, "true"),
        App("NotifyQuestion", Notifications, "A question is asked", "", SettingKind.Toggle, "true"),
        App("NotifyJob", Notifications, "A job finishes", "", SettingKind.Toggle, "true"),
        App("NotifyPairing", Notifications, "A pairing code arrives", "", SettingKind.Toggle, "true"),
        App("NotifyOnlyUnfocused", Notifications, "Only while the window is unfocused", "", SettingKind.Toggle, "true"),
        App("Sounds", Notifications, "Sounds", "A sound per event, off by default.", SettingKind.Toggle, "false"),

        App("StartLayout", Layout, "Open in", "Focus: the conversation at reading width. Work: with the dock. Board: every session.", SettingKind.Choice, "Work", "Focus", "Work", "Board"),
        App("RailCollapsed", Layout, "Start with the rail hidden", "Ctrl+Shift+B shows it.", SettingKind.Toggle, "false"),
        App("DockCollapsed", Layout, "Start with the dock hidden", "Ctrl+B shows it.", SettingKind.Toggle, "false"),
    ];

    /// <summary>The page's tabs, in order; the raw editor and keybindings sit between the typed configuration and the app's own.</summary>
    public static IReadOnlyList<string> Sections { get; } = [Configuration, "corvid.json", "Keybindings", Appearance, Notifications, Layout];

    public static SettingDefinition Find(string key) =>
        Definitions.First(definition => string.Equals(definition.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// The validator's messages for these values, one per broken rule, in the validator's order. A value that is not
    /// even the right kind (a word where a number goes) gets a message of its own, since the host's binder would refuse
    /// it before the validator ran.
    /// </summary>
    public static IReadOnlyList<SettingProblem> Validate(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var problems = new List<SettingProblem>();
        string Get(string key) => values.TryGetValue(key, out var value) ? value : Find(key).Default;
        void Add(string key, string message) => problems.Add(new SettingProblem(key, message));

        // Hidden characters first: a value that holds them is never saved, whatever else is wrong with it.
        foreach (var definition in Definitions)
        {
            var value = Get(definition.Key);
            if (SafeText.HasHidden(value))
                Add(definition.Key, $"{definition.FullKey} holds hidden characters: {SafeText.Reveal(value)}. Type it again.");
        }

        bool Whole(string key, out long number)
        {
            if (long.TryParse(Get(key).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number))
                return true;
            Add(key, $"Corvid:{key} must be a whole number, not '{Shown(Get(key))}'.");
            return false;
        }

        bool Choice(string key)
        {
            var definition = Find(key);
            if (definition.Choices.Contains(Get(key).Trim(), StringComparer.OrdinalIgnoreCase))
                return true;
            return false;
        }

        foreach (var (key, message) in new[]
                 {
                     ("Privacy:Cloud", "Corvid:Privacy: Cloud, LocalServer and Tainted are Send, Redact or Refuse."),
                     ("Privacy:Tainted", "Corvid:Privacy: Cloud, LocalServer and Tainted are Send, Redact or Refuse."),
                 })
        {
            if (!Choice(key))
                Add(key, message);
        }

        if (!double.TryParse(Get("Privacy:Threshold").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
            || threshold is < 0 or > 1 || double.IsNaN(threshold))
            Add("Privacy:Threshold", "Corvid:Privacy:Threshold must be between 0 and 1.");

        if (Whole("Devices:CpuThreads", out var threads) && threads is < 1 or > 64)
            Add("Devices:CpuThreads", "Corvid:Devices:CpuThreads must be between 1 and 64.");
        if (!Choice("Turn:ReducedCheckpoints"))
            Add("Turn:ReducedCheckpoints", "Corvid:Turn:ReducedCheckpoints must be Auto, On or Off.");
        if (!Choice("Approvals:Mode"))
            Add("Approvals:Mode", "Corvid:Approvals:Mode must be Manual, Smart or Permissive.");
        if (Whole("Approvals:TimeoutSeconds", out var timeout) && timeout is < 1 or > 86_400)
            Add("Approvals:TimeoutSeconds", "Corvid:Approvals:TimeoutSeconds must be between 1 and 86400.");
        if (Whole("Turn:MaxSteps", out var steps) && steps is < 1 or > 64)
            Add("Turn:MaxSteps", "Corvid:Turn:MaxSteps must be between 1 and 64.");
        if (Whole("Turn:MaxOutputTokens", out var tokens) && tokens is < 16 or > 65_536)
            Add("Turn:MaxOutputTokens", "Corvid:Turn:MaxOutputTokens must be between 16 and 65536.");
        var endpoint = Get("Telemetry:OtlpEndpoint").Trim();
        if (endpoint.Length > 0 && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var otlp) || otlp.Scheme is not ("http" or "https")))
            Add("Telemetry:OtlpEndpoint", $"Corvid:Telemetry:OtlpEndpoint must be an absolute http or https URI, not '{Shown(endpoint)}'.");
        if (string.IsNullOrWhiteSpace(Get("Telemetry:ServiceName")))
            Add("Telemetry:ServiceName", "Corvid:Telemetry:ServiceName cannot be empty.");
        if (Whole("Checkpoints:MaxFiles", out var files) && files < 1)
            Add("Checkpoints:MaxFiles", "Corvid:Checkpoints:MaxFiles must be positive.");
        foreach (var key in new[] { "DataDirectory", "Workspace" })
        {
            var path = Get(key).Trim();
            if (path.Length > 0 && !IsAbsolute(path))
                Add(key, $"Corvid:{key} must be an absolute path (or start with ~/), not '{Shown(path)}'.");
        }

        var url = Get("Serve:Url").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var listen) || listen.Scheme != "http" || listen.AbsolutePath != "/" || listen.UserInfo.Length > 0)
            Add("Serve:Url", $"Corvid:Serve:Url must be an http URL with no path, such as http://127.0.0.1:8411, not '{Shown(url)}' " +
                "(for TLS, put a reverse proxy in front).");
        else if (!listen.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && listen.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6))
            Add("Serve:Url", $"Corvid:Serve:Url must name an IP address or localhost, not '{Shown(listen.Host)}'.");
        else if (!IsLoopback(listen) && string.IsNullOrWhiteSpace(Get("Serve:TokenSecret")))
            Add("Serve:TokenSecret", $"Corvid:Serve:Url {Shown(url)} is not a loopback address: set Corvid:Serve:TokenSecret to a Vault secret, " +
                "so remote callers must present it.");

        foreach (var definition in Definitions.Where(definition => definition.Kind == SettingKind.Toggle))
        {
            if (!bool.TryParse(Get(definition.Key).Trim(), out _))
                Add(definition.Key, $"{definition.FullKey} must be true or false, not '{Shown(Get(definition.Key))}'.");
        }

        foreach (var definition in Definitions.Where(definition => definition.Store == SettingStore.App && definition.Kind == SettingKind.Choice))
        {
            if (!definition.Choices.Contains(Get(definition.Key).Trim(), StringComparer.Ordinal))
                Add(definition.Key, $"{definition.Label} must be one of {string.Join(", ", definition.Choices)}.");
        }

        return problems;
    }

    /// <summary>A value as a message quotes it: hidden characters made visible, so a message never hides what it names.</summary>
    private static string Shown(string value) => SafeText.Reveal(value);

    private static bool IsAbsolute(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || System.IO.Path.IsPathFullyQualified(path);

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && System.Net.IPAddress.IsLoopback(address);

    private static SettingDefinition App(string key, string section, string label, string description, SettingKind kind, string standard, params string[] choices) =>
        new(key, section, label, description, kind, SettingApplies.Now, standard) { Store = SettingStore.App, Choices = choices };
}
