using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Corvid.Skills;

/// <param name="File">Relative to the skill folder.</param>
public sealed record ScanFinding(string File, int Line, string Rule, string Excerpt);

/// <summary>
/// A static scan of a skill's text (plan §10.6), in the spirit of Hermes' skills guard: instructions aimed at the agent
/// rather than the task, piping downloads into a shell, reading credential stores or Corvid's own secrets, reverse
/// shells, and invisible characters that hide text from a reviewer. A finding refuses an import or a draft; it is a
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
        return findings.ToImmutable();
    }

    /// <summary>Scans SKILL.md and every text file of a skill folder (binary files are skipped).</summary>
    public static ImmutableArray<ScanFinding> ScanFolder(string folder)
    {
        var findings = ImmutableArray.CreateBuilder<ScanFinding>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            var info = new FileInfo(path);
            if (info.Length > SkillDocument.MaxBytes * 4)
                continue;
            var bytes = File.ReadAllBytes(path);
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0)
                continue;
            findings.AddRange(Scan(Path.GetRelativePath(folder, path).Replace('\\', '/'), System.Text.Encoding.UTF8.GetString(bytes)));
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
}
