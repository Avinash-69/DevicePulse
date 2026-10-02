using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DevicePulse.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DevicePulse.Tests.Integration;

/// <summary>
/// Boots the real application against a throwaway SQL Server database.
///
/// Deliberately a real database rather than the in-memory provider: this suite exists to test
/// the things the in-memory provider cannot model — the unique indexes that make the
/// check-then-act races safe, the filtered index behind telemetry idempotency, RowVersion
/// concurrency tokens, and the actual SQL translation of the dashboard's grouped queries.
/// Testing those against a fake would prove nothing about production behaviour.
///
/// Each run gets its own database, dropped at the end, so runs cannot interfere with each other
/// or with the developer's own DevicePulseDb.
/// </summary>
public sealed class DevicePulseApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"DevicePulseTest_{Guid.NewGuid():N}";

    public string ConnectionString =>
        $"Server=localhost;Database={_databaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";

    public const string SuperAdminEmail = "it-superadmin@devicepulse.test";
    public const string SuperAdminPassword = "IntegrationTest#2026";

    /// <summary>
    /// Mirrors the API's own serializer configuration. The string-enum converter is essential:
    /// the API sends enums as names, so without it every response containing a status or a
    /// severity fails to deserialise here.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>True when a local SQL Server was reachable, so tests can skip rather than fail.</summary>
    public bool DatabaseAvailable { get; private set; }

    public string? SkipReason { get; private set; }

    /// <summary>
    /// Overrides are applied as environment variables, set in the constructor.
    ///
    /// This is not a stylistic choice. Program.cs reads the connection string and the security
    /// options from builder.Configuration while the builder is still being constructed, which
    /// happens before WebApplicationFactory's ConfigureAppConfiguration sources are layered on.
    /// Supplying them through AddInMemoryCollection alone silently lost to the developer's own
    /// user-secrets — and the first version of this factory did exactly that, so the suite ran
    /// against the real DevicePulseDb instead of a throwaway database.
    ///
    /// Environment variables sit above user-secrets in the default provider order, and the
    /// constructor runs before the host is ever built, so these win in every case.
    /// </summary>
    public DevicePulseApiFactory()
    {
        foreach (var (key, value) in Overrides)
            Environment.SetEnvironmentVariable(key.Replace(":", "__"), value);
    }

    private Dictionary<string, string?> Overrides => new()
    {
        ["ConnectionStrings:DevicePulseDb"] = ConnectionString,
        ["Jwt:Issuer"] = "DevicePulse.Tests",
        ["Jwt:Audience"] = "DevicePulse.Tests",
        ["Jwt:SigningKey"] = "integration-test-signing-key-at-least-32-characters-long",
        ["Jwt:AccessTokenMinutes"] = "30",
        ["Jwt:RefreshTokenDays"] = "1",
        ["Database:ApplyMigrationsOnStartup"] = "true",
        ["Database:SeedOnStartup"] = "true",
        ["Seed:SuperAdminEmail"] = SuperAdminEmail,
        ["Seed:SuperAdminName"] = "Integration Super Admin",
        ["Seed:SuperAdminPassword"] = SuperAdminPassword,

        // Off: these tests create exactly the devices they need, so a surprise seeded fleet
        // would make the dashboard assertions depend on sample data.
        ["Seed:SeedSampleData"] = "false",
        ["Security:AllowedCorsOrigins:0"] = "http://localhost:4200",

        // The suite signs in far more often per minute than any human, so the production auth
        // limit would reject tests for the wrong reason. The limiter still has its own test,
        // which sets a deliberately low limit.
        ["Security:AuthRequestsPerMinute"] = "100000",
        ["Security:IngestionRequestsPerMinute"] = "1000000"
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        // Belt and braces: also layered into configuration for anything read after Build().
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(Overrides));

        builder.ConfigureServices(services =>
        {
            // The background workers are not wanted here: an offline sweep firing mid-test
            // would change device connectivity underneath an assertion. Their logic is covered
            // by dedicated tests instead.
            foreach (var descriptor in services
                         .Where(s => s.ServiceType == typeof(IHostedService))
                         .ToList())
            {
                services.Remove(descriptor);
            }
        });
    }

    public async ValueTask InitializeAsync()
    {
        try
        {
            await using var connection = new SqlConnection(MasterConnection);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{_databaseName}]";
            await command.ExecuteNonQueryAsync();

            DatabaseAvailable = true;
        }
        catch (Exception ex)
        {
            // Skipped, not failed. A developer without a local SQL Server should still be able
            // to run the unit tests, and CI provides the server through a service container.
            DatabaseAvailable = false;
            SkipReason = $"A local SQL Server was not reachable, so the integration tests were skipped: {ex.Message}";
            return;
        }

        // Forces the host to build and run migrations plus seeding now, so a failure here is
        // reported as setup rather than as a mysterious failure in the first test.
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DevicePulseDbContext>();

        // Hard stop if the overrides ever fail to apply again. Running the suite against a real
        // database would create users and devices in it and then drop nothing — far worse than
        // a failed test run, so it is checked rather than assumed.
        var actualDatabase = db.Database.GetDbConnection().Database;

        if (!string.Equals(actualDatabase, _databaseName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The test host connected to '{actualDatabase}' instead of the throwaway database " +
                $"'{_databaseName}'. The configuration overrides are not being applied; refusing to " +
                "run the suite against another database.");
        }

        await db.Database.CanConnectAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (!DatabaseAvailable)
            return;

        try
        {
            await using var connection = new SqlConnection(MasterConnection);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();

            // SINGLE_USER WITH ROLLBACK IMMEDIATE because the pooled connections from the test
            // run are still open; without it the DROP blocks and the database is left behind.
            command.CommandText =
                $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];";

            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // A leftover test database is untidy, not a test failure. Swallowing this keeps a
            // cleanup hiccup from masking the real result of the run.
        }
    }

    /// <summary>An HTTP client already bearing a freshly-issued token for the seeded Super Admin.</summary>
    public async Task<HttpClient> CreateSuperAdminClientAsync()
        => await CreateAuthenticatedClientAsync(SuperAdminEmail, SuperAdminPassword);

    public async Task<HttpClient> CreateAuthenticatedClientAsync(string email, string password)
    {
        var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, Json);
        response.EnsureSuccessStatusCode();

        var auth = await response.Content.ReadFromJsonAsync<LoginResult>(Json)
            ?? throw new InvalidOperationException("Login returned no body.");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    /// <summary>
    /// Returns ids for an active device type and location, creating them if the database has
    /// none. Sample-data seeding is off for this suite, so a fresh database starts with no
    /// reference data at all and every device test needs this first.
    /// </summary>
    public async Task<(int DeviceTypeId, int LocationId)> EnsureReferenceDataAsync(HttpClient client)
    {
        var types = await client.GetFromJsonAsync<List<DevicePulse.Api.Models.DeviceTypeResponse>>(
            "/api/v1/reference/device-types", Json);

        var typeId = types is { Count: > 0 }
            ? types[0].DeviceTypeId
            : (await (await client.PostAsJsonAsync("/api/v1/reference/device-types",
                    new { name = $"Test Type {Guid.NewGuid():N}"[..28], description = "Created by the test suite." }, Json))
                .Content.ReadFromJsonAsync<DevicePulse.Api.Models.DeviceTypeResponse>(Json))!.DeviceTypeId;

        var locations = await client.GetFromJsonAsync<List<DevicePulse.Api.Models.LocationResponse>>(
            "/api/v1/reference/locations", Json);

        var locationId = locations is { Count: > 0 }
            ? locations[0].LocationId
            : (await (await client.PostAsJsonAsync("/api/v1/reference/locations",
                    new { name = $"Test Location {Guid.NewGuid():N}"[..28], description = "Created by the test suite." }, Json))
                .Content.ReadFromJsonAsync<DevicePulse.Api.Models.LocationResponse>(Json))!.LocationId;

        return (typeId, locationId);
    }

    /// <summary>Registers a device and returns it, creating reference data first if required.</summary>
    public async Task<DevicePulse.Api.Models.DeviceResponse> RegisterDeviceAsync(
        HttpClient client, string prefix = "IT")
    {
        var (typeId, locationId) = await EnsureReferenceDataAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/devices", new
        {
            deviceCode = $"{prefix}-{Guid.NewGuid():N}"[..20],
            deviceName = $"{prefix} Test Device",
            deviceTypeId = typeId,
            locationId
        }, Json);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<DevicePulse.Api.Models.DeviceResponse>(Json))!;
    }

    public async Task<T> ExecuteDbAsync<T>(Func<DevicePulseDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<DevicePulseDbContext>());
    }

    public sealed record LoginResult(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAt, LoginUser User);
    public sealed record LoginUser(int UserId, string Name, string Email, List<string> Roles, List<string> Permissions);
}

/// <summary>
/// One application host shared by every test in the collection. Booting ASP.NET Core and running
/// migrations per test class would dominate the suite's runtime for no benefit.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<DevicePulseApiFactory>
{
    public const string Name = "DevicePulse API";
}
