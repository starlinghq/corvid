using System.Text.Json.Serialization;

namespace Corvid.Vault;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StoreDocument))]
[JsonSerializable(typeof(PolicyDocument))]
[JsonSerializable(typeof(AuditEvent))]
internal sealed partial class VaultInternalJsonContext : JsonSerializerContext;
