using System;
using System.Collections.Generic;

namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>One member's credit spend in a workspace. WT-413.</summary>
public sealed record MemberCreditUsageDto(
    Guid UserId,
    int CreditsConsumed,
    int RecordCount,
    DateTime? LastUsedAt);

/// <summary>
/// Per-member credit spend for one workspace, plus the total.
///
/// The total is computed from the same rows as the breakdown rather than read from the
/// subscription: a workspace owner comparing "the sum of the table" against "the number above
/// the table" must never find them disagreeing, and the subscription balance answers a
/// different question (what is left, not what these people spent in this window).
/// </summary>
public sealed record WorkspaceUsageByMemberDto(
    Guid WorkspaceId,
    DateTime? From,
    DateTime? To,
    int TotalCreditsConsumed,
    IReadOnlyList<MemberCreditUsageDto> Members);
