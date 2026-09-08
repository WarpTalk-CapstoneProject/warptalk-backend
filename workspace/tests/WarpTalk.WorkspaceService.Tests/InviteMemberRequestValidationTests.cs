using System.ComponentModel.DataAnnotations;
using System.Linq;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;
using WarpTalk.WorkspaceService.Domain.Constants;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// The invite request's rules, read off the constructor parameters where ASP.NET reads them.
///
/// WHY REFLECTION AND NOT Validator.TryValidateObject
///     This service has no FluentValidation; the invite rules live entirely in DataAnnotations on
///     a positional record, which means they sit on the CONSTRUCTOR PARAMETERS. TryValidateObject
///     reflects over properties, so it sees none of them and reports a malformed address as valid.
///
///     The obvious repair — move them with [property:] so both paths see them — is the one thing
///     that must not be done. ASP.NET refuses that arrangement outright:
///
///       InvalidOperationException: Record type 'InviteMemberRequest' has validation metadata
///       defined on property 'RoleName' that will be ignored. 'RoleName' is a parameter in the
///       record primary constructor and validation metadata must be associated with the
///       constructor parameter.
///
///     It is thrown during model binding, so every invite becomes a 500 and nothing is validated
///     at all. That was tried, and it took calling the endpoint to find out; the build was clean
///     and the unit tests were green.
///
/// WHAT THIS DOES AND DOES NOT PROVE
///     It proves the rules are declared, and declared in the place ASP.NET honours — which is what
///     a refactor would quietly break. It does not exercise the binding pipeline; the end-to-end
///     behaviour is covered by calling POST /workspaces/{id}/invitations.
/// </summary>
public class InviteMemberRequestValidationTests
{
    private static T? ParameterAttribute<T>(string parameterName) where T : ValidationAttribute
    {
        var parameter = typeof(InviteMemberRequest)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Single(p => p.Name == parameterName);

        return parameter.GetCustomAttributes(typeof(T), inherit: false).Cast<T>().SingleOrDefault();
    }

    [Fact]
    public void Email_IsBoundedByTheColumnThatHasToHoldIt()
    {
        var rule = ParameterAttribute<StringLengthAttribute>("Email");

        Assert.NotNull(rule);
        Assert.Equal(WorkspaceConstants.InvitationEmailMaxLength, rule!.MaximumLength);
    }

    [Fact]
    public void Email_StillHasItsFormatAndPresenceRules()
    {
        // The length rule must be an addition, not a replacement.
        Assert.NotNull(ParameterAttribute<EmailAddressAttribute>("Email"));
        Assert.NotNull(ParameterAttribute<RequiredAttribute>("Email"));
    }

    [Fact]
    public void TheRules_AreOnTheConstructorParameters_NotOnTheProperties()
    {
        // The arrangement ASP.NET throws on. If somebody "tidies" these onto the properties, model
        // binding raises InvalidOperationException and every invite returns 500 — so this asserts
        // the properties carry NO validation metadata.
        foreach (var name in new[] { "Email", "RoleName" })
        {
            var property = typeof(InviteMemberRequest).GetProperty(name);
            Assert.NotNull(property);

            var onProperty = property!.GetCustomAttributes(typeof(ValidationAttribute), inherit: false);
            Assert.True(
                onProperty.Length == 0,
                $"{name} carries validation metadata on the property. ASP.NET refuses that on a record and every request becomes a 500 — keep it on the constructor parameter.");
        }
    }
}
