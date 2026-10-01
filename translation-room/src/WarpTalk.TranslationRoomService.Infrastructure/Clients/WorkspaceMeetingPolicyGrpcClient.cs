using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Clients;

public sealed class WorkspaceMeetingPolicyGrpcClient : IWorkspaceMeetingPolicy
{
    private const string UnavailableMessage =
        "Could not verify your permission to create meetings. Please try again in a moment.";

    private const string SuspendedMessage =
        "This workspace is suspended. Contact your administrator to restore it.";

    private readonly WorkspaceService.WorkspaceServiceClient _client;
    private readonly ILogger<WorkspaceMeetingPolicyGrpcClient> _logger;

    public WorkspaceMeetingPolicyGrpcClient(
        WorkspaceService.WorkspaceServiceClient client,
        ILogger<WorkspaceMeetingPolicyGrpcClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<Result> ValidateMeetingCreationAsync(
        Guid workspaceId,
        Guid userId,
        IEnumerable<string> targetLanguages,
        string? sourceLanguage = null,
        CancellationToken ct = default)
    {
        var request = new ValidateMeetingCreationRequest
        {
            WorkspaceId = workspaceId.ToString(),
            UserId = userId.ToString(),
            // proto3 has no null string: an unstated source language must go on the wire as "",
            // which the workspace side reads as "not stated" rather than as a violation.
            SourceLanguage = sourceLanguage ?? string.Empty
        };
        request.TargetLanguages.AddRange(targetLanguages ?? Enumerable.Empty<string>());

        ValidateMeetingCreationResponse response;
        try
        {
            response = await _client.ValidateMeetingCreationAsync(request, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Fails closed on purpose: this call IS the permission gate, so letting an outage
            // through would reopen WT-249. Surfaced as Unavailable rather than Forbidden so the
            // caller can tell "you may not" apart from "we could not check".
            _logger.LogError(
                ex,
                "Meeting-creation policy check failed. WorkspaceId: {WorkspaceId}, UserId: {UserId}",
                workspaceId,
                userId);
            return Result.Failure(UnavailableMessage, ErrorCodes.ServiceUnavailable);
        }

        return response.IsAllowed
            ? Result.Success()
            : Result.Failure(response.ErrorMessage, ErrorCodes.Forbidden);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<string>>> GetAllowedLanguagesAsync(
        Guid workspaceId,
        CancellationToken ct = default)
    {
        var settings = await FetchSettingsAsync(workspaceId, ct);
        return settings.IsSuccess
            ? Result.Success<IReadOnlyList<string>>(settings.Value!.AllowedTargetLanguages.ToArray())
            : Result.Failure<IReadOnlyList<string>>(settings.Error ?? UnavailableMessage, settings.ErrorCode);
    }

    /// <inheritdoc />
    public async Task<Result> ValidateRoomLanguagesAsync(
        Guid workspaceId,
        string? sourceLanguage,
        IEnumerable<string> targetLanguages,
        CancellationToken ct = default)
    {
        var fetched = await FetchSettingsAsync(workspaceId, ct);
        if (!fetched.IsSuccess)
        {
            // The lookup failing is not the same as the languages being refused, but this path
            // fails CLOSED for the reason given on the interface: it is the enforcement of a rule
            // an owner set deliberately.
            return Result.Failure(fetched.Error ?? UnavailableMessage, fetched.ErrorCode);
        }

        var settings = fetched.Value!;
        var targets = (targetLanguages ?? Enumerable.Empty<string>()).ToList();

        // EMPTY MEANS UNRESTRICTED. A workspace that never set a policy allows everything the
        // platform supports, and reading empty the other way would refuse every edit in every such
        // workspace — which is most of them. The plan quota below still applies either way.
        if (settings.AllowedTargetLanguages.Count > 0)
        {
            // Both sides normalized: rooms store primary subtags ("vi") while a workspace may have
            // stored a regional code ("vi-VN"); a raw comparison refused languages the owner allowed.
            var allowed = new HashSet<string>(
                settings.AllowedTargetLanguages
                    .Select(LanguageHelper.NormalizeLanguageCode)
                    .Where(code => code.Length > 0),
                StringComparer.Ordinal);

            if (!string.IsNullOrWhiteSpace(sourceLanguage)
                && !allowed.Contains(LanguageHelper.NormalizeLanguageCode(sourceLanguage)))
            {
                return Result.Failure(
                    $"Source language '{sourceLanguage}' is not allowed by the workspace policy.",
                    ErrorCodes.ValidationError);
            }

            foreach (var language in targets)
            {
                if (!string.IsNullOrWhiteSpace(language)
                    && !allowed.Contains(LanguageHelper.NormalizeLanguageCode(language)))
                {
                    return Result.Failure(
                        $"Target language '{language}' is not allowed by the workspace policy.",
                        ErrorCodes.ValidationError);
                }
            }
        }

        // Plan quota (WT-707): counted on DISTINCT normalized codes, so "en" and "en-US" are one
        // language. 0 means the plan sets no limit.
        var max = settings.MaxLanguages;
        if (max > 0)
        {
            var distinct = targets
                .Select(LanguageHelper.NormalizeLanguageCode)
                .Where(code => code.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Count();

            if (distinct > max)
            {
                return Result.Failure(
                    $"Your plan allows {max} target language(s) per meeting; {distinct} were requested.",
                    ErrorCodes.ValidationError);
            }
        }

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> EnsureWorkspaceCanHostMeetingsAsync(
        Guid workspaceId,
        CancellationToken ct = default)
    {
        GetWorkspacePreflightResponse response;
        try
        {
            // UserEmail deliberately left empty: it only drives the verified-domain lookup on the
            // workspace side, which this caller has no use for and should not pay for on a path
            // that runs on every join.
            response = await _client.GetWorkspacePreflightDetailsAsync(
                new GetWorkspacePreflightRequest { WorkspaceId = workspaceId.ToString() },
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Fails OPEN, unlike ValidateMeetingCreationAsync above — see the interface docs. Join
            // and start carried no WorkspaceService dependency before this check existed, and a
            // WorkspaceService outage must not become "no meeting in the product can be entered".
            _logger.LogWarning(
                ex,
                "Workspace lifecycle check failed; allowing the request through. WorkspaceId: {WorkspaceId}",
                workspaceId);
            return Result.Success();
        }

        return response.IsActive
            ? Result.Success()
            : Result.Failure(SuspendedMessage, ErrorCodes.Forbidden);
    }

    /// <inheritdoc />
    public async Task<bool?> HasActiveSubscriptionAsync(Guid workspaceId, CancellationToken ct = default)
    {
        // FetchSettingsAsync already logs a failure; unknown is the answer then (see the interface).
        var fetched = await FetchSettingsAsync(workspaceId, ct);
        if (!fetched.IsSuccess || !fetched.Value!.SubscriptionKnown)
        {
            return null;
        }

        return fetched.Value.HasActiveSubscription;
    }

    private async Task<Result<GetWorkspaceSettingsResponse>> FetchSettingsAsync(
        Guid workspaceId,
        CancellationToken ct)
    {
        try
        {
            var settings = await _client.GetWorkspaceSettingsAsync(
                new GetWorkspaceSettingsRequest { WorkspaceId = workspaceId.ToString() },
                cancellationToken: ct);

            return Result.Success(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Workspace language-policy lookup failed. WorkspaceId: {WorkspaceId}",
                workspaceId);
            return Result.Failure<GetWorkspaceSettingsResponse>(UnavailableMessage, ErrorCodes.ServiceUnavailable);
        }
    }
}
