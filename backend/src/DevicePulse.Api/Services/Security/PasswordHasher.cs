using System.Security.Cryptography;
using DevicePulse.Api.Exceptions;
using DevicePulse.Api.Options;
using Microsoft.Extensions.Options;

namespace DevicePulse.Api.Services.Security;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string storedHash);

    /// <summary>Throws <see cref="ValidationException"/> if the password fails the server-side policy.</summary>
    void ValidatePolicy(string password);
}

/// <summary>
/// PBKDF2-HMAC-SHA256 with a per-password random salt.
///
/// Chosen over writing anything bespoke: the master reference is explicit that this project uses
/// established primitives rather than custom cryptography (§5). PBKDF2 ships in the framework
/// (<see cref="Rfc2898DeriveBytes"/>) so there is no third-party dependency, and the iteration
/// count and salt are embedded in the stored string so it can be raised later without
/// invalidating existing hashes.
///
/// Format: <c>{algorithm}.{iterations}.{base64 salt}.{base64 hash}</c>
/// </summary>
public sealed class PasswordHasher : IPasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 210_000; // OWASP guidance for PBKDF2-HMAC-SHA256.
    private const string Algorithm = "PBKDF2-SHA256";

    private readonly SecurityOptions _options;

    public PasswordHasher(IOptions<SecurityOptions> options) => _options = options.Value;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return $"{Algorithm}.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(storedHash))
            return false;

        var parts = storedHash.Split('.', 4);
        if (parts.Length != 4 || parts[0] != Algorithm)
            return false;

        if (!int.TryParse(parts[1], out var iterations))
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // Fixed-time comparison. A plain == would leak how many leading bytes matched,
        // which is enough to recover a hash byte by byte given enough attempts.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public void ValidatePolicy(string password)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(password) || password.Length < _options.PasswordMinLength)
            failures.Add($"must be at least {_options.PasswordMinLength} characters long");

        if (_options.PasswordRequireUppercase && !password.Any(char.IsUpper))
            failures.Add("must contain an uppercase letter");

        if (_options.PasswordRequireLowercase && !password.Any(char.IsLower))
            failures.Add("must contain a lowercase letter");

        if (_options.PasswordRequireDigit && !password.Any(char.IsDigit))
            failures.Add("must contain a digit");

        if (_options.PasswordRequireNonAlphanumeric && password.All(char.IsLetterOrDigit))
            failures.Add("must contain a non-alphanumeric character");

        if (failures.Count == 0)
            return;

        // Enforced here on the server regardless of what the Angular form checked
        // (Appendix D.3) — the client's validation is a convenience, not a control.
        throw new ValidationException(new Dictionary<string, string[]>
        {
            ["password"] = [$"Password {string.Join(", ", failures)}."]
        });
    }
}
