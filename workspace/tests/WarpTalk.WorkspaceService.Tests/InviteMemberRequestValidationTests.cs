using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;
using WarpTalk.WorkspaceService.Domain.Constants;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// The invite form's DataAnnotations, exercised the way [ApiController] exercises them.
///
/// This service has no FluentValidation; its request rules live entirely in attributes on the DTO,
/// which means they are easy to drop in a refactor and nothing notices until a row fails to save.
/// [EmailAddress] on its own is the case in point: it accepts an address far longer than the
/// varchar(320) column behind it, so the only thing refusing one used to be Postgres.
/// </summary>
public class InviteMemberRequestValidationTests
{
    private static IReadOnlyList<ValidationResult> Validate(InviteMemberRequest request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    private static string AddressOfLength(int length)
    {
        const string domain = "@example.com";
        return new string('a', length - domain.Length) + domain;
    }

    [Fact]
    public void AnEmailPastTheColumnLength_IsRefused()
    {
        var request = new InviteMemberRequest(
            AddressOfLength(WorkspaceConstants.InvitationEmailMaxLength + 1), "Member");

        var results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(InviteMemberRequest.Email)));
    }

    [Fact]
    public void AnEmailExactlyOnTheColumnLength_IsAccepted()
    {
        var request = new InviteMemberRequest(
            AddressOfLength(WorkspaceConstants.InvitationEmailMaxLength), "Member");

        var results = Validate(request);

        Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(InviteMemberRequest.Email)));
    }

    [Fact]
    public void AnOrdinaryAddress_IsStillAccepted()
    {
        var results = Validate(new InviteMemberRequest("someone@example.com", "Member"));

        Assert.Empty(results);
    }

    [Fact]
    public void AMalformedAddress_IsStillRefused()
    {
        // The length rule must not have displaced the format rule.
        var results = Validate(new InviteMemberRequest("not-an-email", "Member"));

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(InviteMemberRequest.Email)));
    }
}
