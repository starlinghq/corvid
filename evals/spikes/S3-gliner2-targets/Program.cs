using S3.Gliner2Targets;

// Spike S3 (plan §18): GLiNER2 on shell-argument targets and PII labels. See docs/spikes/S3-gliner2-targets.md.
//   corpus  <repo>   label the command corpus with the structural analyzer (no model)
//   targets <repo>   GLiNER2 recall and precision on the corpus, every label set and threshold (loads the model)
//   pii     <repo>   GLiNER2 on the privacy egress labels over the hand-labelled PII fixture (loads the model)
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: S3.Gliner2Targets corpus|targets|pii <repo root>");
    return 2;
}

var repo = Path.GetFullPath(args[1]);
return args[0] switch
{
    "corpus" => Corpus.Build(repo),
    "targets" => await TargetEval.RunAsync(repo),
    "pii" => await PiiEval.RunAsync(repo),
    _ => 2,
};
