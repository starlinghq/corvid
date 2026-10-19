using System.Text;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;
using Corvid.Kernel.Events;

namespace Corvid.Evals.Calibration;

/// <summary>What an export wrote.</summary>
/// <param name="MissingState">Kev records written without a turn state (decisions recorded before C-021 kept it).</param>
public sealed record ExportSummary(int Written, int Skipped, int MissingState);

/// <param name="LabelledOnly">Only decisions with a positive or negative outcome.</param>
/// <param name="CalibratedOnly">Only answers from a calibrated decider; by default degraded answers are included and marked.</param>
public sealed record KevExportOptions(bool LabelledOnly = false, bool CalibratedOnly = false);

/// <summary>
/// Writes decisions as Kev training records (plan §16), one JSON object per line: the turn state, the question with its
/// instructions and options in the order asked, the answer's probabilities, the acted answer and the outcome. Decisions
/// the decider did not answer (degraded gates) carry no probabilities and are skipped.
/// </summary>
public static class KevTrainingExport
{
    public const string Format = "corvid-kev-decision";
    public const int FormatVersion = 1;

    public static ExportSummary Write(DecisionSet decisions, TextWriter output, KevExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new KevExportOptions();
        int written = 0, skipped = 0, missingState = 0;
        foreach (var record in decisions.Records)
        {
            if (record.Answer is null || record.Question is null
                || options.LabelledOnly && record.Label is not (OutcomeLabel.Positive or OutcomeLabel.Negative)
                || options.CalibratedOnly && record.Tier != DecisionTier.Calibrated)
            {
                skipped++;
                continue;
            }
            if (record.Event.State is null)
                missingState++;
            output.Write(Line(record));
            output.Write('\n');
            written++;
        }
        return new ExportSummary(written, skipped, missingState);
    }

    /// <summary>One record as a single line of JSON.</summary>
    public static string Line(DecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var question = record.Question ?? throw new ArgumentException("The decision's question is not in the catalogue.", nameof(record));
        var answer = record.Answer ?? throw new ArgumentException("The decision has no answer.", nameof(record));
        var decision = record.Event;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, CalibrationJson.Line))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("format_version", FormatVersion);
            writer.WriteString("source", record.Source);
            writer.WriteString("session_id", record.SessionId);
            writer.WriteNumber("sequence", record.Sequence);
            writer.WriteString("decision_id", decision.DecisionId);
            writer.WriteString("question", record.QuestionId);
            WriteNullableString(writer, "instance", record.Instance);
            writer.WriteString("kind", DecisionFabric.KindWireName(question.Kind));
            writer.WriteString("role", CalibrationReport.Wire(question.Role));
            writer.WritePropertyName("state");
            if (decision.State is { } state)
                state.WriteTo(writer);
            else
                writer.WriteNullValue();

            // Instructions and option descriptions as asked when recorded; otherwise the catalogue's, which is what the
            // fabric sends unless the caller supplies its own.
            writer.WriteBoolean("question_recorded", decision.Question is not null);
            writer.WritePropertyName("instructions");
            if (decision.Question is { } asked)
                asked.Instructions.WriteTo(writer);
            else
                writer.WriteStringValue(question.Instructions);
            var descriptions = decision.Question is { } recorded
                ? recorded.Options.ToDictionary(option => option.Name, option => option.Description, StringComparer.Ordinal)
                : question.Options.ToDictionary(option => option.Name, option => option.Description, StringComparer.Ordinal);
            writer.WriteStartArray("options");
            for (var i = 0; i < answer.Options.Length; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("name", answer.Options[i]);
                WriteNullableString(writer, "description", descriptions.GetValueOrDefault(answer.Options[i]));
                writer.WriteNumber("probability", answer.Probabilities[i]);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("answer", answer.Choice);
            writer.WriteNumber("confidence", decision.Confidence);
            CalibrationReport.WriteNullable(writer, "measure", record.Measure);
            writer.WriteBoolean("acted", record.Acted);
            // A Noul answer that acts means yes, even when P(yes) is below one half under a low floor.
            WriteNullableString(writer, "acted_answer", !record.Acted ? null
                : question.Kind == QuestionKind.Noul ? DecisionQuestion.Yes : answer.Choice);
            writer.WriteString("action", decision.Action);
            writer.WriteNumber("threshold", decision.Threshold);
            WriteNullableString(writer, "floors_version", decision.CatalogueVersion);
            WriteNullableString(writer, "decider", decision.Decider);
            writer.WriteString("tier", CalibrationReport.Wire(record.Tier));
            writer.WriteBoolean("calibrated", record.Tier == DecisionTier.Calibrated);
            WriteNullableString(writer, "outcome", record.Outcome);
            writer.WriteString("outcome_label", record.Label switch
            {
                OutcomeLabel.Positive => "positive",
                OutcomeLabel.Negative => "negative",
                OutcomeLabel.Unlabelled => "unlabelled",
                _ => "none",
            });
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }
}

/// <summary>
/// Writes sessions as trajectories in the ShareGPT form Hermes uses (plan §16), one JSON object per line:
/// <c>{"id", "source", "conversations": [{"from", "value"}]}</c>. User messages are <c>human</c>; assistant text and tool
/// calls are <c>gpt</c>, with each call as <c>&lt;tool_call&gt;{"name", "arguments"}&lt;/tool_call&gt;</c>; tool results are
/// <c>tool</c>, as <c>&lt;tool_response&gt;{"tool_call_id", "name", "content"}&lt;/tool_response&gt;</c>. Consecutive turns
/// from one speaker are joined with a newline. The raw history is used (compactions expanded), so a summary never stands in
/// for what was said. Decisions and other events are not part of a trajectory.
/// </summary>
public static class ShareGptExport
{
    public static ExportSummary Write(DecisionSet decisions, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(output);
        int written = 0, skipped = 0;
        foreach (var session in decisions.Sessions)
        {
            var line = Line(session);
            if (line is null)
            {
                skipped++;
                continue;
            }
            output.Write(line);
            output.Write('\n');
            written++;
        }
        return new ExportSummary(written, skipped, 0);
    }

    /// <summary>One session as a single line of JSON, or null when it has no conversation.</summary>
    public static string? Line(SessionHistory session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var snapshot = SessionProjection.Build(session.SessionId, session.Events, expandCompactions: true);
        var tools = new Dictionary<string, string>(StringComparer.Ordinal);
        var turns = new List<(string From, StringBuilder Value)>();
        foreach (var entry in snapshot.Transcript)
        {
            var (from, value) = entry.Content switch
            {
                UserMessage message => ("human", message.Text),
                AssistantMessage message => ("gpt", message.Text),
                ToolCallIssued call => ("gpt", ToolCall(call, tools)),
                ToolResult result => ("tool", ToolResponse(result, tools)),
                _ => (null, null),
            };
            if (from is null)
                continue;
            if (turns.Count > 0 && turns[^1].From == from)
                turns[^1].Value.Append('\n').Append(value);
            else
                turns.Add((from, new StringBuilder(value)));
        }
        if (turns.Count == 0)
            return null;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, CalibrationJson.Line))
        {
            writer.WriteStartObject();
            writer.WriteString("id", session.SessionId);
            writer.WriteString("source", session.Source);
            writer.WriteStartArray("conversations");
            foreach (var (from, value) in turns)
            {
                writer.WriteStartObject();
                writer.WriteString("from", from);
                writer.WriteString("value", value.ToString());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ToolCall(ToolCallIssued call, Dictionary<string, string> tools)
    {
        tools[call.CallId] = call.ToolName;
        return "<tool_call>\n" + Json(writer =>
        {
            writer.WriteString("name", call.ToolName);
            writer.WritePropertyName("arguments");
            call.Arguments.WriteTo(writer);
        }) + "\n</tool_call>";
    }

    private static string ToolResponse(ToolResult result, Dictionary<string, string> tools) =>
        "<tool_response>\n" + Json(writer =>
        {
            writer.WriteString("tool_call_id", result.CallId);
            KevTrainingExport.WriteNullableString(writer, "name", tools.GetValueOrDefault(result.CallId));
            writer.WritePropertyName("content");
            result.Result.WriteTo(writer);
            if (result.IsError)
                writer.WriteBoolean("is_error", true);
        }) + "\n</tool_response>";

    private static string Json(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, CalibrationJson.Line))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
