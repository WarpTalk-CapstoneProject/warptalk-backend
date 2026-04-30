using System.Text.Json;
using System.Text.RegularExpressions;
using WarpTalk.Shared;

namespace WarpTalk.NotificationService.Application.Validators;

public static class NotificationValidator
{
    private static readonly Regex HtmlRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private class PayloadSchema
    {
        public Dictionary<string, JsonValueKind> RequiredFields { get; set; } = new();
        public Dictionary<string, JsonValueKind> OptionalFields { get; set; } = new();
    }

    private static readonly Dictionary<string, PayloadSchema> Schemas = new(StringComparer.OrdinalIgnoreCase)
    {
        {
            "SYSTEM", new PayloadSchema
            {
                OptionalFields = { { "action_url", JsonValueKind.String } }
            }
        },
        {
            "MEETING_INVITE", new PayloadSchema
            {
                RequiredFields = { { "meeting_id", JsonValueKind.String }, { "inviter_name", JsonValueKind.String } },
                OptionalFields = { { "action_url", JsonValueKind.String } }
            }
        },
        {
            "TRANSCRIPT_READY", new PayloadSchema
            {
                RequiredFields = { { "transcript_id", JsonValueKind.String }, { "meeting_name", JsonValueKind.String } },
                OptionalFields = { { "action_url", JsonValueKind.String } }
            }
        },
        {
            "MESSAGE", new PayloadSchema
            {
                RequiredFields = { { "sender_id", JsonValueKind.String }, { "sender_name", JsonValueKind.String }, { "room_id", JsonValueKind.String } },
                OptionalFields = { { "action_url", JsonValueKind.String } }
            }
        }
    };

    public static Result Validate(string type, string title, string content, string? payloadJson)
    {
        // 1. Check Title and Content for HTML
        if (HasHtml(title) || HasHtml(content))
        {
            return Result.Failure("HTML_NOT_ALLOWED", ErrorCodes.ValidationError);
        }

        // 2. Validate Payload
        if (string.IsNullOrWhiteSpace(payloadJson) || payloadJson == "{}")
        {
            // Empty payload might be fine for some types if no required fields exist
            if (Schemas.TryGetValue(type, out var schemaCheck) && schemaCheck.RequiredFields.Any())
            {
                return Result.Failure("MISSING_REQUIRED_FIELDS", ErrorCodes.ValidationError);
            }
            return Result.Success();
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Result.Failure("INVALID_PAYLOAD_FORMAT", ErrorCodes.ValidationError);
            }

            if (!Schemas.TryGetValue(type, out var schema))
            {
                // If type is unknown, we can either reject or accept with no payload. 
                // Strict approach: if type is unknown, payload should not contain anything.
                if (root.EnumerateObject().Any())
                {
                    return Result.Failure("UNSUPPORTED_NOTIFICATION_TYPE", ErrorCodes.ValidationError);
                }
                return Result.Success();
            }

            // Track found required fields
            var foundRequired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in root.EnumerateObject())
            {
                var key = prop.Name;
                var valueKind = prop.Value.ValueKind;

                // Check against Required
                if (schema.RequiredFields.TryGetValue(key, out var expectedKindRequired))
                {
                    if (valueKind != expectedKindRequired && valueKind != JsonValueKind.Null)
                        return Result.Failure("INVALID_FIELD_TYPE", ErrorCodes.ValidationError);
                        
                    // Check for HTML in string values
                    if (valueKind == JsonValueKind.String && HasHtml(prop.Value.GetString()!))
                        return Result.Failure("HTML_NOT_ALLOWED", ErrorCodes.ValidationError);
                        
                    foundRequired.Add(key);
                }
                // Check against Optional
                else if (schema.OptionalFields.TryGetValue(key, out var expectedKindOptional))
                {
                    if (valueKind != expectedKindOptional && valueKind != JsonValueKind.Null)
                        return Result.Failure("INVALID_FIELD_TYPE", ErrorCodes.ValidationError);

                    // Check for HTML in string values
                    if (valueKind == JsonValueKind.String && HasHtml(prop.Value.GetString()!))
                        return Result.Failure("HTML_NOT_ALLOWED", ErrorCodes.ValidationError);
                }
                // Key not in either dictionary -> Reject
                else
                {
                    return Result.Failure("UNSUPPORTED_PAYLOAD_FIELD", ErrorCodes.ValidationError);
                }
            }

            // Check if all required fields are present
            foreach (var req in schema.RequiredFields.Keys)
            {
                if (!foundRequired.Contains(req))
                {
                    return Result.Failure("MISSING_REQUIRED_FIELDS", ErrorCodes.ValidationError);
                }
            }
        }
        catch (JsonException)
        {
            return Result.Failure("INVALID_JSON", ErrorCodes.ValidationError);
        }

        return Result.Success();
    }

    private static bool HasHtml(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return HtmlRegex.IsMatch(text);
    }
}
