using System.Text.Json.Serialization;

namespace Madi.Runtime.Authentication;

[JsonSerializable(typeof(Dictionary<string, OAuthCredential>))]
[JsonSerializable(typeof(string))]
public partial class CredentialJsonContext : JsonSerializerContext;
