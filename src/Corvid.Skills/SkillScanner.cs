using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Corvid.Secrets;

namespace Corvid.Skills;

/// <param name="File">Relative to the skill folder.</param>
public sealed record ScanFinding(string File, int Line, string Rule, string Excerpt);

/// <summary>
/// A static scan of a skill's text (plan §10.6), in the spirit of Hermes' skills guard: instructions aimed at the agent
/// rather than the task, piping downloads into a shell, reading credential stores or Corvid's own secrets, reverse
/// shells, invisible characters that hide text from a reviewer, and credential values themselves (C-040). A finding refuses an import or a draft; it is a
/// screen, not a proof of safety. Taint (§10.3) applies to hub skills whatever the scan says, and scripts only ever run
/// in the sandbox.
/// </summary>
public static partial class SkillScanner
{
    private static readonly (string Rule, Regex Pattern)[] Rules =
    [
        ("instruction_override", OverridePattern()),
        ("hide_from_user", HidePattern()),
        ("download_to_shell", DownloadPattern()),
        ("credential_read", CredentialPattern()),
        ("environment_dump", EnvironmentPattern()),
        ("reverse_shell", ReverseShellPattern()),
        ("destroy_home", DestroyPattern()),
        ("encoded_execution", EncodedPattern()),
    ];

    public static ImmutableArray<ScanFinding> Scan(string file, string text)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(text);
        var findings = ImmutableArray.CreateBuilder<ScanFinding>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            foreach (var c in line)
            {
                // Bidirectional overrides and zero-width characters make what a reviewer reads differ from what a model reads.
                if (c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '​' or '‌' or '‍' or '⁠' or '﻿')
                {
                    findings.Add(new ScanFinding(file, i + 1, "invisible_character", $"U+{(int)c:X4}"));
                    break;
                }
            }
            foreach (var (rule, pattern) in Rules)
                if (pattern.Match(line) is { Success: true } match)
                    findings.Add(new ScanFinding(file, i + 1, rule, Excerpt(line, match.Index)));
        }
        CredentialValues(file, text, findings);
        return findings.ToImmutable();
    }

    /// <summary>The rule name for a credential value in a skill's text (C-040).</summary>
    public const string CredentialValueRule = "credential_value";

    // A skill never carries a credential value (C-040): a learned draft would store one in the skill store, a hub skill would
    // hand one to the agent. One pass over the whole text, so a PEM block across lines is seen; each finding's excerpt is the
    // scrub's marker, never the line. Vault placeholders and markers are not credentials. A timeout is a finding.
    private static void CredentialValues(string file, string text, ImmutableArray<ScanFinding>.Builder findings)
    {
        ImmutableArray<CredentialMatch> matches;
        try
        {
            matches = CredentialDetector.Find(text, CredentialDetector.Egress);
        }
        catch (RegexMatchTimeoutException)
        {
            findings.Add(new ScanFinding(file, 0, CredentialValueRule, CredentialScrub.Unscannable));
            return;
        }
        foreach (var match in matches)
        {
            // OPENAI_API_KEY=your_api_key_here in a setup section is documentation, not a key. Only an assigned or prose value
            // is judged so; a provider shape (sk-..., ghp_...) is refused whatever it says.
            if (match.Strength == CredentialStrength.Labelled && Placeholder().IsMatch(match.ValueIn(text)))
                continue;
            var line = 1 + text.AsSpan(0, match.Start).Count('\n');
            findings.Add(new ScanFinding(file, line, CredentialValueRule, CredentialScrub.Marker(text, match)));
        }
    }

    /// <summary>Largest file the scanner reads; a larger one is a finding, since what is not read cannot be screened.</summary>
    public const long MaxScanBytes = 4L * 1024 * 1024;

    // Files a skill's instructions or scripts are made of. Binary content in one of them would pass unread, so it is a finding;
    // other binary files (images, say) are skipped.
    private static readonly ImmutableHashSet<string> TextExtensions = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        "", ".md", ".txt", ".sh", ".bash", ".zsh", ".py", ".js", ".ts", ".rb", ".pl", ".ps1", ".json", ".yaml", ".yml", ".toml");

    /// <summary>
    /// Scans SKILL.md and every text file of a skill folder. A file too large to read, or binary content in a file named as
    /// text or a script, is itself a finding (fail closed); other binary files are skipped.
    /// </summary>
    public static ImmutableArray<ScanFinding> ScanFolder(string folder)
    {
        var findings = ImmutableArray.CreateBuilder<ScanFinding>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
            var info = new FileInfo(path);
            if (info.Length > MaxScanBytes)
            {
                findings.Add(new ScanFinding(relative, 0, "too_large_to_scan", $"{info.Length} bytes"));
                continue;
            }
            var bytes = File.ReadAllBytes(path);
            if (Array.IndexOf(bytes, (byte)0) >= 0)
            {
                if (TextExtensions.Contains(Path.GetExtension(path)) || relative.StartsWith("scripts/", StringComparison.Ordinal))
                    findings.Add(new ScanFinding(relative, 0, "binary_content", "a NUL byte in a text or script file"));
                continue;
            }
            findings.AddRange(Scan(relative, System.Text.Encoding.UTF8.GetString(bytes)));
        }
        return findings.ToImmutable();
    }

    private static string Excerpt(string line, int index)
    {
        var start = Math.Max(0, index - 20);
        var text = line[start..Math.Min(line.Length, index + 60)].Trim();
        return text.Length > 0 ? text : line.Trim();
    }

    [GeneratedRegex(@"\b(ignore|disregard|forget|override)\b.{0,30}\b(previous|prior|above|earlier|all|system|your)\b.{0,20}\b(instructions?|prompts?|rules|guidelines)\b", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex OverridePattern();

    [GeneratedRegex(@"\b(do not|don't|never)\b.{0,20}\b(tell|inform|show|mention|reveal)\b.{0,20}\b(the )?user\b|\bwithout (asking|telling|informing) the user\b", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex HidePattern();

    [GeneratedRegex(@"\b(curl|wget|fetch)\b[^|\n]{0,200}\|\s*(sudo\s+)?(ba|z|da|k)?sh\b|\b(curl|wget)\b[^\n]{0,200}\|\s*python[0-9.]*\b", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex DownloadPattern();

    [GeneratedRegex(@"~/\.ssh\b|\bid_(rsa|ed25519|ecdsa)\b|\.aws/credentials|\.netrc\b|\.config/gh\b|\.gnupg\b|\.kube/config|corvid-vault|/etc/shadow|\.docker/config\.json", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex CredentialPattern();

    [GeneratedRegex(@"\bprintenv\b|\benv\s*\|\s*(curl|nc|wget)|\bos\.environ\b.{0,40}\b(post|send|requests)\b|\$\{?[A-Z_]*(TOKEN|SECRET|PASSWORD|API_KEY)\b", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex EnvironmentPattern();

    [GeneratedRegex(@"/dev/tcp/|\bnc\b[^\n]{0,40}\s-e\s|\bsocat\b[^\n]{0,80}\bexec:|\bmkfifo\b[^\n]{0,80}\bnc\b", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex ReverseShellPattern();

    [GeneratedRegex(@"\brm\s+-[a-z]*r[a-z]*f?[a-z]*\s+(/|~|\$HOME)(\s|$|/\*)", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex DestroyPattern();

    [GeneratedRegex(@"\bbase64\s+(-d|--decode)\b[^\n]{0,80}\|\s*(ba)?sh\b|\beval\s*\(\s*(atob|base64)", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex EncodedPattern();

    // A documentation placeholder, whole: letters and separators only, saying so (your_api_key_here, YOUR_TOKEN, replace-me,
    // changeme, <placeholder>-style words, example-key, dummy). A digit makes it a value.
    [GeneratedRegex(@"\A(?:your[A-Za-z_.-]*|[A-Za-z_.-]*[_.-]here|(?:replace|change|fill|set)[_.-]?(?:me|this|it)|(?:insert|put|add|enter|paste)[_.-][A-Za-z_.-]*|[A-Za-z_.-]*(?:placeholder|example|dummy|redacted)[A-Za-z_.-]*|todo|tbd|fixme|\.\.\.|…)\z", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex Placeholder();
}
