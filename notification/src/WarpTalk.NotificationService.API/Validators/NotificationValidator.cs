using System.Text.Json;
using System.Text.RegularExpressions;
using WarpTalk.Shared;
using WarpTalk.NotificationService.Domain.Constants;

namespace WarpTalk.NotificationService.API.Middlewares;

public static class NotificationValidator
{
    // Matches specific known HTML tags to distinguish from normal text (e.g., '1 < 2')
    private static readonly Regex HtmlRegex = new Regex(
        @"<\/?\s*(?:script|iframe|object|embed|svg|img|base|a|div|span|p|b|i|strong|em|h[1-6]|ul|ol|li|table|tr|td|th|tbody|thead|tfoot|style|link|meta|head|title|body|html|br|hr)(?:\s+[^>]*)?>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private class PayloadSchema
    {
        public Dictionary<string, JsonValueKind> RequiredFields { get; set; } = new();
        public Dictionary<string, JsonValueKind> OptionalFields { get; set; } = new();
    }

    private static readonly Dictionary<string, PayloadSchema> Schemas = new(StringComparer.OrdinalIgnoreCase)
    {
        {
            NotificationConstants.DefaultNotificationType, new PayloadSchema
            {
            }
        },
        {
            NotificationConstants.TypeMeetingInvite, new PayloadSchema
            {
                RequiredFields = { { "meeting_id", JsonValueKind.String }, { "inviter_name", JsonValueKind.String } }
            }
        },
        {
            // room_id + room_title are exactly what TranslationRoomService.NotifyInvitedUserAsync
            // puts in Metadata. Same shape as MEETING_STARTED on purpose: the web client reads an
            // invitation and a live-meeting notice through the same payload reader, and an invite
            // whose room cannot be identified has no Accept button to offer.
            NotificationConstants.TypeMeetingInvited, new PayloadSchema
            {
                RequiredFields = { { "room_id", JsonValueKind.String }, { "room_title", JsonValueKind.String } }
            }
        },
        {
            NotificationConstants.TypeMeetingReminder, new PayloadSchema
            {
                RequiredFields = { { "room_id", JsonValueKind.String }, { "room_title", JsonValueKind.String }, { "minutes_until_start", JsonValueKind.String } }
            }
        },
        {
            // room_id + room_title are exactly what TranslationRoomService.NotifyInviteesAsync
            // and ArtifactsFinalizationWorker put in Metadata. Registered as REQUIRED because a
            // notification about a meeting with no meeting on it is not worth delivering.
            NotificationConstants.TypeMeetingStarted, new PayloadSchema
            {
                RequiredFields = { { "room_id", JsonValueKind.String }, { "room_title", JsonValueKind.String } }
            }
        },
        {
            NotificationConstants.TypeMeetingSummaryReady, new PayloadSchema
            {
                RequiredFields = { { "room_id", JsonValueKind.String }, { "room_title", JsonValueKind.String } }
            }
        },
        {
            // Every field the producer sends is declared. An UNDECLARED field does not get
            // ignored — it rejects the whole payload with UNSUPPORTED_PAYLOAD_FIELD.
            NotificationConstants.TypeActionItemAssigned, new PayloadSchema
            {
                RequiredFields =
                {
                    { "room_id", JsonValueKind.String },
                    { "room_title", JsonValueKind.String },
                    { "action_item_id", JsonValueKind.String },
                    { "task", JsonValueKind.String }
                }
            }
        },
        {
            "TRANSCRIPT_READY", new PayloadSchema
            {
                RequiredFields = { { "transcript_id", JsonValueKind.String }, { "meeting_name", JsonValueKind.String } }
            }
        },
        {
            "MESSAGE", new PayloadSchema
            {
                RequiredFields = { { "sender_id", JsonValueKind.String }, { "sender_name", JsonValueKind.String }, { "room_id", JsonValueKind.String } }
            }
        },
        {
            // Producer: WorkspaceMemberService.NotifyMemberRoleChangedAsync, which has been
            // sending exactly these three fields against no schema at all — so every role change
            // fell through to the unknown-type branch below and was rejected.
            NotificationConstants.TypeWorkspaceRoleChanged, new PayloadSchema
            {
                RequiredFields =
                {
                    { "workspace_id", JsonValueKind.String },
                    { "old_role", JsonValueKind.String },
                    { "new_role", JsonValueKind.String }
                }
            }
        },
        {
            // WT-454. The reason is required, not optional: a suspension notice that does not say
            // why is the same dead end as no notice, and the admin endpoint already refuses to
            // suspend without one.
            NotificationConstants.TypeWorkspaceSuspended, new PayloadSchema
            {
                RequiredFields =
                {
                    { "workspace_id", JsonValueKind.String },
                    { "workspace_name", JsonValueKind.String },
                    { "reason", JsonValueKind.String }
                }
            }
        },
        {
            NotificationConstants.TypeWorkspaceReactivated, new PayloadSchema
            {
                RequiredFields =
                {
                    { "workspace_id", JsonValueKind.String },
                    { "workspace_name", JsonValueKind.String }
                }
            }
        },
        {
            // WT-521, to every Owner and Admin. `requester_id` is required because the Requests
            // tab has to be able to find the row this notification is about; a notice that only
            // says somebody wants to leave sends the reader hunting.
            NotificationConstants.TypeWorkspaceLeaveRequested, new PayloadSchema
            {
                RequiredFields =
                {
                    { "workspace_id", JsonValueKind.String },
                    { "workspace_name", JsonValueKind.String },
                    { "requester_id", JsonValueKind.String }
                }
            }
        },
        {
            // WT-521, back to the member who asked. Both outcomes carry the same shape, because
            // the member needs the same two facts either way: which workspace, and what happened.
            NotificationConstants.TypeWorkspaceLeaveApproved, new PayloadSchema
            {
                RequiredFields =
                {
                    { "workspace_id", JsonValueKind.String },
                    { "workspace_name", JsonValueKind.String }
                }
            }
        },
        {
            NotificationConstants.TypeWorkspaceLeaveRejected, new PayloadSchema
            {
                RequiredFields =
                {
                    { "workspace_id", JsonValueKind.String },
                    { "workspace_name", JsonValueKind.String }
                }
            }
        }
    };

    public static Result Validate(string type, string title, string content, string? actionUrl, string? payloadJson)
    {
        // Check Title and Content for HTML
        if (HasHtml(title) || HasHtml(content) || HasHtml(actionUrl))
        {
            return Result.Failure(NotificationConstants.ErrorHtmlNotAllowed, ErrorCodes.ValidationError);
        }

        // Validate Payload
        if (string.IsNullOrWhiteSpace(payloadJson) || payloadJson == "{}")
        {
            // Empty payload might be fine for some types if no required fields exist
            if (Schemas.TryGetValue(type, out var schemaCheck) && schemaCheck.RequiredFields.Any())
            {
                return Result.Failure(NotificationConstants.ErrorMissingRequiredFields, ErrorCodes.ValidationError);
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
                        return Result.Failure(NotificationConstants.ErrorInvalidFieldType, ErrorCodes.ValidationError);

                    // Check for HTML in string values
                    if (valueKind == JsonValueKind.String && HasHtml(prop.Value.GetString()!))
                        return Result.Failure(NotificationConstants.ErrorHtmlNotAllowed, ErrorCodes.ValidationError);

                    foundRequired.Add(key);
                }
                // Check against Optional
                else if (schema.OptionalFields.TryGetValue(key, out var expectedKindOptional))
                {
                    if (valueKind != expectedKindOptional && valueKind != JsonValueKind.Null)
                        return Result.Failure(NotificationConstants.ErrorInvalidFieldType, ErrorCodes.ValidationError);

                    // Check for HTML in string values
                    if (valueKind == JsonValueKind.String && HasHtml(prop.Value.GetString()!))
                        return Result.Failure(NotificationConstants.ErrorHtmlNotAllowed, ErrorCodes.ValidationError);
                }
                // Key not in either dictionary -> Reject
                else
                {
                    return Result.Failure(NotificationConstants.ErrorUnsupportedPayloadField, ErrorCodes.ValidationError);
                }
            }

            // Check if all required fields are present
            foreach (var req in schema.RequiredFields.Keys)
            {
                if (!foundRequired.Contains(req))
                {
                    return Result.Failure(NotificationConstants.ErrorMissingRequiredFields, ErrorCodes.ValidationError);
                }
            }
        }
        catch (JsonException)
        {
            return Result.Failure("INVALID_JSON", ErrorCodes.ValidationError);
        }

        return Result.Success();
    }

    private static bool HasHtml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return HtmlRegex.IsMatch(text);
    }
}
