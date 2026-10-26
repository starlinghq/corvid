using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Corvid.Vault.Contracts;

namespace Corvid.Secrets;

/// <summary>Which rule families a scan runs. <see cref="CredentialDetector"/> names the three combinations Corvid uses.</summary>
[Flags]
public enum CredentialRules
{
    None = 0,

    /// <summary>
    /// Token shapes that are credentials wherever they appear: provider key prefixes (<c>ghp_</c>, <c>xoxb-</c>, <c>sk-</c>,
    /// <c>AKIA</c>, ...), JWTs, PEM private key blocks with key material, and Authorization header values.
    /// </summary>
    Shapes = 1,

    /// <summary>
    /// Values named by a label: assignments (<c>password=</c>, <c>"api_key": "..."</c>, <c>export TOKEN='...'</c>), URL userinfo
    /// passwords, and prose (<c>the password is ...</c>), the last only when the value looks like a secret.
    /// </summary>
    Labelled = 2,

    /// <summary>Unlabelled encoded blobs: long hex and long base64 runs. Strict: a git SHA or a digest matches too.</summary>
    Encoded = 4,

    /// <summary>A bare PEM private key header with no key material after it. Strict: text that only names the header matches.</summary>
    KeyHeader = 8,
}

/// <summary>How a match was recognized: by its own shape, by a label next to it, or as an unlabelled encoded blob.</summary>
public enum CredentialStrength
{
    Shape,
    Labelled,
    Encoded,
}

public enum CredentialKind
{
    PrivateKey,
    PrivateKeyHeader,
    Authorization,
    AwsAccessKey,
    GitHubToken,
    SlackToken,
    SkKey,
    StripeKey,
    GitLabToken,
    GoogleApiKey,
    HuggingFaceToken,
    Jwt,
    LabelledValue,
    UrlPassword,
    ProseValue,
    EncodedHex,
    EncodedBase64,

    /// <summary>The password of a <c>user:password</c> pair on a command line (<c>curl -u</c>, <c>--user</c>).</summary>
    UserPassword,
}

/// <summary>One credential value in a text.</summary>
/// <param name="Start">Where the value starts. For a labelled value it is the value alone, never the label or its quotes.</param>
/// <param name="PrefixLength">
/// The leading characters that only name the credential's type (<c>ghp_</c>, <c>AKIA</c>, <c>sk-</c>), which a redaction may
/// keep, as secret scanners report a finding. Zero when there is none.
/// </param>
public readonly record struct CredentialMatch(CredentialKind Kind, int Start, int Length, int PrefixLength, CredentialStrength Strength)
{
    public int End => Start + Length;

    /// <summary>The matched value in the text it was found in.</summary>
    public string ValueIn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Substring(Start, Length);
    }
}

/// <summary>
/// The credential-value detector (C-032), shared by the privacy egress gate (C-015), trace export (C-027) and the lesson
/// validator (C-025). Deterministic: token shapes, labelled values and, for lessons, encoded blobs. It finds the value, not
/// the noun naming it, which is the gap spike S3 found in GLiNER2's credential label.
/// </summary>
/// <remarks>
/// <para>
/// Every pattern runs on <see cref="RegexOptions.NonBacktracking"/>, linear in the text, with a one second timeout; none uses
/// a lookaround. Checks a pattern cannot make without one (entropy, exclusions, the secret-like test for prose) run in code
/// over each candidate. <see cref="Find"/> throws <see cref="RegexMatchTimeoutException"/>; <see cref="Redact"/> and
/// <see cref="Contains"/> fail closed. Thread-safe.
/// </para>
/// <para>
/// Never a credential, under every rule, when it is the whole value: a Vault placeholder (<see cref="Placeholders"/>;
/// session-bound and meaningless off the machine), an egress placeholder (<c>&lt;CREDENTIAL_1&gt;</c>) or another name in angle
/// brackets (<c>&lt;YOUR_API_KEY&gt;</c>), <c>[REDACTED]</c> alone or after a kept prefix (<c>ghp_[REDACTED]</c>, so
/// <see cref="Redact"/> is idempotent), a scrub marker (<c>[REDACTED:labelled-value]</c>,
/// <c>ghp_[REDACTED:github-token:3fa91c0e]</c>, so <see cref="CredentialScrub"/> is idempotent too), a variable reference (<c>$VAR</c>, <c>${VAR}</c>, <c>$(cmd)</c>, <c>%VAR%</c>,
/// <c>{{ ... }}</c>), a mask (<c>***</c>, <c>xxxx</c>), the JSON literals, a bare Authorization scheme, and a number of at most
/// six digits. A value that only contains one of these is not exempt.
/// </para>
/// <para>
/// A bare (unquoted) value ends at whitespace or <c>, ; &amp; ) ] }</c>, since those separate fields in connection strings,
/// query strings and JSON; a password holding one is cut there, and only its first part is found. A quoted value is found
/// whole.
/// </para>
/// <para>
/// A screen, not a proof: a credential in no known shape under no telling label passes. Vault exact-value scrubbing is a
/// separate, later step (vault.md §5); Corvid holds no secret values to compare against.
/// </para>
/// </remarks>
public static partial class CredentialDetector
{
    public const string Redacted = "[REDACTED]";

    /// <summary>The privacy egress gate: shapes and labelled values. No encoded blobs, or every git SHA in a prompt would go.</summary>
    public const CredentialRules Egress = CredentialRules.Shapes | CredentialRules.Labelled;

    /// <summary>Trace export: the same rules as egress; an assignment's value is redacted whatever it looks like, to where a bare value ends.</summary>
    public const CredentialRules Traces = CredentialRules.Shapes | CredentialRules.Labelled;

    /// <summary>Lessons: everything, including encoded blobs and a bare key header, since a lesson should hold neither.</summary>
    public const CredentialRules Lessons = CredentialRules.Shapes | CredentialRules.Labelled | CredentialRules.Encoded | CredentialRules.KeyHeader;

    private const int TimeoutMilliseconds = 1000;

    /// <summary>Random base64 changes character class at about two characters in three; words in an identifier about twice a word.</summary>
    internal const double MinSwitchRate = 0.45;

    // Single words that follow "the password is" in prose about passwords, never as the password.
    private static readonly FrozenWords Stopwords = new(
        "a", "an", "the", "is", "are", "was", "be", "been", "being", "to", "of", "in", "on", "at", "for", "with", "from", "by", "and",
        "or", "not", "no", "yes", "it", "this", "that", "these", "those", "here", "there", "below", "above", "same", "your", "my",
        "our", "their", "his", "her", "its", "stored", "saved", "kept", "set", "unset", "required", "optional", "missing", "empty",
        "changed", "correct", "incorrect", "wrong", "invalid", "valid", "expired", "secret", "hidden", "private", "public",
        "needed", "please", "provided", "redacted", "removed", "rotated", "revoked", "unknown", "none", "string", "value", "default");

    // The word after a prose label that makes the label a modifier ("the password manager is ..."), not the thing named.
    private static readonly FrozenWords LabelModifiers = new(
        "manager", "managers", "policy", "policies", "reset", "file", "files", "hash", "hashes", "field", "fields", "length",
        "prompt", "requirement", "requirements", "rule", "rules", "rotation", "change", "expiry", "expiration", "store", "vault",
        "form", "box", "input", "strength", "complexity", "history", "hint", "hints", "check", "checks", "generator", "format",
        "scheme", "page", "dialog", "flow", "screen", "setting", "settings", "option", "options", "name", "names", "id", "type",
        "prefix", "scope", "scopes", "count", "limit", "budget", "usage", "header", "endpoint", "url", "path", "env", "variable");

    /// <summary>Every credential value in <paramref name="text"/> under <paramref name="rules"/>, in order, never overlapping.</summary>
    /// <exception cref="RegexMatchTimeoutException">A pattern ran past its timeout; treat the text as holding a credential.</exception>
    public static ImmutableArray<CredentialMatch> Find(string text, CredentialRules rules)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || rules == CredentialRules.None)
            return [];
        var found = new List<CredentialMatch>();
        if (rules.HasFlag(CredentialRules.Shapes))
            FindShapes(text, found);
        if (rules.HasFlag(CredentialRules.Labelled))
            FindLabelled(text, found);
        if (rules.HasFlag(CredentialRules.Encoded))
            FindEncoded(text, found);
        if (rules.HasFlag(CredentialRules.KeyHeader))
            foreach (var match in PrivateKeyHeader().EnumerateMatches(text))
                found.Add(new(CredentialKind.PrivateKeyHeader, match.Index, match.Length, 0, CredentialStrength.Shape));
        DropInsideMarkers(text, found);
        return Merge(found);
    }

    // A redaction's or scrub's own output is never a credential, and neither is a part of it: "github-token:3fa91c0e" inside
    // ghp_[REDACTED:github-token:3fa91c0e] reads as a labelled assignment, and "[REDACTED:prose-value]." after "the password
    // is" as a prose value. Candidates that lie wholly inside one are dropped, so Redact and CredentialScrub are idempotent. A
    // candidate that runs on past a marker's end ("the password is [REDACTED]CorrectHorseBattery", admin:[REDACTED]S3cretPw@)
    // holds text the marker does not cover and is kept whole, as before markers were recognised (C-040 review).
    private static void DropInsideMarkers(string text, List<CredentialMatch> found)
    {
        if (found.Count == 0 || !text.Contains("[REDACTED", StringComparison.Ordinal))
            return;
        var markers = new List<(int Start, int End)>();
        foreach (var match in Marker().EnumerateMatches(text))
            markers.Add((match.Index, match.Index + match.Length));
        if (markers.Count > 0)
            found.RemoveAll(candidate => markers.Exists(marker => candidate.Start >= marker.Start && candidate.End <= marker.End));
    }

    /// <summary>Whether <paramref name="text"/> holds a credential. A timeout counts as yes.</summary>
    public static bool Contains(string text, CredentialRules rules) => Contains(text, rules, Find);

    internal static bool Contains(string text, CredentialRules rules, Func<string, CredentialRules, ImmutableArray<CredentialMatch>> find)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            return !find(text, rules).IsEmpty;
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    /// <summary>
    /// <paramref name="text"/> with every credential replaced by <see cref="Redacted"/>, keeping a match's public prefix
    /// (<c>ghp_[REDACTED]</c>) and a labelled value's label and quotes. On a timeout the whole text is <see cref="Redacted"/>.
    /// </summary>
    public static string Redact(string text, CredentialRules rules) => Redact(text, rules, Find);

    internal static string Redact(string text, CredentialRules rules, Func<string, CredentialRules, ImmutableArray<CredentialMatch>> find)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return text;
        ImmutableArray<CredentialMatch> matches;
        try
        {
            matches = find(text, rules);
        }
        catch (RegexMatchTimeoutException)
        {
            return Redacted;
        }
        if (matches.IsEmpty)
            return text;
        var builder = new StringBuilder(text.Length);
        var position = 0;
        foreach (var match in matches)
        {
            builder.Append(text, position, match.Start - position).Append(text, match.Start, match.PrefixLength).Append(Redacted);
            position = match.End;
        }
        return builder.Append(text, position, text.Length - position).ToString();
    }

    private static void FindShapes(string text, List<CredentialMatch> found)
    {
        foreach (var match in PrivateKeyBlock().EnumerateMatches(text))
            found.Add(new(CredentialKind.PrivateKey, match.Index, match.Length, 0, CredentialStrength.Shape));
        foreach (Match match in AuthorizationValue().Matches(text))
            AddAuthorization(text, match.Groups["value"], found);
        foreach (Match match in BearerValue().Matches(text))
            AddAuthorization(text, match.Groups["value"], found);
        Prefixed(text, AwsAccessKey(), CredentialKind.AwsAccessKey, 4, found);
        Prefixed(text, GitHubToken(), CredentialKind.GitHubToken, 4, found);
        Prefixed(text, GitHubFineGrained(), CredentialKind.GitHubToken, "github_pat_".Length, found);
        foreach (var match in SlackToken().EnumerateMatches(text))
            if (!IsSlug(text.AsSpan(match.Index + 5, match.Length - 5)))
                found.Add(new(CredentialKind.SlackToken, match.Index, match.Length, 5, CredentialStrength.Shape));
        foreach (var match in SkKey().EnumerateMatches(text))
            if (!IsSlug(text.AsSpan(match.Index + 3, match.Length - 3)))
                found.Add(new(CredentialKind.SkKey, match.Index, match.Length, 3, CredentialStrength.Shape));
        Prefixed(text, StripeKey(), CredentialKind.StripeKey, "sk_live_".Length, found);
        Prefixed(text, GitLabToken(), CredentialKind.GitLabToken, "glpat-".Length, found);
        Prefixed(text, GoogleApiKey(), CredentialKind.GoogleApiKey, 4, found);
        Prefixed(text, HuggingFaceToken(), CredentialKind.HuggingFaceToken, 3, found);
        Prefixed(text, Jwt(), CredentialKind.Jwt, 3, found);
    }

    private static void Prefixed(string text, Regex pattern, CredentialKind kind, int prefix, List<CredentialMatch> found)
    {
        foreach (var match in pattern.EnumerateMatches(text))
            found.Add(new(kind, match.Index, match.Length, prefix, CredentialStrength.Shape));
    }

    // "Bearer authentication" names the scheme; a credential has a digit or a symbol, or is long and mixed case.
    private static void AddAuthorization(string text, Group value, List<CredentialMatch> found)
    {
        var span = value.ValueSpan;
        if (IsExcluded(value.Value))
            return;
        var digitOrSymbol = span.ContainsAnyExcept(Letters);
        var mixed = span.Length >= 16 && span.ContainsAnyInRange('A', 'Z') && span.ContainsAnyInRange('a', 'z');
        if (digitOrSymbol || mixed)
            found.Add(new(CredentialKind.Authorization, value.Index, value.Length, 0, CredentialStrength.Labelled));
    }

    private static readonly System.Buffers.SearchValues<char> Letters =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    // "sk-learn-pipeline-examples" is a slug: every hyphen-separated part is lower-case letters.
    private static bool IsSlug(ReadOnlySpan<char> body)
    {
        if (body.IndexOf('-') < 0)
            return false;
        foreach (var c in body)
            if (c != '-' && !char.IsAsciiLetterLower(c))
                return false;
        return true;
    }

    private static void FindLabelled(string text, List<CredentialMatch> found)
    {
        // Assignments accept any value that is not excluded: a value under "password=" is the password, whatever it looks like.
        foreach (Match match in LabelledValue().Matches(text))
        {
            var group = match.Groups["value"];
            var (start, length) = Unquote(group);
            var bare = IsBare(group);
            if (bare && IsCutExemption(text, start, length))
                continue;
            if (bare)
                length = TrimBareEnd(text, start, length);
            if (length == 0)
                continue;
            var value = text.Substring(start, length);
            if (IsExcluded(value) || (bare && IsStopword(value)) || (bare && IsBlockIndicator(value)))
                continue;
            // PWD=/home/me is the shell's working directory, not a password.
            if (match.Groups["label"].ValueSpan.EndsWith("pwd", StringComparison.OrdinalIgnoreCase) && IsPathOrUrl(value))
                continue;
            // nextPageToken=..., page_token=...: a pagination cursor, which a model needs back to fetch the next page.
            if (CursorLabel().IsMatch(match.Groups["label"].ValueSpan))
                continue;
            found.Add(new(CredentialKind.LabelledValue, start, length, 0, CredentialStrength.Labelled));
        }

        foreach (Match match in UserOption().Matches(text))
        {
            var value = match.Groups["value"];
            if (!IsExcluded(value.Value))
                found.Add(new(CredentialKind.UserPassword, value.Index, value.Length, 0, CredentialStrength.Labelled));
        }

        foreach (Match match in UrlPassword().Matches(text))
        {
            var value = match.Groups["value"];
            if (!IsExcluded(value.Value))
                found.Add(new(CredentialKind.UrlPassword, value.Index, value.Length, 0, CredentialStrength.Labelled));
        }

        // Prose names a password in passing ("the password is stored in Vault"), so its value must also look like one.
        foreach (Match match in ProseIs().Matches(text))
            if (!LabelModifiers.Contains(FirstWord(match.Groups["words"].ValueSpan)))
                AddProse(text, match.Groups["value"], found, plainWords: IsPasswordNoun(match.Groups["noun"].ValueSpan));
        foreach (Match match in ProseWith().Matches(text))
            AddProse(text, match.Groups["value"], found);
    }

    // A6: the nouns after which a plain word that ends the clause is taken as the password itself ("my wifi password is
    // sunshinerainbow"). Not "secret" or the key and token nouns: "the secret is patience" is prose, and a key or token is
    // never one plain word.
    private static bool IsPasswordNoun(ReadOnlySpan<char> noun) =>
        noun.StartsWith("password", StringComparison.OrdinalIgnoreCase) || noun.Equals("passwd", StringComparison.OrdinalIgnoreCase)
        || noun.Equals("passphrase", StringComparison.OrdinalIgnoreCase) || noun.Equals("passcode", StringComparison.OrdinalIgnoreCase);

    private static void AddProse(string text, Group group, List<CredentialMatch> found, bool plainWords = false)
    {
        var (start, length) = Unquote(group);
        if (IsBare(group))
        {
            if (IsCutExemption(text, start, length))
                return;
            length = TrimBareEnd(text, start, length);
        }
        if (length == 0)
            return;
        var value = text.Substring(start, length);
        if (IsExcluded(value) || IsStopword(value) || IsPathOrUrl(value))
            return;
        if (IsSecretLike(value) || (plainWords && (IsBare(group) ? IsPlainWordPassword(text, start, length) : IsQuotedPassword(value))))
            found.Add(new(CredentialKind.ProseValue, start, length, 0, CredentialStrength.Labelled));
    }

    /// <summary>The fewest letters a plain-word password in prose has (A6): "is ok", "is set" and "is blank" are never one.</summary>
    internal const int MinPlainWordLength = 8;

    // Words after "the password is" that describe the password rather than give it, beyond the stopwords. Shorter words than
    // MinPlainWordLength are never taken, so only longer ones need listing.
    private static readonly FrozenWords Descriptors = new(
        "different", "identical", "separate", "sensitive", "temporary", "permanent", "mandatory", "insecure", "plaintext",
        "cleartext", "available", "unavailable", "forgotten", "something", "whatever", "anything", "nothing", "everything",
        "important", "personal", "confidential", "terrible", "excellent", "standard", "universal", "irrelevant", "elsewhere",
        "somewhere", "nowhere", "anywhere", "therefore", "obsolete", "outdated", "unchanged", "untouched", "whichever",
        "lowercase", "uppercase", "alphanumeric", "numeric", "encrypted", "unencrypted", "complicated", "unsecure",
        "strongest", "weakest", "shortest", "longest", "together", "unknown", "nonexistent", "inactive", "disabled", "enabled");

    // What may follow a plain-word password: the clause ends with it, so it is the password and not the start of a
    // description ("the password is different from the old one").
    private static readonly FrozenWords ClauseFollowers = new(
        "and", "but", "so", "or", "then", "now", "for", "on", "at", "in", "from", "if", "when", "until", "since", "while");

    // Adjective, participle and adverb endings: "is guessable", "is hardcoded", "is expiring", "is strictly", "is worthless".
    private static readonly string[] DescriptorEndings = ["ed", "ing", "ly", "able", "ible", "ous", "less", "ful", "ive"];

    /// <summary>
    /// A6: the labelled-prose rule for a password that is one plain word ("my wifi password is sunshinerainbow"), which
    /// <see cref="IsSecretLike"/> turns away for want of a digit or symbol: letters only, at least
    /// <see cref="MinPlainWordLength"/> of them, no stopword, descriptor or adjective ending, and the clause ends with it.
    /// </summary>
    private static bool IsPlainWordPassword(string text, int start, int length)
    {
        var word = text.AsSpan(start, length);
        if (word.Length < MinPlainWordLength || word.Length > 64)
            return false;
        foreach (var c in word)
        {
            if (!char.IsLetter(c))
                return false;
        }

        if (Descriptors.Contains(word))
            return false;
        foreach (var ending in DescriptorEndings)
        {
            if (word.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return EndsClause(text, start + length);
    }

    // A quoted value after a password noun is the password whatever it holds ("my password is 'sunshine'"), once it has six
    // characters or more and is not a descriptor.
    private static bool IsQuotedPassword(string value) =>
        value.Trim().Length >= 6 && !Descriptors.Contains(value.AsSpan().Trim());

    private static bool EndsClause(string text, int end)
    {
        if (end >= text.Length)
            return true;
        var next = text[end];
        if (next is '.' or ',' or ';' or ':' or '!' or '?' or ')' or '\r' or '\n' or '"' or '\'')
            return true;
        if (next is not (' ' or '\t'))
            return false;
        var rest = text.AsSpan(end).TrimStart(" \t");
        if (rest.IsEmpty || rest[0] is '\r' or '\n' or '(' or '-' or '–' or '—')
            return true;
        var stop = rest.IndexOfAny(" \t\r\n.,;:!?)");
        return ClauseFollowers.Contains(stop < 0 ? rest : rest[..stop]);
    }

    // A sentence's own punctuation is not part of a bare value; "!" and "?" are kept, since passwords end in them. A bare value
    // may hold a quote (Xk9"vQ2), so one closing quote around the assignment (say "TOKEN=abc") is trimmed too, with the
    // backslash that escapes it in JSON text ("TOKEN=abc\"" is TOKEN=abc" in the leaf).
    private static int TrimBareEnd(string text, int start, int length)
    {
        length = TrimSentenceEnd(text, start, length);
        if (length > 1 && text[start + length - 1] is '"' or '\'' or '`')
        {
            length--;
            var backslashes = 0;
            while (backslashes < length && text[start + length - 1 - backslashes] == '\\')
                backslashes++;
            if (backslashes % 2 == 1)
                length--;
            length = TrimSentenceEnd(text, start, length);
        }
        return length;
    }

    private static int TrimSentenceEnd(string text, int start, int length)
    {
        while (length > 0 && text[start + length - 1] is '.' or ',' or ';' or ':' or ')' or ']' or '}')
            length--;
        return length;
    }

    // A bare value stops before "]" and "}" and at a space, so a redaction's own output (ghp_[REDACTED]) and a reference
    // (${GITHUB_TOKEN}, ${{ secrets.X }}) arrive cut short. Either is left as it is; redacting ghp_[REDACTED] again would give
    // ghp_[REDACTED]], so this keeps Redact idempotent.
    private static bool IsCutExemption(string text, int start, int length)
    {
        var end = start + length;
        // A prose value keeps its "]" until the sentence's punctuation is trimmed.
        if (RedactionOutput().IsMatch(text.AsSpan(start, length)))
            return true;
        // The scrub's markers (ghp_[REDACTED:github-token:3fa91c0e], C-040) arrive cut before their "]" the same way.
        if (end < text.Length && text[end] == ']' && length > 0 && text[start + length - 1] != ']')
            return RedactionOutput().IsMatch(text.AsSpan(start, length + 1));
        if (length == 0 || text[start] is not ('$' or '{'))
            return false;
        var close = text.IndexOf('}', start);
        if (close < 0 || close - start > 256)
            return false;
        if (close + 1 < text.Length && text[close + 1] == '}')
            close++;
        return close + 1 >= end && Reference().IsMatch(text.AsSpan(start, close + 1 - start));
    }

    // "password: |" opens a YAML block scalar; the value is on the lines after it, which no rule reads (C-032's deferrals).
    private static bool IsBlockIndicator(string value) => value.Length <= 3 && value[0] is '|' or '>';

    private static void FindEncoded(string text, List<CredentialMatch> found)
    {
        foreach (var match in LongHex().EnumerateMatches(text))
            found.Add(new(CredentialKind.EncodedHex, match.Index, match.Length, 0, CredentialStrength.Encoded));
        foreach (var match in LongBase64().EnumerateMatches(text))
            if (IsEncodedBlob(text.AsSpan(match.Index, match.Length)) && !IsExcluded(text.Substring(match.Index, match.Length)))
                found.Add(new(CredentialKind.EncodedBase64, match.Index, match.Length, 0, CredentialStrength.Encoded));
    }

    /// <summary>
    /// A base64 run that mixes digits and both cases and is not a path. One without <c>+</c>, <c>/</c> or <c>=</c> counts only
    /// when it also switches class as often as random text does, so a long PascalCase identifier with a digit is no blob.
    /// </summary>
    private static bool IsEncodedBlob(ReadOnlySpan<char> value)
    {
        if (value.Count('/') >= 2)
            return false;
        if (!(value.ContainsAnyInRange('0', '9') && value.ContainsAnyInRange('A', 'Z') && value.ContainsAnyInRange('a', 'z')))
            return false;
        return value.IndexOfAny("+/=") >= 0 || HighSwitchRate(value);
    }

    /// <summary>Whether <paramref name="value"/> changes between upper case, lower case, digits and other as often as random text.</summary>
    internal static bool HighSwitchRate(ReadOnlySpan<char> value)
    {
        if (value.Length < 2)
            return false;
        var switches = 0;
        for (var i = 1; i < value.Length; i++)
            if (Class(value[i]) != Class(value[i - 1]))
                switches++;
        return switches >= MinSwitchRate * (value.Length - 1);

        static int Class(char c) => char.IsAsciiLetterUpper(c) ? 0 : char.IsAsciiLetterLower(c) ? 1 : char.IsAsciiDigit(c) ? 2 : 3;
    }

    /// <summary>
    /// The gate for a prose value: at least 8 characters with a digit or a symbol, a hyphenated phrase of at least 12
    /// (<c>correct-horse-battery-staple</c>), or at least 16 mixed-case characters that switch class like random text.
    /// </summary>
    internal static bool IsSecretLike(string value)
    {
        var span = value.AsSpan();
        if (span.Length >= 8 && (span.ContainsAnyInRange('0', '9') || HasSymbol(span)))
            return true;
        if (span.Length >= 12 && span.Count('-') >= 1 && !span.StartsWith('-') && !span.EndsWith('-'))
            return true;
        return span.Length >= 16 && span.ContainsAnyInRange('A', 'Z') && span.ContainsAnyInRange('a', 'z') && HighSwitchRate(span);

        // Hyphens, apostrophes and underscores join words; they do not make one a secret.
        static bool HasSymbol(ReadOnlySpan<char> span)
        {
            foreach (var c in span)
                if (!char.IsLetterOrDigit(c) && c is not '-' and not '\'' and not '_' && !char.IsWhiteSpace(c))
                    return true;
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="value"/>, as a whole, is one no rule reports (see the class remarks): for a caller that decodes a
    /// match (a JSON-escaped <c><CREDENTIAL_1></c>) and must check the decoded value again.
    /// </summary>
    public static bool IsExempt(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return IsExcluded(value);
    }

    /// <summary>
    /// Whether <paramref name="name"/>, as a whole, is a label whose value the labelled rule reports (<c>password</c>,
    /// <c>GITHUB_TOKEN</c>, <c>api_key</c>, <c>db_pass</c>): for a caller that walks JSON and sees a key apart from its value.
    /// A pagination cursor (<c>nextPageToken</c>, <c>page_token</c>, <c>NextContinuationToken</c>) is not one.
    /// </summary>
    public static bool IsSecretLabel(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length is > 0 and <= 128 && SecretLabel().IsMatch(name) && !CursorLabel().IsMatch(name);
    }

    /// <summary>The values no rule ever reports (see the class remarks).</summary>
    internal static bool IsExcluded(string value)
    {
        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty)
            return true;
        // Only a whole value is exempt: a secret that merely holds [REDACTED], a placeholder, a $ or angle brackets is not.
        // [REDACTED] alone or after a public prefix (ghp_[REDACTED]) is this detector's own output.
        if (RedactionOutput().IsMatch(trimmed) || IsWholePlaceholder(trimmed.ToString()))
            return true;
        // Variable references and templates ($VAR, ${VAR}, $(cmd), %VAR%, {{ secrets.X }}) and placeholders whose content
        // reads as a name (<CREDENTIAL_1>, <YOUR_API_KEY>, the neutralized ‹EMAIL_1›).
        if (Reference().IsMatch(trimmed))
            return true;
        if (trimmed.Equals("null", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("nil", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("undefined", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IsScheme(trimmed))
            return true;
        if (trimmed.Length <= 6 && !trimmed.ContainsAnyExceptInRange('0', '9'))
            return true;
        // A mask: ***, xxxx, ••••.
        return !trimmed.ContainsAnyExcept("*xX•·#.-_?");
    }

    private static bool IsWholePlaceholder(string value)
    {
        if (!value.StartsWith(Placeholders.Prefix, StringComparison.Ordinal))
            return false;
        var match = Placeholders.Find(value).FirstOrDefault();
        return match.Value is not null && match.Index == 0 && match.Length == value.Length;
    }

    private static bool IsScheme(ReadOnlySpan<char> value) =>
        value.Equals("bearer", StringComparison.OrdinalIgnoreCase) || value.Equals("basic", StringComparison.OrdinalIgnoreCase)
        || value.Equals("token", StringComparison.OrdinalIgnoreCase) || value.Equals("digest", StringComparison.OrdinalIgnoreCase)
        || value.Equals("negotiate", StringComparison.OrdinalIgnoreCase);

    private static bool IsStopword(string value) => Stopwords.Contains(value.AsSpan().Trim());

    private static bool IsPathOrUrl(string value) =>
        value.Contains("://", StringComparison.Ordinal) || value[0] is '/' or '~' || value.StartsWith("./", StringComparison.Ordinal)
        || value.StartsWith("../", StringComparison.Ordinal);

    private static ReadOnlySpan<char> FirstWord(ReadOnlySpan<char> words)
    {
        words = words.TrimStart();
        var end = words.IndexOfAny(" \t\r\n");
        return end < 0 ? words : words[..end];
    }

    private static bool IsBare(Group value) => value.Length == 0 || value.ValueSpan[0] is not ('"' or '\'');

    private static (int Start, int Length) Unquote(Group value) =>
        value.Length >= 2 && value.ValueSpan[0] is '"' or '\'' && value.ValueSpan[^1] == value.ValueSpan[0]
            ? (value.Index + 1, value.Length - 2)
            : (value.Index, value.Length);

    // Overlapping candidates from different rules become one span covering both, so no rule's extent leaks past another's.
    // The earliest, longest, strongest candidate names the span and keeps its prefix.
    private static ImmutableArray<CredentialMatch> Merge(List<CredentialMatch> found)
    {
        if (found.Count == 0)
            return [];
        found.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start)
            : a.Length != b.Length ? b.Length.CompareTo(a.Length)
            : a.Strength.CompareTo(b.Strength));
        var merged = ImmutableArray.CreateBuilder<CredentialMatch>();
        var current = found[0];
        foreach (var next in found.Skip(1))
        {
            if (next.Start < current.End)
            {
                if (next.End > current.End)
                    current = current with { Length = next.End - current.Start };
                continue;
            }
            merged.Add(current);
            current = next;
        }
        merged.Add(current);
        return merged.ToImmutable();
    }

    /// <summary>A small case-insensitive word set that looks up spans without allocating.</summary>
    private sealed class FrozenWords(params string[] words)
    {
        private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup =
            new HashSet<string>(words, StringComparer.OrdinalIgnoreCase).GetAlternateLookup<ReadOnlySpan<char>>();

        public bool Contains(ReadOnlySpan<char> word) => _lookup.Contains(word);
    }

    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    private const RegexOptions IgnoreCase = Options | RegexOptions.IgnoreCase;

    // The header, then key material (optionally after RFC 1421 headers such as Proc-Type, and with JSON-escaped line breaks),
    // to the END line or, for a block cut short, the end of the text. Text that only names the header is not a block.
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----(?:\s|\\[rn])*(?:[A-Za-z][A-Za-z0-9-]*: [^\r\n\\]*(?:\s|\\[rn])+)*[A-Za-z0-9+/=]{16,}[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----|$)",
        Options, TimeoutMilliseconds)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----", Options, TimeoutMilliseconds)]
    private static partial Regex PrivateKeyHeader();

    // Authorization: Bearer|Basic|token|Digest <value>, as a header, a JSON key or an assignment.
    [GeneratedRegex(@"\bauthorization[""'`]?[ \t]*[:=][ \t]*[""'`]?(?:(?:bearer|basic|token|digest|negotiate)[ \t]+)?(?<value>[A-Za-z0-9._~+/=-]{8,})",
        IgnoreCase, TimeoutMilliseconds)]
    private static partial Regex AuthorizationValue();

    [GeneratedRegex(@"\bbearer[ \t]+(?<value>[A-Za-z0-9._~+/=-]{8,})", IgnoreCase, TimeoutMilliseconds)]
    private static partial Regex BearerValue();

    [GeneratedRegex(@"\b(?:AKIA|ASIA|ABIA|ACCA)[A-Z0-9]{16}\b", Options, TimeoutMilliseconds)]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{20,}", Options, TimeoutMilliseconds)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bgithub_pat_[A-Za-z0-9_]{20,}", Options, TimeoutMilliseconds)]
    private static partial Regex GitHubFineGrained();

    // Bot, user, app-level, refresh and legacy tokens: xoxb-, xoxp-, xoxa-, xoxe-, xoxo-, xoxs-, xoxr-, xapp-. Slugs are dropped in code.
    [GeneratedRegex(@"\b(?:xox[abeposr]|xapp)-[A-Za-z0-9-]{10,}", Options, TimeoutMilliseconds)]
    private static partial Regex SlackToken();

    // Anthropic sk-ant-, OpenAI sk-proj- and plain sk- keys; slugs are dropped in code.
    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{16,}", Options, TimeoutMilliseconds)]
    private static partial Regex SkKey();

    [GeneratedRegex(@"\b[sr]k_live_[A-Za-z0-9]{16,}", Options, TimeoutMilliseconds)]
    private static partial Regex StripeKey();

    [GeneratedRegex(@"\bglpat-[A-Za-z0-9_-]{20,}", Options, TimeoutMilliseconds)]
    private static partial Regex GitLabToken();

    [GeneratedRegex(@"\bAIza[A-Za-z0-9_-]{35}", Options, TimeoutMilliseconds)]
    private static partial Regex GoogleApiKey();

    [GeneratedRegex(@"\bhf_[A-Za-z0-9]{30,}", Options, TimeoutMilliseconds)]
    private static partial Regex HuggingFaceToken();

    // The payload need not start eyJ, as the lesson rule this replaced did not require it.
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", Options, TimeoutMilliseconds)]
    private static partial Regex Jwt();

    // key=value, key: value, "key": "value", export KEY='value': a label naming a credential (any prefix, so GITHUB_TOKEN and
    // aws_secret_access_key count; pass and pwd only whole or after a separator, so DB_PASS and Pwd= count and compass does
    // not), the closing quote of a quoted label, a separator on the same line, then a quoted value to its closing quote, past
    // backslash escapes (a JSON \" is inside the value), or a bare value to whitespace or , ; & ) ] }. A bare value may hold a
    // quote after its first character (Xk9"vQ2). One value group; quotes are stripped in code.
    [GeneratedRegex(
        """\b(?<label>[A-Za-z0-9_.-]*(?:api[_-]?key|access[_-]?key|secret[_-]?key|secret|token|password|passwd|passphrase|private[_-]?key|client[_-]?secret)|(?:[A-Za-z0-9_.-]*[_.-])?(?:pass|pwd))["'`]?[ \t]*[:=][ \t]*(?<value>"(?:[^"\\\r\n]|\\[^\r\n])*"|'(?:[^'\\\r\n]|\\[^\r\n])*'|[^\s,;"'`}\]&)\[][^\s,;}\]&)]*)""",
        IgnoreCase, TimeoutMilliseconds)]
    private static partial Regex LabelledValue();

    // LabelledValue's label, whole.
    [GeneratedRegex(
        """\A(?:[A-Za-z0-9_.-]*(?:api[_-]?key|access[_-]?key|secret[_-]?key|secret|token|password|passwd|passphrase|private[_-]?key|client[_-]?secret)|(?:[A-Za-z0-9_.-]*[_.-])?(?:pass|pwd))\z""",
        IgnoreCase, TimeoutMilliseconds)]
    private static partial Regex SecretLabel();

    // A pagination cursor's label, whole: nextPageToken, page_token, next_token, NextContinuationToken, cursor_token. Not
    // csrf_token, which authenticates a form beside its session cookie, nor sync_token or resume_token, which some services
    // issue as credentials.
    [GeneratedRegex(@"\A[A-Za-z0-9_.-]*?(?:page|next|prev|previous|continuation|cursor)[_.-]?token\z", IgnoreCase, TimeoutMilliseconds)]
    private static partial Regex CursorLabel();

    // curl -u user:PASSWORD, --user user:PASSWORD, --user='user:PASSWORD'.
    [GeneratedRegex(
        """(?:\A|\s)(?:-u|--user|--proxy-user)(?:[ \t]+|=)(?:"[^":\s]+:(?<value>(?:[^"\\\r\n]|\\[^\r\n])+)"|'[^':\s]+:(?<value>[^'\r\n]+)'|[^\s:"'@]+:(?<value>[^\s"'@]+))""",
        Options, TimeoutMilliseconds)]
    private static partial Regex UserOption();

    // The scrub's kind names (CredentialScrub.KindName, plus its fallback and Unscannable), enumerated: an open kind pattern
    // would make [REDACTED:sk-abcdefghijklmnopqrst] a marker and hide the key inside it from every rule.
    private const string MarkerKinds =
        "(?:private-key-header|private-key|authorization|aws-access-key|github-token|slack-token|sk-key|stripe-key|gitlab-token"
        + "|google-api-key|huggingface-token|jwt|labelled-value|url-password|prose-value|encoded-hex|encoded-base64|user-password"
        + "|credential|unscannable)";

    // [REDACTED], alone or after a public prefix this detector keeps: what Redact writes. Also the scrub's markers (C-040):
    // [REDACTED:labelled-value], ghp_[REDACTED:github-token:3fa91c0e].
    [GeneratedRegex(@"\A(?:gh[pousr]_|github_pat_|xox[abeposr]-|xapp-|sk-|[sr]k_live_|glpat-|AIza|hf_|eyJ|AKIA|ASIA|ABIA|ACCA)?\[REDACTED(?::" + MarkerKinds + @"(?::[0-9a-f]{8})?)?\]\z",
        Options, TimeoutMilliseconds)]
    private static partial Regex RedactionOutput();

    // RedactionOutput anywhere in a text.
    [GeneratedRegex(@"(?:\bgh[pousr]_|\bgithub_pat_|\bxox[abeposr]-|\bxapp-|\bsk-|\b[sr]k_live_|\bglpat-|\bAIza|\bhf_|\beyJ|\bAKIA|\bASIA|\bABIA|\bACCA)?\[REDACTED(?::" + MarkerKinds + @"(?::[0-9a-f]{8})?)?\]",
        Options, TimeoutMilliseconds)]
    private static partial Regex Marker();

    // A whole value that is a reference or a placeholder: $VAR, ${VAR}, $(cmd), %VAR%, {{ ... }}, <NAME>, ‹NAME›.
    [GeneratedRegex(
        """\A(?:\$[A-Za-z_][A-Za-z0-9_]*|\$\{[A-Za-z_][A-Za-z0-9_]*\}|\$\([^()\r\n]*\)|%[A-Za-z_][A-Za-z0-9_]*%|\$\{\{[^{}\r\n]*\}\}|\{\{[^{}\r\n]*\}\}|<[A-Za-z][A-Za-z0-9_ .-]*>|‹[A-Za-z][A-Za-z0-9_ .-]*›)\z""",
        Options, TimeoutMilliseconds)]
    private static partial Regex Reference();

    // scheme://user:PASSWORD@host
    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9+.-]{1,20}://[^\s:/@]{1,128}:(?<value>[^\s/@]{1,256})@", Options, TimeoutMilliseconds)]
    private static partial Regex UrlPassword();

    // "the password is X", "the wifi password at the cabin is X", "the root password was changed to X", "secret access key: X".
    // The longer separators come first: the engine prefers the first alternative that matches, as a backtracking one would.
    [GeneratedRegex(
        """\b(?<noun>passwords?|passwd|passphrase|passcode|secret access key|secret key|access key|api key|api token|access token|auth token|bot token|bearer token|refresh token|session token|client secret|secret)(?<words>(?:[ \t]+[A-Za-z0-9_'-]+){0,5}?)(?:[ \t]+(?:is|was|has[ \t]+been)[ \t]+(?:set|changed|reset)[ \t]+to|[ \t]+(?:set|changed|reset)[ \t]+to|[ \t]+(?:is|was)(?:[ \t]+(?:now|still))?|[ \t]*[:=])[ \t]+(?<value>"(?:[^"\\\r\n]|\\[^\r\n]){1,200}"|'(?:[^'\\\r\n]|\\[^\r\n]){1,200}'|[^\s"'`<>][^\s<>]{0,255})""",
        IgnoreCase, TimeoutMilliseconds)]
    private static partial Regex ProseIs();

    // "log in with password X", "use the passphrase X".
    [GeneratedRegex(
        """\b(?:with|using|use)[ \t]+(?:the[ \t]+|a[ \t]+|my[ \t]+)?(?:password|passphrase|passcode)[ \t]+(?<value>"(?:[^"\\\r\n]|\\[^\r\n]){1,200}"|'(?:[^'\\\r\n]|\\[^\r\n]){1,200}'|[^\s"'`<>][^\s<>]{0,255})""",
        IgnoreCase, TimeoutMilliseconds)]
    private static partial Regex ProseWith();

    [GeneratedRegex(@"\b[0-9a-fA-F]{32,}\b", Options, TimeoutMilliseconds)]
    private static partial Regex LongHex();

    [GeneratedRegex(@"[A-Za-z0-9+/_-]{40,}={0,2}", Options, TimeoutMilliseconds)]
    private static partial Regex LongBase64();
}
