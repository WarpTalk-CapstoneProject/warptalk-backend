using System.Text.Json.Serialization;

namespace WarpTalk.AuthService.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccountStatus
{
    PENDING,
    ACTIVE,
    DISABLED,
    LOCKED
}
