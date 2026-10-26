using Corvid.App.Core.Sessions;

namespace Corvid.App.Core;

/// <summary>
/// The desktop app's command line (docs/plan/ui.md §8). Parsed here rather than in the head, so its rules are tested
/// without a display.
/// </summary>
public sealed record AppOptions
{
    public const string Usage = """
        Usage: Corvid.App [--demo | --engine | --attach <address> [--pair <code>]] [--theme light|dark|system]
                          [--page <area>] [--session <id>] [--scene main|approval]

          --engine            Corvid's engine in-process, over the configuration, models and event log the CLI
                              uses (corvid.json; CORVID_CONFIG and the XDG directories move them): the default
          --demo              demo services and the scripted scenario, with the clock frozen at 2026-10-24 14:05
                              local, instead of the engine
          --attach <address>  a Corvid that runs elsewhere (corvid serve --attach) instead of the engine here:
                              unix:/path/to/attach.sock, or ws://host:port; its sessions, approvals and questions
                              in this window. Over the Unix socket the owner token is read from
                              $XDG_STATE_HOME/corvid/attach/owner.token (or $CORVID_ATTACH_TOKEN); over ws://, pair
          --pair <code>       with --attach: pair this window as a device with a code from corvid serve pair;
                              its token is kept for the next --attach to that address
          --theme <theme>     light, dark or system (the default)
          --page <area>       the area to open: sessions, board, review, rewind, memory, skills, jobs, models,
                              safety, mcp, gateway, secrets, audit, settings or first-run
          --session <id>      the session to open, such as cli-9f2e
          --scene <moment>    the demo scenario's moment to open at: main (turn 12 running, the default) or
                              approval (turn 13 waiting on approval a-32); implies --demo
          --help              this text
        """;

    /// <summary>
    /// Demo services and the scripted scenario, with the clock frozen (<c>--demo</c>). False by default (and with
    /// <c>--engine</c>, which says the default out loud): the engine runs in-process (Corvid.App.Engine) on the system clock.
    /// </summary>
    public bool Demo { get; init; }

    /// <summary>G-1: the address of a Corvid elsewhere to attach to (<c>--attach</c>), instead of the in-process engine.</summary>
    public string? Attach { get; init; }

    /// <summary>G-1: a pairing code to redeem at <see cref="Attach"/> before attaching (<c>--pair</c>).</summary>
    public string? Pair { get; init; }

    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>The area to open on; Sessions when absent.</summary>
    public Area? Page { get; init; }

    /// <summary>The session to select and open, by id.</summary>
    public string? Session { get; init; }

    /// <summary>The demo scenario's moment to open at: Main.png's (the default) or Approval.png's. Giving it implies <see cref="Demo"/>.</summary>
    public DemoScene Scene { get; init; } = DemoScene.Main;

    /// <summary>The caller asked for <see cref="Usage"/>; the app prints it and exits.</summary>
    public bool Help { get; init; }

    /// <summary>Reads the options; on failure <paramref name="error"/> says which argument was wrong and why.</summary>
    public static bool TryParse(IReadOnlyList<string> args, out AppOptions options, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = new AppOptions();
        error = "";
        bool? demo = null;
        var sceneGiven = false;
        for (var i = 0; i < args.Count; i++)
        {
            // "--page models" and "--page=models" are the same.
            var name = args[i];
            string? inline = null;
            var equals = name.IndexOf('=', StringComparison.Ordinal);
            if (name.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                inline = name[(equals + 1)..];
                name = name[..equals];
            }

            switch (name)
            {
                case "--demo" or "--engine" when inline is null:
                    // One or the other: an engine run that quietly showed the demo (or the reverse) would mislead.
                    var wanted = name == "--demo";
                    if (demo is { } earlier && earlier != wanted)
                    {
                        error = "--demo and --engine cannot be used together.";
                        return false;
                    }

                    demo = wanted;
                    options = options with { Demo = wanted };
                    break;
                case "--help" or "-h" or "-?" when inline is null:
                    options = options with { Help = true };
                    break;
                case "--theme":
                    if (!TakeValue(args, ref i, name, inline, out var theme, out error))
                        return false;
                    AppTheme? parsed = theme.ToLowerInvariant() switch
                    {
                        "light" => AppTheme.Light,
                        "dark" => AppTheme.Dark,
                        "system" => AppTheme.System,
                        _ => null,
                    };
                    if (parsed is not { } chosen)
                    {
                        error = $"--theme takes light, dark or system, not \"{theme}\".";
                        return false;
                    }

                    options = options with { Theme = chosen };
                    break;
                case "--page":
                    if (!TakeValue(args, ref i, name, inline, out var page, out error))
                        return false;
                    if (!Areas.TryParse(page, out var area))
                    {
                        error = $"--page takes an area such as sessions, review or models, not \"{page}\".";
                        return false;
                    }

                    options = options with { Page = area };
                    break;
                case "--attach" or "--pair":
                    if (!TakeValue(args, ref i, name, inline, out var given, out error))
                        return false;
                    if (name == "--attach")
                    {
                        if (!Remote.AttachedAddress.TryParse(given, out _, out var wrong))
                        {
                            error = wrong!;
                            return false;
                        }

                        options = options with { Attach = given };
                    }
                    else
                    {
                        options = options with { Pair = given };
                    }

                    break;
                case "--session":
                    if (!TakeValue(args, ref i, name, inline, out var session, out error))
                        return false;
                    options = options with { Session = session };
                    break;
                case "--scene":
                    if (!TakeValue(args, ref i, name, inline, out var scene, out error))
                        return false;
                    // Names only: Enum.TryParse also accepts a number such as "1".
                    if (!Enum.GetNames<DemoScene>().Contains(scene, StringComparer.OrdinalIgnoreCase)
                        || !Enum.TryParse<DemoScene>(scene, ignoreCase: true, out var moment))
                    {
                        error = $"--scene takes main or approval, not \"{scene}\".";
                        return false;
                    }

                    options = options with { Scene = moment };
                    sceneGiven = true;
                    break;
                default:
                    error = $"Unknown argument \"{args[i]}\".";
                    return false;
            }
        }

        // Attached is a third engine: never with the demo's (or this machine's) on top of it.
        if (options.Attach is not null && (demo is not null || sceneGiven))
        {
            error = "--attach runs against another Corvid, so it cannot be used with --demo, --engine or --scene.";
            return false;
        }

        if (options.Pair is not null && options.Attach is null)
        {
            error = "--pair needs --attach: the address the code is for.";
            return false;
        }

        // A scene is the demo's: --scene alone opens the demo (it never runs the engine on this machine's home), and with
        // --engine it is refused.
        if (sceneGiven)
        {
            if (demo == false)
            {
                error = "--scene is a moment of the demo, so it cannot be used with --engine.";
                return false;
            }

            options = options with { Demo = true };
        }

        return true;
    }

    private static bool TakeValue(IReadOnlyList<string> args, ref int i, string name, string? inline, out string value, out string error)
    {
        error = "";
        if (inline is null && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            inline = args[++i];
        value = inline?.Trim() ?? "";
        if (value.Length > 0)
            return true;
        error = $"{name} needs a value.";
        return false;
    }
}
