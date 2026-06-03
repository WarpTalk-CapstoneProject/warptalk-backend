using System.Text.Json.Serialization;

namespace WarpTalk.AuthService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MembershipType
{
    Internal,
    External
}
