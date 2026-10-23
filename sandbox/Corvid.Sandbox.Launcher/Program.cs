using Corvid.Sandbox.Launcher;

try
{
    if (args.Length > 0 && args[0] == Arguments.Stage2Flag)
    {
        return Stage2.Run(Arguments.ParseStage2(args));
    }

    if (args.Length > 0 && args[0] == Arguments.RelayFlag)
    {
        return Relay.Run(Arguments.ParseRelay(args));
    }

    if (args.Length == 0 || args[0] is "-h" or "--help")
    {
        Console.Error.WriteLine(Arguments.Usage);
        return args.Length == 0 ? Supervisor.NotStarted : 0;
    }

    if (args[0] == "--version")
    {
        Console.WriteLine("corvid-sandbox " + (typeof(Arguments).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
        return 0;
    }

    return Supervisor.Run(Arguments.ParseLaunch(args));
}
catch (RefusedException e)
{
    Console.Error.WriteLine("corvid-sandbox: " + e.Message);
    return Supervisor.NotStarted;
}
