using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Corvid.Tools.Generators;

/// <summary>Turns a [Tool] method symbol into a <see cref="ToolModel"/>: diagnostics, schema text and binding code.</summary>
internal static class ToolParser
{
    private const string Args = "global::Corvid.Tools.ToolArguments";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static ToolModel Parse(IMethodSymbol method, AttributeData attribute, CancellationToken cancellationToken)
    {
        INamedTypeSymbol type = method.ContainingType;
        LocationInfo? location = LocationInfo.From(method.Locations.FirstOrDefault());
        var diagnostics = new List<DiagnosticInfo>();
        void Report(DiagnosticDescriptor descriptor, params string[] arguments) =>
            diagnostics.Add(new DiagnosticInfo(descriptor.Id, location, new EquatableArray<string>([.. arguments])));

        string toolName = attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string explicitName
            ? explicitName
            : SnakeCase(method.Name);

        if (!IsPartialTopLevelClass(type, cancellationToken))
        {
            Report(Diagnostics.ContainingTypeNotPartial, method.Name);
        }

        if (method.IsGenericMethod)
        {
            Report(Diagnostics.GenericMethod, method.Name);
        }

        if (!IsValidName(toolName))
        {
            Report(Diagnostics.InvalidName, toolName);
        }

        string? description = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Description").Value.Value as string
            ?? DescriptionOf(method);
        if (string.IsNullOrWhiteSpace(description))
        {
            Report(Diagnostics.MissingDescription, toolName);
        }

        int sideEffects = 0;
        bool network = false;
        bool destructive = false;
        ITypeSymbol? sideEffectsType = null;
        foreach (AttributeData data in method.GetAttributes())
        {
            switch (data.AttributeClass?.ToDisplayString())
            {
                case "Corvid.Tools.SideEffectAttribute" when data.ConstructorArguments.Length == 1 && data.ConstructorArguments[0].Value is int value:
                    sideEffects |= value;
                    sideEffectsType = data.ConstructorArguments[0].Type;
                    break;
                case "Corvid.Tools.NetworkAttribute":
                    network = true;
                    break;
                case "Corvid.Tools.DestructiveAttribute":
                    destructive = true;
                    break;
            }
        }

        if (destructive && sideEffects == 0)
        {
            Report(Diagnostics.DestructiveWithoutSideEffect, toolName);
        }

        string target = method.IsStatic ? $"global::{type.ToDisplayString()}" : "_target";
        var properties = new List<string>();
        var required = new List<string>();
        var parameters = new List<string>();
        var bindings = new StringBuilder();
        var helpers = new StringBuilder();
        var callArguments = new List<string>();
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (parameter.RefKind == RefKind.None && !parameter.IsParams && parameter.Type.ToDisplayString() == "System.Threading.CancellationToken")
            {
                callArguments.Add("cancellationToken");
                continue;
            }

            // The turn's context for the call (session, taint, approval) comes from AIFunctionArguments.Context, never from
            // the model, so it is not in the schema. A nullable parameter accepts a call made outside a turn.
            if (parameter.RefKind == RefKind.None && !parameter.IsParams
                && parameter.Type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() == "Corvid.Tools.ToolCallContext")
            {
                callArguments.Add(parameter.NullableAnnotation == NullableAnnotation.Annotated
                    ? "global::Corvid.Tools.ToolCallContext.From(arguments)"
                    : $"global::Corvid.Tools.ToolCallContext.Require(arguments, {Literal(toolName)})");
                continue;
            }

            // NaN and infinity have no JSON form for the schema's "default".
            bool finiteDefault = !parameter.HasExplicitDefaultValue
                || parameter.ExplicitDefaultValue is not (double.NaN or double.PositiveInfinity or double.NegativeInfinity or float.NaN or float.PositiveInfinity or float.NegativeInfinity);
            TypeShape? shape = parameter.RefKind == RefKind.None && !parameter.IsParams && finiteDefault ? Classify(parameter.Type, helpers) : null;
            if (shape is null)
            {
                Report(Diagnostics.UnsupportedParameter, parameter.Name, toolName, parameter.Type.ToDisplayString());
                continue;
            }

            string name = parameter.Name;
            string local = $"__arg{callArguments.Count}";
            string declared = parameter.Type.ToDisplayString(TypeFormat);
            string? parameterDescription = DescriptionOf(parameter);
            bool isRequired = !parameter.HasExplicitDefaultValue;
            string literal = Literal(name);

            var schema = new StringBuilder("{");
            if (parameterDescription is not null)
            {
                schema.Append("\"description\":").Append(Json.String(parameterDescription)).Append(',');
            }

            schema.Append(shape.Schema);
            if (!isRequired)
            {
                schema.Append(",\"default\":").Append(DefaultJson(parameter));
            }

            schema.Append('}');
            properties.Add($"{Json.String(name)}:{schema}");
            if (isRequired)
            {
                required.Add(Json.String(name));
            }

            if (isRequired && shape.Nullable)
            {
                bindings.AppendLine($"object? {local}Raw = {Args}.Get(arguments, {literal});");
                bindings.AppendLine($"{declared} {local} = {Args}.IsNull({local}Raw) ? null : {shape.Read($"{local}Raw", literal)};");
            }
            else if (isRequired)
            {
                bindings.AppendLine($"{declared} {local} = {shape.Read($"{Args}.Get(arguments, {literal})", literal)};");
            }
            else if (shape.Nullable)
            {
                // The schema admits null, so an explicit null binds null; only an absent argument takes the default.
                bindings.AppendLine($"{declared} {local} = {Args}.TryGetPresent(arguments, {literal}, out object? {local}Raw) ? ({Args}.IsNull({local}Raw) ? ({declared})null : {shape.Read($"{local}Raw", literal)}) : {DefaultCSharp(parameter, declared)};");
            }
            else
            {
                bindings.AppendLine($"{declared} {local} = {Args}.TryGet(arguments, {literal}, out object? {local}Raw) ? {shape.Read($"{local}Raw", literal)} : {DefaultCSharp(parameter, declared)};");
            }

            parameters.Add($"new global::Corvid.Tools.ToolParameter({literal}, {(parameterDescription is null ? "null" : Literal(parameterDescription))}, {Bool(isRequired)}, {Bool(shape.IsSecret)})");
            callArguments.Add(local);
        }

        string call = $"{target}.{method.Name}({string.Join(", ", callArguments)})";
        (string body, bool isAsync) = ReturnStatement(method.ReturnType, call, toolName, Report);

        var schemaText = new StringBuilder("{\"type\":\"object\",\"properties\":{");
        schemaText.Append(string.Join(",", properties)).Append('}');
        if (required.Count > 0)
        {
            schemaText.Append(",\"required\":[").Append(string.Join(",", required)).Append(']');
        }

        schemaText.Append('}');

        string parameterArray = parameters.Count == 0
            ? "global::System.Array.Empty<global::Corvid.Tools.ToolParameter>()"
            : $"new global::Corvid.Tools.ToolParameter[] {{ {string.Join(", ", parameters)} }}";
        string descriptor =
            $"new global::Corvid.Tools.ToolDescriptor({Literal(toolName)}, {Literal(description ?? "")}, {Literal(schemaText.ToString())}, " +
            $"new global::Corvid.Tools.ToolRisk({SideEffectsExpression(sideEffects, sideEffectsType)}, {Bool(network)}, {Bool(destructive)}), {parameterArray})";

        return new ToolModel(
            type.ToDisplayString(),
            type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString(),
            type.Name,
            type.IsRecord ? "record" : "class",
            type.IsStatic,
            toolName,
            "__CorvidTool_" + new string([.. toolName.Select(c => char.IsLetterOrDigit(c) ? c : '_')]),
            descriptor,
            bindings.ToString() + body,
            isAsync,
            helpers.ToString().TrimEnd(),
            location,
            new EquatableArray<DiagnosticInfo>([.. diagnostics]));
    }

    /// <summary>How a parameter type is read and described. <see cref="Read"/> wraps a raw-value expression.</summary>
    private sealed class TypeShape(string schema, Func<string, string, string> read, bool nullable, bool isSecret = false)
    {
        public string Schema { get; } = schema;

        public bool Nullable { get; } = nullable;

        public bool IsSecret { get; } = isSecret;

        public string Read(string raw, string name) => read(raw, name);
    }

    private static TypeShape? Classify(ITypeSymbol type, StringBuilder helpers)
    {
        bool nullable = false;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } wrapped)
        {
            type = wrapped.TypeArguments[0];
            nullable = true;
        }
        else if (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            nullable = true;
        }

        (string Json, string Reader)? scalar = Scalar(type, helpers);
        if (scalar is { } s)
        {
            bool secret = s.Reader.EndsWith(".ReadSecretRef", StringComparison.Ordinal);
            return new TypeShape(WithNull(s.Json, nullable), (raw, name) => $"{s.Reader}({raw}, {name})", nullable, secret);
        }

        if (Element(type) is not (ITypeSymbol element, bool isList))
        {
            return null;
        }

        if (element is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } || element.NullableAnnotation == NullableAnnotation.Annotated)
        {
            return null;
        }

        // SecretRef stays scalar: an array of secrets would need per-element catalogue rendering.
        if (Scalar(element, helpers) is not { } item || item.Reader.EndsWith(".ReadSecretRef", StringComparison.Ordinal))
        {
            return null;
        }

        string elementType = element.ToDisplayString(TypeFormat);
        string arrayJson = WithNull($"\"type\":\"array\",\"items\":{{{item.Json}}}", nullable);
        return new TypeShape(
            arrayJson,
            (raw, name) => isList
                ? $"new global::System.Collections.Generic.List<{elementType}>({Args}.ReadArray<{elementType}>({raw}, {name}, {item.Reader}))"
                : $"{Args}.ReadArray<{elementType}>({raw}, {name}, {item.Reader})",
            nullable);
    }

    private static (string Json, string Reader)? Scalar(ITypeSymbol type, StringBuilder helpers)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                return ("\"type\":\"string\"", $"{Args}.ReadString");
            case SpecialType.System_Boolean:
                return ("\"type\":\"boolean\"", $"{Args}.ReadBoolean");
            case SpecialType.System_Int32:
                return ("\"type\":\"integer\"", $"{Args}.ReadInt32");
            case SpecialType.System_Int64:
                return ("\"type\":\"integer\"", $"{Args}.ReadInt64");
            case SpecialType.System_Single:
                return ("\"type\":\"number\"", $"{Args}.ReadSingle");
            case SpecialType.System_Double:
                return ("\"type\":\"number\"", $"{Args}.ReadDouble");
            case SpecialType.System_Decimal:
                return ("\"type\":\"number\"", $"{Args}.ReadDecimal");
            case SpecialType.System_DateTime:
                return ("\"type\":\"string\",\"format\":\"date-time\"", $"{Args}.ReadDateTime");
        }

        switch (type.ToDisplayString())
        {
            case "System.Guid":
                return ("\"type\":\"string\",\"format\":\"uuid\"", $"{Args}.ReadGuid");
            case "System.DateTimeOffset":
                return ("\"type\":\"string\",\"format\":\"date-time\"", $"{Args}.ReadDateTimeOffset");
            case "System.Uri":
                return ("\"type\":\"string\",\"format\":\"uri\"", $"{Args}.ReadUri");
            case "Corvid.Vault.Client.SecretRef":
                // The enum of catalogue names is added at runtime by ToolRegistry.GetFunctions (§10.5).
                return ("\"type\":\"string\"", $"{Args}.ReadSecretRef");
        }

        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType
            && !enumType.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"))
        {
            return EnumReader(enumType, helpers);
        }

        return null;
    }

    private static (string Json, string Reader) EnumReader(INamedTypeSymbol enumType, StringBuilder helpers)
    {
        string[] members = [.. enumType.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).Select(f => f.Name)];
        string qualified = enumType.ToDisplayString(TypeFormat);
        string helper = "Read" + enumType.ToDisplayString().Replace('.', '_');
        string declaration = $"private static {qualified} {helper}(object? value, string parameter) =>";
        if (!helpers.ToString().Contains(declaration))
        {
            // Names map to members by a switch, not Enum.Parse, and numbers are refused: the schema offers names only.
            helpers.AppendLine();
            helpers.AppendLine(declaration);
            helpers.AppendLine($"    value is {qualified} typed ? typed : {Args}.ReadString(value, parameter) switch");
            helpers.AppendLine("    {");
            foreach (string member in members)
            {
                // A member named after a keyword (declared as @default) needs the escape in generated code.
                string identifier = SyntaxFacts.GetKeywordKind(member) == SyntaxKind.None ? member : "@" + member;
                helpers.AppendLine($"        {Literal(member)} => {qualified}.{identifier},");
            }

            helpers.AppendLine($"        var other => throw {Args}.NotAllowed(parameter, other, {Literal(string.Join(", ", members))}),");
            helpers.AppendLine("    };");
        }

        return ($"\"type\":\"string\",\"enum\":[{string.Join(",", members.Select(Json.String))}]", helper);
    }

    private static (ITypeSymbol Element, bool IsList)? Element(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol { Rank: 1 } array)
        {
            return (array.ElementType, false);
        }

        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named)
        {
            switch (named.OriginalDefinition.ToDisplayString())
            {
                case "System.Collections.Generic.List<T>":
                    return (named.TypeArguments[0], true);
                case "System.Collections.Generic.IEnumerable<T>":
                case "System.Collections.Generic.IReadOnlyList<T>":
                case "System.Collections.Generic.IReadOnlyCollection<T>":
                case "System.Collections.Generic.IList<T>":
                case "System.Collections.Generic.ICollection<T>":
                    return (named.TypeArguments[0], false);
            }
        }

        return null;
    }

    private static string WithNull(string json, bool nullable)
    {
        if (!nullable)
        {
            return json;
        }

        // "type":"x" becomes "type":["x","null"]; an enum also admits null.
        int end = json.IndexOf('"', "\"type\":\"".Length);
        string kind = json.Substring("\"type\":\"".Length, end - "\"type\":\"".Length);
        string rest = json.Substring(end + 1);
        if (rest.StartsWith(",\"enum\":[", StringComparison.Ordinal))
        {
            rest = rest.Substring(0, rest.Length - 1) + ",null]";
        }

        return $"\"type\":[\"{kind}\",\"null\"]{rest}";
    }

    private static (string Body, bool IsAsync) ReturnStatement(ITypeSymbol returnType, string call, string toolName, Action<DiagnosticDescriptor, string[]> report)
    {
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            return ($"{call};\nreturn default;", false);
        }

        string definition = returnType.OriginalDefinition.ToDisplayString();
        if (definition is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask")
        {
            return ($"await {call}.ConfigureAwait(false);\nreturn null;", true);
        }

        if (definition is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
        {
            ITypeSymbol result = ((INamedTypeSymbol)returnType).TypeArguments[0];
            if (!IsSerializableResult(result))
            {
                report(Diagnostics.UnsupportedReturn, [toolName, returnType.ToDisplayString()]);
            }

            return ($"return await {call}.ConfigureAwait(false);", true);
        }

        if (!IsSerializableResult(returnType))
        {
            report(Diagnostics.UnsupportedReturn, [toolName, returnType.ToDisplayString()]);
        }

        return ($"return new global::System.Threading.Tasks.ValueTask<object?>({call});", false);
    }

    /// <summary>Results the chat client can serialize with the default AOT-safe options, without a caller-supplied type.</summary>
    private static bool IsSerializableResult(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } wrapped)
        {
            type = wrapped.TypeArguments[0];
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_String:
            case SpecialType.System_Boolean:
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_DateTime:
                return true;
        }

        if (type.ToDisplayString() is "System.Guid" or "System.DateTimeOffset" or "System.Text.Json.JsonElement")
        {
            return true;
        }

        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() is "System.Text.Json.Nodes.JsonNode" or "Microsoft.Extensions.AI.AIContent")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPartialTopLevelClass(INamedTypeSymbol type, CancellationToken cancellationToken) =>
        type.TypeKind == TypeKind.Class
        && type.ContainingType is null
        && !type.IsGenericType
        && type.DeclaringSyntaxReferences.Any(r =>
            r.GetSyntax(cancellationToken) is TypeDeclarationSyntax declaration
            && declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));

    private static string? DescriptionOf(ISymbol symbol) =>
        symbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.ComponentModel.DescriptionAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string;

    private static string DefaultJson(IParameterSymbol parameter)
    {
        object? value = parameter.ExplicitDefaultValue;
        ITypeSymbol type = parameter.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } wrapped
            ? wrapped.TypeArguments[0]
            : parameter.Type;
        if (value is null)
        {
            return "null";
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            IFieldSymbol? member = type.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, value));
            return member is null ? "null" : Json.String(member.Name);
        }

        return value switch
        {
            string s => Json.String(s),
            bool b => b ? "true" : "false",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => "null",
        };
    }

    private static string DefaultCSharp(IParameterSymbol parameter, string declared) => parameter.ExplicitDefaultValue switch
    {
        null => "default",
        // Without the suffix a decimal default would round-trip through double.
        decimal d => $"({declared})({d.ToString(CultureInfo.InvariantCulture)}m)",
        object value => $"({declared})({SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false)})",
    };

    private static string SideEffectsExpression(int value, ITypeSymbol? enumType)
    {
        const string Prefix = "global::Corvid.Tools.SideEffects";
        if (value == 0 || enumType is null)
        {
            return $"{Prefix}.None";
        }

        var parts = new List<string>();
        int remaining = value;
        foreach (IFieldSymbol member in enumType.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
        {
            int bit = Convert.ToInt32(member.ConstantValue, CultureInfo.InvariantCulture);
            if (bit != 0 && (remaining & bit) == bit)
            {
                parts.Add($"{Prefix}.{member.Name}");
                remaining &= ~bit;
            }
        }

        if (remaining != 0)
        {
            parts.Add($"({Prefix}){remaining.ToString(CultureInfo.InvariantCulture)}");
        }

        return string.Join(" | ", parts);
    }

    private static bool IsValidName(string name) =>
        name.Length is >= 1 and <= 64 && name.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-');

    internal static string SnakeCase(string name)
    {
        if (name.EndsWith("Async", StringComparison.Ordinal) && name.Length > "Async".Length)
        {
            name = name.Substring(0, name.Length - "Async".Length);
        }

        var result = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool boundary = i > 0 && char.IsUpper(c)
                && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
            if (boundary)
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(c));
        }

        return result.ToString();
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    private static string Bool(bool value) => value ? "true" : "false";
}

internal static class Json
{
    public static string String(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }
}
