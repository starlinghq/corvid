using System.Collections.Immutable;

namespace Corvid.Memory.Tests;

internal static class LessonFixtures
{
    public static readonly string Project = ProjectIds.FromRemote("https://github.com/example/widgets.git");
    public static readonly string OtherProject = ProjectIds.FromRemote("https://github.com/example/gadgets.git");
    public const string Hash = "0f0e0d0c0b0a09080706050403020100f0e0d0c0b0a090807060504030201000";

    public static LessonEvidence Evidence(string session = "s-1", string hash = Hash, params long[] positions) =>
        new(session, positions.Length == 0 ? [14, 15, 19] : [.. positions], "build failed until the hooks binary existed", hash);

    public static LessonContent Content(
        string kind = LessonKinds.Workflow,
        string title = "Build the hooks binary before running the cli tests",
        string applicability = "when running go test in cli on a fresh checkout",
        string? body = null,
        ImmutableArray<string>? tags = null,
        ImmutableArray<LessonEvidence>? evidence = null) =>
        new(kind, title, applicability,
            body ?? "Run `make build-hooks` in cli before `go test ./...`.\n"
                 + "The package embeds hooks.bin, so a fresh checkout fails with \"no matching files\" (events 14-19).\n"
                 + "Confirm with `go build ./...`, which exits 0 before the tests run.",
            tags ?? ["build", "testing"], evidence ?? [Evidence()]);

    public static Lesson Candidate(LessonContent content, string project, string session = "s-1", bool localOnly = false) =>
        new(Lesson.IdFor(project, content.Kind, content.Title, content.Body), project, content.Kind, content.Title, content.Applicability,
            content.Body, content.Tags, LessonStates.Candidate, null, LessonAuthors.Agent, null, null, localOnly, default, null,
            content.Evidence, session);
}
