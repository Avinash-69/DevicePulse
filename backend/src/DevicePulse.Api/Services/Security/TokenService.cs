using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DevicePulse.Api.Authorization;
using DevicePulse.Api.Entities;
using DevicePulse.Api.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DevicePulse.Api.Services.Security;

public interface ITokenService
{
    /// <summary>
    /// Issues an access token carrying the user's flattened permission set, so the
    /// authorization handler can decide without a database round trip on every request.
    /// </summary>
    (string Token, DateTime ExpiresAt) CreateAccessToken(User user, IEnumerable<string> roles, IEnumerable<string> permissions);

    /// <summary>Returns the plaintext refresh token to hand to the client and the hash to persist.</summary>
    (string Token, string Hash, DateTime ExpiresAt) CreateRefreshToken();

    string HashRefreshToken(string token);
}

/// <summary>
/// Issues DevicePulse's own tokens (Phase 1 of §5).
///
/// When identity moves to Keycloak in Phase 2, this class is what gets replaced: the API keeps
/// validating a bearer token and keeps enforcing its own permission claims, but stops being the
/// thing that mints them. Keeping issuance behind this one interface is what makes that swap a
/// contained change rather than a rewrite.
/// </summary>
public sealed class TokenService : ITokenService
{
    private readonly JwtOptions _options;

    public TokenService(IOptions<JwtOptions> options) => _options = options.Value;

    public (string Token, DateTime ExpiresAt) CreateAccessToken(
        User user, IEnumerable<string> roles, IEnumerable<string> permissions)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.UserId.ToString()),
            new(AppClaimTypes.UserId, user.UserId.ToString()),
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        // Roles are included for readability and for any future role-based check, but
        // authorization itself keys off the permission claims (§11, Appendix A.3).
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(permissions.Distinct().Select(p => new Claim(AppClaimTypes.Permission, p)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public (string Token, string Hash, DateTime ExpiresAt) CreateRefreshToken()
    {
        // Opaque random bytes, not a JWT: a refresh token carries no claims and is only ever
        // looked up by its hash, so there is nothing to sign and nothing to read from it.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

        return (token, HashRefreshToken(token), DateTime.UtcNow.AddDays(_options.RefreshTokenDays));
    }

    /// <summary>
    /// SHA-256, not a password KDF: the token is 512 bits of randomness, so there is no
    /// low-entropy guess to slow down, and refresh happens often enough that a slow hash
    /// would be a pointless cost.
    /// </summary>
    public string HashRefreshToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>
/// Claim names used in issued tokens. Spelled out rather than taken from
/// JwtRegisteredClaimNames so the dependency on the JWT package stays in this one file.
/// </summary>
internal static class JwtRegisteredClaimNames
{
    public const string Sub = "sub";
    public const string Jti = "jti";
}
