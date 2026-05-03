using System.Security.Cryptography;
using System.Text;

namespace WarpTalk.BillingService.API.Security;

/// <summary>
/// Helper class for webhook signature validation using HMAC-SHA256.
/// </summary>
public static class WebhookSignatureValidator
{
    /// <summary>
    /// Validates a PayOS webhook signature using HMAC-SHA256.
    /// </summary>
    public static bool ValidatePayOsSignature(string payload, string signature, string checksumKey)
    {
        if (string.IsNullOrEmpty(payload) || string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(checksumKey))
            return false;

        try
        {
            var key = Encoding.UTF8.GetBytes(checksumKey);
            var hash = new HMACSHA256(key);
            var computedSignature = Convert.ToHexString(hash.ComputeHash(Encoding.UTF8.GetBytes(payload)));

            // Constant-time comparison to prevent timing attacks
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(computedSignature));
        }
        catch
        {
            return false;
        }
    }
}
