using System;
using Xunit;
using WarpTalk.Gateway.Helpers;

namespace WarpTalk.Gateway.Tests.Helpers;

public class ProfanityFilterHelperTests
{
    [Theory]
    [InlineData("This is a test", "This is a test")]
    [InlineData("What the fuck is this", "What the *** is this")]
    [InlineData("Đụ má mày", "*** má mày")]
    [InlineData("Cái lồn gì vậy", "Cái *** gì vậy")]
    [InlineData("You are a bitch!", "You are a ***!")]
    [InlineData("Don't be an asshole", "Don't be an ***")]
    [InlineData("Shit happens", "*** happens")]
    [InlineData("fUcK", "***")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void MaskProfanity_ShouldMaskProfaneWords_WhenPresent(string input, string expected)
    {
        // Act
        var result = ProfanityFilterHelper.MaskProfanity(input);

        // Assert
        Assert.Equal(expected, result);
    }
}
