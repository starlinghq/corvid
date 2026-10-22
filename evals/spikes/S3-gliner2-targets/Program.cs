using S3.Gliner2Targets;

// Spike S3 (plan §18): GLiNER2 on shell-argument targets and PII labels. See docs/spikes/S3-gliner2-targets.md.
//   corpus  <repo>   label the command corpus with the structural analyzer (no model)
//   targets <repo>   GLiNER2 recall and precision on the corpus, every label set and threshold (loads the model)
//   rescore <repo>   token-containment recall over the committed target predictions (no model)
//   pii     <repo>   GLiNER2 on the privacy egress labels over the hand-labelled PII fixture (loads the model)
//   credentials <repo>  the credential detector (C-032) beside GLiNER2 in the egress gate (loads the model once)
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: S3.Gliner2Targets corpus|targets|rescore|pii|credentials <repo root>");
    return 2;
}

var repo = Path.GetFullPath(args[1]);
return args[0] switch
{
    "corpus" => Corpus.Build(repo),
    "targets" => await TargetEval.RunAsync(repo),
    "rescore" => Rescore.Run(repo),
    "pii" => await PiiEval.RunAsync(repo),
    "credentials" => await CredentialEval.RunAsync(repo),
    _ => 2,
};
