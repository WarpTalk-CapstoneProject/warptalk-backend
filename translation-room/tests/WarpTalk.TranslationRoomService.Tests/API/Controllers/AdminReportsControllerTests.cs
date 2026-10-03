using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.API.Controllers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.API.Controllers;

/// <summary>
/// AdminReportsController (WT-892): the Insights report's Word file in, a PDF out. The conversion
/// itself is Gotenberg's; what is pinned here is what this endpoint refuses to hand to it and what
/// it says when there is no converter.
/// </summary>
public class AdminReportsControllerTests
{
    private static readonly byte[] Docx = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00];
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7 fake");

    private readonly Mock<IDocumentPdfConverter> _converter = new();

    private AdminReportsController ControllerWithBody(byte[] body, long? declaredLength = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = declaredLength ?? body.Length;
        return new AdminReportsController(_converter.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }

    [Fact]
    public async Task ADocx_IsConverted_AndTheBytesComeBackAsAPdf()
    {
        _converter.SetupGet(c => c.IsConfigured).Returns(true);
        _converter.Setup(c => c.ToPdfAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Pdf);

        var result = await ControllerWithBody(Docx).DocxToPdf(CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("application/pdf");
        file.FileContents.Should().Equal(Pdf);
        _converter.Verify(c => c.ToPdfAsync(
            It.Is<byte[]>(bytes => bytes.SequenceEqual(Docx)), "report.docx", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WithoutAConverter_ItSays503_AndConvertsNothing()
    {
        _converter.SetupGet(c => c.IsConfigured).Returns(false);

        var result = await ControllerWithBody(Docx).DocxToPdf(CancellationToken.None);

        var response = result.Should().BeOfType<ObjectResult>().Subject;
        response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        response.Value.Should().BeOfType<ApiErrorResponse>().Which.Code.Should().Be(ErrorCodes.ServiceUnavailable);
        _converter.Verify(c => c.ToPdfAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AConversionThatFails_Is503_BecauseTheWordFileTheCallerHoldsIsStillGood()
    {
        _converter.SetupGet(c => c.IsConfigured).Returns(true);
        _converter.Setup(c => c.ToPdfAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);

        var result = await ControllerWithBody(Docx).DocxToPdf(CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D })] // a PDF is not a .docx
    [InlineData(new byte[] { 0x50, 0x4B })]                    // too short to be a zip
    public async Task ABodyThatIsNotADocx_IsRefused_WithoutTouchingTheConverter(byte[] body)
    {
        _converter.SetupGet(c => c.IsConfigured).Returns(true);

        var result = await ControllerWithBody(body).DocxToPdf(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        _converter.Verify(c => c.ToPdfAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ADeclaredLengthOverTheCap_IsRefused_BeforeTheBodyIsRead()
    {
        _converter.SetupGet(c => c.IsConfigured).Returns(true);

        var result = await ControllerWithBody(Docx, declaredLength: AdminReportsController.MaxDocxBytes + 1)
            .DocxToPdf(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        _converter.Verify(c => c.ToPdfAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ABodyOverTheCap_WithNoDeclaredLength_IsRefusedWhileReading()
    {
        _converter.SetupGet(c => c.IsConfigured).Returns(true);
        var big = new byte[AdminReportsController.MaxDocxBytes + 10];
        Docx.CopyTo(big, 0);
        var controller = ControllerWithBody(big);
        controller.Request.ContentLength = null; // chunked: nothing to check up front

        var result = await controller.DocxToPdf(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        _converter.Verify(c => c.ToPdfAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
