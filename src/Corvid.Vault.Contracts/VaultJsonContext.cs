using System.Text.Json.Serialization;

namespace Corvid.Vault.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VaultRequest))]
[JsonSerializable(typeof(VaultResponse))]
public sealed partial class VaultJsonContext : JsonSerializerContext;
