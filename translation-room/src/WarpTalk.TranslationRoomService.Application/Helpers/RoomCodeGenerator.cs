using System;
using System.Linq;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

public static class RoomCodeGenerator
{
    private const string Chars = "abcdefghijklmnopqrstuvwxyz";
    private static readonly Random Random = new Random();

    public static string GenerateCode()
    {
        // Generates a 12-character code in the format: xxx-yyyy-zzz
        // 10 alphanumeric characters + 2 hyphens = 12 characters
        var randomChars = new string(Enumerable.Repeat(Chars, 10)
            .Select(s => s[Random.Next(s.Length)]).ToArray());

        return $"{randomChars.Substring(0, 3)}-{randomChars.Substring(3, 4)}-{randomChars.Substring(7, 3)}";
    }
}
