using System.Security.Cryptography;

namespace WarpTalk.WorkspaceService.Application.Helpers;

public static class WorkspaceInvitationTokenGenerator
{
    public static string Generate()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }
}
