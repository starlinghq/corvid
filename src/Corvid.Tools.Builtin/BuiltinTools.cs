using System.Collections.Immutable;

namespace Corvid.Tools.Builtin;

/// <summary>What the host needs to wire the built-in tools into the turn loop and the approval pipeline.</summary>
public static class BuiltinTools
{
    /// <summary>
    /// For <c>ApprovalOptions.ShellTools</c>: the <c>shell</c> tool's calls are assessed as the command line in their
    /// <c>command</c> argument. Without it the shell tool never runs, because it requires a shell approval.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ShellToolMapping { get; } =
        ImmutableDictionary.CreateRange(StringComparer.Ordinal, [KeyValuePair.Create(ShellTool.ToolName, ShellTool.CommandParameter)]);

    /// <summary>
    /// Example requests per tool, for the multi-vector shortlist (plan §5.4; <c>TurnOptions.ToolExamples</c>). C-005 has no
    /// generated examples file yet, so they are written here, in the user's words rather than the tool's.
    /// </summary>
    public static IReadOnlyDictionary<string, ImmutableArray<string>> Examples { get; } = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
    {
        ["read_file"] =
        [
            "what's in README.md", "show me the config file", "open src/Program.cs", "read the notes I saved in todo.txt",
            "print the first lines of the log file", "what does the build script say",
        ],
        ["list_files"] =
        [
            "what files are in this folder", "list the project directory", "show me what's in src", "which files are there",
            "what's in the docs folder", "list everything in the workspace",
        ],
        ["search_files"] =
        [
            "find where TODO appears in the code", "which files mention the database password setting", "search the notes for dentist",
            "grep for ConnectionString", "where is the function Parse defined", "find every file that uses HttpClient",
        ],
        ["write_file"] =
        [
            "save this as notes.txt", "create a file called plan.md with this outline", "write the summary to report.md",
            "make a new script hello.sh", "put these lines in todo.txt", "overwrite config.json with these settings",
        ],
        ["patch_file"] =
        [
            "change the port in config.json from 8080 to 9090", "rename the heading in README.md", "fix the typo in notes.txt",
            "replace the old email address in contacts.md", "update the version number in the project file", "edit line that says debug=true to false",
        ],
        ["shell"] =
        [
            "run the tests", "build the project", "how much disk space does this folder use", "run git status",
            "count the lines of code in src", "run the script setup.sh", "compress the logs folder into a zip",
        ],
        ["web_fetch"] =
        [
            "fetch https://example.com and summarize it", "what does this page say", "read the article at this link",
            "get the JSON from this API URL", "check what's on the project's website", "download the release notes page",
        ],
        ["memory_search"] =
        [
            "what do you remember about Sam", "did I tell you my dentist's name", "what did we talk about last week",
            "what's my wife's birthday", "remind me what I said about the trip", "what do you know about my project",
        ],
        ["memory_add"] =
        [
            "remember that my sister is called Anna", "please remember I'm allergic to peanuts", "note that I prefer meetings after 10am",
            "keep in mind my car is a blue Golf", "remember my anniversary is 12 June", "don't forget I moved to Leeds",
        ],
        ["memory_pending"] =
        [
            "what memories are waiting for my review", "show pending memory writes", "is there anything you want to remember",
            "list the facts you staged", "what's in the memory review queue",
        ],
        ["memory_approve"] =
        [
            "yes, keep that memory", "approve the pending memory about Anna", "save the staged fact", "that one is right, remember it",
            "accept all of those memories",
        ],
        ["memory_reject"] =
        [
            "no, forget that one", "reject the pending memory", "don't keep the fact about the car", "discard that staged memory",
            "that's wrong, don't remember it",
        ],
        ["cron_create"] =
        [
            "remind me in 30 minutes to call mum", "every weekday at 9am give me a summary of my notes", "check the inbox every hour",
            "tomorrow at 8am remind me about the dentist", "every monday at 5pm ask me for my weekly review", "schedule a backup check daily at midnight",
        ],
        ["cron_list"] =
        [
            "what reminders do I have", "list my scheduled jobs", "what's scheduled for tomorrow", "show the recurring tasks",
            "when does the backup job run next",
        ],
        ["cron_remove"] =
        [
            "cancel the dentist reminder", "stop the hourly inbox check", "delete that scheduled job", "remove the weekly review reminder",
            "I don't need the morning summary any more",
        ],
    }.ToImmutableDictionary(StringComparer.Ordinal);
}
