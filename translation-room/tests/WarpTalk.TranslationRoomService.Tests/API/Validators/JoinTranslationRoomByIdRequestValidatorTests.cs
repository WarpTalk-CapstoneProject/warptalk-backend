using System.ComponentModel.DataAnnotations;
using System.Linq;
using WarpTalk.TranslationRoomService.API.Validators;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;
using Xunit;
using FluentValidation.TestHelper;

namespace WarpTalk.TranslationRoomService.Tests.API.Validators;

/// <summary>
/// WT-555. A shared meeting link produces a join with no room code, and the whole point of this
/// request type is that nothing asks it for one.
/// </summary>
public class JoinTranslationRoomByIdRequestValidatorTests
{
    private readonly JoinTranslationRoomByIdRequestValidator _validator = new();

    [Fact]
    public void A_Join_By_Id_Carries_No_Room_Code_And_Is_Still_Valid()
    {
        var result = _validator.TestValidate(new JoinTranslationRoomByIdRequest("Test User", "en", "vi"));

        result.ShouldNotHaveAnyValidationErrors();
    }

    /// <summary>
    /// The shipped bug was NOT in FluentValidation alone: `[Required] string TranslationRoomCode`
    /// on the shared record made model binding answer "The TranslationRoomCode field is required."
    /// before any validator ran. Asserting on the property set is what catches a future edit that
    /// re-adds the field.
    /// </summary>
    [Fact]
    public void The_Request_Type_Has_No_Room_Code_Property_At_All()
    {
        var names = typeof(JoinTranslationRoomByIdRequest)
            .GetProperties()
            .Select(p => p.Name)
            .ToArray();

        Assert.DoesNotContain(nameof(JoinTranslationRoomRequest.TranslationRoomCode), names);
    }

    [Fact]
    public void Display_Name_Is_Still_Required()
    {
        var result = _validator.TestValidate(new JoinTranslationRoomByIdRequest(null!, "en", "vi"));

        result.ShouldHaveValidationErrorFor(x => x.DisplayName)
            .WithErrorMessage(TranslationRoomConstants.ValidationDisplayNameRequired);
    }

    [Fact]
    public void Languages_Are_Still_Required()
    {
        var result = _validator.TestValidate(new JoinTranslationRoomByIdRequest("Test User", null, null));

        result.ShouldHaveValidationErrorFor(x => x.SpeakLanguage)
            .WithErrorMessage(TranslationRoomConstants.ValidationSpeakLanguageRequired);
        result.ShouldHaveValidationErrorFor(x => x.ListenLanguage)
            .WithErrorMessage(TranslationRoomConstants.ValidationListenLanguageRequired);
    }

    /// <summary>
    /// DataAnnotations run at model binding, ahead of FluentValidation, so a `[Required]` that
    /// crept back would 400 the request before any of the above applied.
    ///
    /// Read off the CONSTRUCTOR PARAMETERS, not the properties: on a positional record an
    /// undecorated attribute binds to the parameter, and that is the copy ASP.NET Core's model
    /// metadata reads. Asking the properties returns an empty list whatever the record says,
    /// which would make this test pass for the wrong reason.
    /// </summary>
    [Fact]
    public void Nothing_On_The_Record_Is_Required_Except_The_Display_Name()
    {
        var required = typeof(JoinTranslationRoomByIdRequest)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Where(p => p.GetCustomAttributes(typeof(RequiredAttribute), inherit: true).Length > 0)
            .Select(p => p.Name)
            .ToArray();

        Assert.Equal(new[] { "DisplayName" }, required);
    }
}
