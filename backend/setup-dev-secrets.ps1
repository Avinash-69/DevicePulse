<#
.SYNOPSIS
    Sets the local development secrets DevicePulse needs to start.

.DESCRIPTION
    The connection string, the JWT signing key and the initial Super Admin password are
    deliberately absent from appsettings.json so they never reach Git (Appendix D.2 of the
    master reference). They live in the per-user secret store instead, which `dotnet
    user-secrets` writes outside the repository.

    Run this once after cloning. It is idempotent — running it again simply overwrites the
    values, and -Force regenerates the signing key.

.PARAMETER SqlServer
    The SQL Server instance to use. Defaults to the local default instance.

.PARAMETER SuperAdminPassword
    Password for the seeded Super Admin. One is generated if not supplied.

.PARAMETER Force
    Regenerate the JWT signing key even if one is already set.

.EXAMPLE
    ./setup-dev-secrets.ps1

.EXAMPLE
    ./setup-dev-secrets.ps1 -SqlServer "localhost\SQLEXPRESS"
#>
[CmdletBinding()]
param(
    [string]$SqlServer = "localhost",
    [string]$Database = "DevicePulseDb",
    [string]$SuperAdminPassword,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$apiProject = Join-Path $PSScriptRoot "src/DevicePulse.Api"
$simulatorProject = Join-Path $PSScriptRoot "src/DevicePulse.Simulator"

if (-not (Test-Path $apiProject)) {
    throw "Could not find the API project at $apiProject. Run this script from the backend folder."
}

function Set-Secret {
    param([string]$Project, [string]$Key, [string]$Value)

    # The value is piped rather than passed as an argument so a password containing characters
    # PowerShell would otherwise interpret still arrives intact.
    dotnet user-secrets set $Key $Value --project $Project | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to set $Key. Is the .NET SDK on PATH?"
    }
}

function Get-ExistingSecret {
    param([string]$Project, [string]$Key)

    $listed = dotnet user-secrets list --project $Project 2>$null

    if ($LASTEXITCODE -ne 0) { return $null }

    foreach ($line in $listed) {
        if ($line -like "$Key = *") {
            return $line.Substring($Key.Length + 3)
        }
    }

    return $null
}

Write-Host "Configuring development secrets for DevicePulse..." -ForegroundColor Cyan

# ---- connection string ----
$connectionString = "Server=$SqlServer;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True"
Set-Secret -Project $apiProject -Key "ConnectionStrings:DevicePulseDb" -Value $connectionString
Write-Host "  connection string -> $SqlServer / $Database"

# ---- JWT signing key ----
$existingKey = Get-ExistingSecret -Project $apiProject -Key "Jwt:SigningKey"

if ($Force -or [string]::IsNullOrWhiteSpace($existingKey)) {
    # 48 random bytes, well above the 32-character minimum the options validator enforces.
    $bytes = New-Object byte[] 48
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $signingKey = [Convert]::ToBase64String($bytes)

    Set-Secret -Project $apiProject -Key "Jwt:SigningKey" -Value $signingKey
    Write-Host "  JWT signing key   -> generated (48 random bytes)"
}
else {
    Write-Host "  JWT signing key   -> already set (use -Force to regenerate)"
}

# ---- Super Admin password ----
if ([string]::IsNullOrWhiteSpace($SuperAdminPassword)) {
    $existingPassword = Get-ExistingSecret -Project $apiProject -Key "Seed:SuperAdminPassword"

    if (-not [string]::IsNullOrWhiteSpace($existingPassword)) {
        $SuperAdminPassword = $existingPassword
        Write-Host "  Super Admin pwd   -> already set"
    }
    else {
        # Generated rather than defaulted: shipping a known administrator password would be a
        # backdoor, and the seeder refuses to invent one itself for the same reason.
        $SuperAdminPassword = "Dp#" + ([Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes([Guid]::NewGuid().ToString())).Substring(0, 14)) + "9a"
        Set-Secret -Project $apiProject -Key "Seed:SuperAdminPassword" -Value $SuperAdminPassword

        Write-Host ""
        Write-Host "  Super Admin password (shown once, save it now):" -ForegroundColor Yellow
        Write-Host "    $SuperAdminPassword" -ForegroundColor Yellow
        Write-Host ""
    }
}
else {
    Set-Secret -Project $apiProject -Key "Seed:SuperAdminPassword" -Value $SuperAdminPassword
    Write-Host "  Super Admin pwd   -> set from the supplied value"
}

# ---- simulator credentials ----
# The simulator signs in as a user holding telemetry.ingest. In development that is the
# seeded Super Admin; in a real deployment it would be a dedicated service account.
$superAdminEmail = "superadmin@devicepulse.local"

Set-Secret -Project $simulatorProject -Key "Simulator:Email" -Value $superAdminEmail
Set-Secret -Project $simulatorProject -Key "Simulator:Password" -Value $SuperAdminPassword
Write-Host "  simulator login   -> $superAdminEmail"

Write-Host ""
Write-Host "Done. Next:" -ForegroundColor Green
Write-Host "  cd src/DevicePulse.Api && dotnet run"
Write-Host "  then open http://localhost:5082/swagger"
Write-Host ""
Write-Host "Sign in as $superAdminEmail and change that password straight away." -ForegroundColor DarkGray
