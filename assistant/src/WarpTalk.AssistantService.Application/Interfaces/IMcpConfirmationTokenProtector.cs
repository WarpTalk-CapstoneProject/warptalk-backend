using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IMcpConfirmationTokenProtector
{
    string Protect(McpConfirmationTokenPayloadDto payload);

    Result<McpConfirmationTokenPayloadDto> Unprotect(string token);
}
