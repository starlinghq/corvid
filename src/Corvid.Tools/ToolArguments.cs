using System.Collections;
using System.Globalization;
using System.Text.Json;
using Corvid.Vault.Client;
using Microsoft.Extensions.AI;

namespace Corvid.Tools;

/// <summary>An argument the model supplied cannot be bound to the tool's parameter. The message is written for the repair step (§7).</summary>
public sealed class ToolArgumentException : ArgumentException
{
    public ToolArgumentException(string message, string parameter)
        : base(message, parameter)
    {
    }

    public ToolArgumentException()
    {
    }

    public ToolArgumentException(string message)
        : base(message)
    {
    }

    public ToolArgumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Argument binding used by generated tool wrappers. Values arrive as <see cref="JsonElement"/> from a tool-call parser
/// or as CLR values from code; both are accepted. Conversions are explicit per type, so binding needs no reflection.
/// </summary>
public static class ToolArguments
{
    /// <summary>The raw value of a required parameter, which may be null. Throws when the model omitted it.</summary>
    public static object? Get(AIFunctionArguments arguments, string parameter)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.TryGetValue(parameter, out object? value)
            ? value
            : throw new ToolArgumentException($"Missing required argument '{parameter}'.", parameter);
    }

    /// <summary>False when the argument is absent or null, so an optional parameter keeps its default.</summary>
    public static bool TryGet(AIFunctionArguments arguments, string parameter, out object? value)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.TryGetValue(parameter, out value) && !IsNull(value);
    }

    public static bool IsNull(object? value) => value is null || value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };

    public static string ReadString(object? value, string parameter) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
        _ => throw Mismatch(parameter, "a string", value),
    };

    public static bool ReadBoolean(object? value, string parameter) => value switch
    {
        bool b => b,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        string s when bool.TryParse(s, out bool parsed) => parsed,
        _ => throw Mismatch(parameter, "true or false", value),
    };

    public static int ReadInt32(object? value, string parameter) => value switch
    {
        int i => i,
        JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt32(out int parsed) => parsed,
        _ => ReadInteger(value, parameter, "a 32-bit integer") is var l and >= int.MinValue and <= int.MaxValue
            ? (int)l
            : throw Mismatch(parameter, "a 32-bit integer", value),
    };

    public static long ReadInt64(object? value, string parameter) => value switch
    {
        long l => l,
        JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt64(out long parsed) => parsed,
        _ => ReadInteger(value, parameter, "an integer"),
    };

    public static double ReadDouble(object? value, string parameter) => value switch
    {
        double d => d,
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
        IConvertible c and not string and not bool => c.ToDouble(CultureInfo.InvariantCulture),
        _ => throw Mismatch(parameter, "a number", value),
    };

    public static float ReadSingle(object? value, string parameter) => value is float f ? f : (float)ReadDouble(value, parameter);

    public static decimal ReadDecimal(object? value, string parameter) => value switch
    {
        decimal d => d,
        JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetDecimal(out decimal parsed) => parsed,
        string s when decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed) => parsed,
        IConvertible c and not string and not bool => c.ToDecimal(CultureInfo.InvariantCulture),
        _ => throw Mismatch(parameter, "a number", value),
    };

    public static Guid ReadGuid(object? value, string parameter) => value is Guid g
        ? g
        : Guid.TryParse(ReadString(value, parameter), out Guid parsed) ? parsed : throw Mismatch(parameter, "a UUID", value);

    public static DateTimeOffset ReadDateTimeOffset(object? value, string parameter) => value switch
    {
        DateTimeOffset d => d,
        DateTime d => d,
        _ => DateTimeOffset.TryParse(ReadString(value, parameter), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
            ? parsed
            : throw Mismatch(parameter, "an ISO 8601 date-time", value),
    };

    public static DateTime ReadDateTime(object? value, string parameter) => value switch
    {
        DateTime d => d,
        _ => DateTime.TryParse(ReadString(value, parameter), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed)
            ? parsed
            : throw Mismatch(parameter, "an ISO 8601 date-time", value),
    };

    public static Uri ReadUri(object? value, string parameter) => value is Uri u
        ? u
        : Uri.TryCreate(ReadString(value, parameter), UriKind.RelativeOrAbsolute, out Uri? parsed) ? parsed : throw Mismatch(parameter, "a URI", value);

    /// <summary>A secret name. The model can name a secret but never supply a value: <see cref="SecretRef"/> holds only a name.</summary>
    public static SecretRef ReadSecretRef(object? value, string parameter)
    {
        if (value is SecretRef secret)
        {
            return secret;
        }

        string name = ReadString(value, parameter);
        try
        {
            return new SecretRef(name);
        }
        catch (ArgumentException exception)
        {
            throw new ToolArgumentException($"Argument '{parameter}': {exception.Message}", parameter);
        }
    }

    public static T[] ReadArray<T>(object? value, string parameter, Func<object?, string, T> readElement)
    {
        ArgumentNullException.ThrowIfNull(readElement);
        switch (value)
        {
            case T[] typed:
                return typed;
            case JsonElement { ValueKind: JsonValueKind.Array } element:
            {
                var result = new T[element.GetArrayLength()];
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    result[index] = readElement(item, $"{parameter}[{index}]");
                    index++;
                }

                return result;
            }

            case IEnumerable<T> items:
                return [.. items];
            case IEnumerable items and not string:
            {
                var result = new List<T>();
                foreach (object? item in items)
                {
                    result.Add(readElement(item, $"{parameter}[{result.Count}]"));
                }

                return [.. result];
            }

            default:
                throw Mismatch(parameter, "an array", value);
        }
    }

    /// <summary>The model's value is not one of an enum's names.</summary>
    public static ToolArgumentException NotAllowed(string parameter, string value, string allowed) =>
        new($"Argument '{parameter}' is '{value}', which is not one of: {allowed}.", parameter);

    private static long ReadInteger(object? value, string parameter, string expected)
    {
        try
        {
            return value switch
            {
                long l => l,
                int or short or byte or sbyte or ushort or uint => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                ulong u => checked((long)u),
                JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt64(out long parsed) => parsed,
                string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
                _ => throw Mismatch(parameter, expected, value),
            };
        }
        catch (OverflowException)
        {
            throw Mismatch(parameter, expected, value);
        }
    }

    private static ToolArgumentException Mismatch(string parameter, string expected, object? value)
    {
        string actual = value switch
        {
            null => "null",
            JsonElement e => e.ValueKind == JsonValueKind.String ? $"\"{e.GetString()}\"" : e.GetRawText(),
            string s => $"\"{s}\"",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().Name,
        };
        if (actual.Length > 80)
        {
            actual = actual[..80] + "...";
        }

        return new ToolArgumentException($"Argument '{parameter}' must be {expected}; got {actual}.", parameter);
    }
}
