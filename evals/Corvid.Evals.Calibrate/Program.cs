using Corvid.Evals.Calibration;

// The verb becomes `corvid evals calibrate` with the CLI (C-013); until then this is the entry point.
return CalibrationCli.Run(args, Console.Out, Console.Error);
