using System.ComponentModel.DataAnnotations;

namespace DevicePulse.Api.Models;

public sealed record LoginRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(256)] string Password);

public sealed record RegisterRequest(
    [Required, MaxLength(200)] string Name,
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(256)] string Password);

public sealed record RefreshRequest([Required] string RefreshToken);

public sealed record AuthResponse(
    string AccessToken,
    string RefreshToken,
    DateTime AccessTokenExpiresAt,
    CurrentUserResponse User);

/// <summary>
/// What the SPA needs to render permission-aware navigation. The permission list here is a
/// convenience for the UI only — the API re-checks every one of them on every request (§29).
/// </summary>
public sealed record CurrentUserResponse(
    int UserId,
    string Name,
    string Email,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);

public sealed record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required, MaxLength(256)] string NewPassword);
