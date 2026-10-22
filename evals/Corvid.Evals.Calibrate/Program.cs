using Corvid.Evals.Calibrate;
using Corvid.Evals.Calibration;
using Corvid.Evals.ToolCalling;

// The verbs become `corvid evals ...` with the CLI (C-013); until then this is the entry point. `tool-calling` runs the S2
// golden set on the local Starling models (C-034); everything else is calibration (C-021).
if (args.Length > 0 && args[0] == "tool-calling")
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancellation.Cancel();
    };
    return await ToolCallingCli.RunAsync(args[1..], Console.Out, Console.Error, () => new StarlingToolCallingModels(), cancellation.Token);
}
return CalibrationCli.Run(args, Console.Out, Console.Error);
