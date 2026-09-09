using System.IO;

namespace WarpTalk.AuthService.Application.DTOs;

/// <summary>An avatar as it comes back off storage: the bytes, and what they are.</summary>
public record ProfileAvatarContent(Stream Content, string ContentType);
