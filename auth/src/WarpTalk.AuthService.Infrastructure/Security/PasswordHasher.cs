using System;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Domain.Settings;

namespace WarpTalk.AuthService.Infrastructure.Security;

public class PasswordHasher : IPasswordHasher
{
    private readonly PasswordHasherSettings _settings;

    public PasswordHasher(IOptions<PasswordHasherSettings> options)
    {
        _settings = options?.Value ?? new PasswordHasherSettings();
    }

    // Default constructor for testing fallback/convenience
    public PasswordHasher()
    {
        _settings = new PasswordHasherSettings();
    }

    private static HashAlgorithmName ParseAlgorithm(string name)
    {
        return name?.ToUpperInvariant() switch
        {
            "SHA1" => HashAlgorithmName.SHA1,
            "SHA256" => HashAlgorithmName.SHA256,
            "SHA384" => HashAlgorithmName.SHA384,
            "SHA512" => HashAlgorithmName.SHA512,
            "MD5" => HashAlgorithmName.MD5,
            _ => HashAlgorithmName.SHA512
        };
    }

    public string Hash(string password)
    {
        var algorithm = ParseAlgorithm(_settings.Algorithm);
        var salt = RandomNumberGenerator.GetBytes(_settings.SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, _settings.Iterations, algorithm, _settings.HashSize);

        // Format: versionPrefix$algorithm$iterations$saltSize$saltBase64$hashBase64
        return $"{_settings.VersionPrefix}${_settings.Algorithm}${_settings.Iterations}${_settings.SaltSize}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string passwordHash)
    {
        if (string.IsNullOrEmpty(passwordHash)) return false;

        // Check if using the new metadata-embedded format (using configured prefix or common "v2")
        if (passwordHash.StartsWith(_settings.VersionPrefix + "$") || passwordHash.StartsWith("v2$") || passwordHash.StartsWith("custom-v3$"))
        {
            var parts = passwordHash.Split('$');

            try
            {
                int iterations;
                int saltSize;
                string saltBase64;
                string hashBase64;
                HashAlgorithmName algorithm;

                if (parts.Length == 6)
                {
                    // 6-part format: prefix$algorithm$iterations$saltSize$saltBase64$hashBase64
                    algorithm = ParseAlgorithm(parts[1]);
                    if (!int.TryParse(parts[2], out iterations)) return false;
                    if (!int.TryParse(parts[3], out saltSize)) return false;
                    saltBase64 = parts[4];
                    hashBase64 = parts[5];
                }
                else if (parts.Length == 5)
                {
                    // 5-part legacy format: prefix$iterations$saltSize$saltBase64$hashBase64 (defaults to SHA512)
                    algorithm = HashAlgorithmName.SHA512;
                    if (!int.TryParse(parts[1], out iterations)) return false;
                    if (!int.TryParse(parts[2], out saltSize)) return false;
                    saltBase64 = parts[3];
                    hashBase64 = parts[4];
                }
                else
                {
                    return false;
                }

                var salt = Convert.FromBase64String(saltBase64);
                var hash = Convert.FromBase64String(hashBase64);

                var inputHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, algorithm, hash.Length);
                return CryptographicOperations.FixedTimeEquals(inputHash, hash);
            }
            catch
            {
                return false;
            }
        }

        // Backward compatibility: verify old format: {saltBase64}.{hashBase64}
        var oldParts = passwordHash.Split('.');
        if (oldParts.Length == 2)
        {
            try
            {
                var salt = Convert.FromBase64String(oldParts[0]);
                var hash = Convert.FromBase64String(oldParts[1]);

                // Old default parameters: 100k iterations, SHA512, 32-byte hash
                var inputHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA512, 32);
                return CryptographicOperations.FixedTimeEquals(inputHash, hash);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }
}


