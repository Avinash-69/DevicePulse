using System.Security.Cryptography;
using System.Text;

namespace DevicePulse.Api.Services.Security;

public interface IDeviceApiKeyService
{
    /// <summary>Generates a new key, returning the plaintext to show once and the hash to store.</summary>
    (string PlainTextKey, string Hash) Generate(string deviceCode);

    string ComputeHash(string plainTextKey);
}

/// <summary>
/// Device credentials are intentionally a different mechanism from human authentication
/// (Appendix C item 3): devices get a long random API key, humans get a JWT from a login flow.
/// They never share a token type or an endpoint.
///
/// The key is hashed with plain SHA-256 rather than PBKDF2 — unlike a human password it is
/// high-entropy random (256 bits), so there is nothing to brute-force, and ingestion verifies
/// it on every single reading where a slow KDF would be a real throughput cost.
/// </summary>
public sealed class DeviceApiKeyService : IDeviceApiKeyService
{
    private const string Prefix = "dpk";

    public (string PlainTextKey, string Hash) Generate(string deviceCode)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        // The device code is embedded so a key found in a log or a config file can be traced
        // back to its device without having to reverse the hash.
        var key = $"{Prefix}_{deviceCode}_{secret}";
        return (key, ComputeHash(key));
    }

    public string ComputeHash(string plainTextKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainTextKey)));
}
