using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

public class RoomCodeGeneratorTests
{
    [Fact]
    public void GenerateCode_ShouldReturnStringOfCorrectLength()
    {
        // Act
        var code = RoomCodeGenerator.GenerateCode();

        // Assert
        code.Should().NotBeNullOrEmpty();
        code.Should().HaveLength(12);
    }

    [Fact]
    public void GenerateCode_ShouldNotGenerateDuplicates()
    {
        // Arrange
        var count = 1000;
        var generatedCodes = new HashSet<string>();

        // Act
        for (int i = 0; i < count; i++)
        {
            generatedCodes.Add(RoomCodeGenerator.GenerateCode());
        }

        // Assert
        generatedCodes.Should().HaveCount(count);
    }
}
