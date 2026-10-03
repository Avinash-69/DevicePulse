using System.Text;
using System.Threading.RateLimiting;
using DevicePulse.Api.Authorization;
using DevicePulse.Api.BackgroundServices;
using DevicePulse.Api.Data;
using DevicePulse.Api.Data.Seed;
using DevicePulse.Api.Middleware;
using DevicePulse.Api.Options;
using DevicePulse.Api.Realtime;
using DevicePulse.Api.Services;
using DevicePulse.Api.Services.Alerting;
using DevicePulse.Api.Services.Configuration;
using DevicePulse.Api.Services.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration — strongly typed, validated at startup (Appendix D.2).
// ValidateOnStart means a missing signing key or a malformed option kills the
// process at boot with a clear message, instead of failing the first login.
// ---------------------------------------------------------------------------

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<SecurityOptions>()
    .Bind(builder.Configuration.GetSection(SecurityOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<SeedOptions>()
    .Bind(builder.Configuration.GetSection(SeedOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException(
        "The Jwt configuration section is missing. See backend/README.md for the required settings.");

var securityOptions = builder.Configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>()
    ?? new SecurityOptions();

// ---------------------------------------------------------------------------
// Persistence
// ---------------------------------------------------------------------------

var connectionString = builder.Configuration.GetConnectionString("DevicePulseDb");

if (string.IsNullOrWhiteSpace(connectionString))
{
    // Checked explicitly so a missing connection string produces one clear sentence at startup
    // rather than an EF stack trace on the first query (Appendix D.2: fail fast at boot).
    throw new InvalidOperationException(
        "ConnectionStrings:DevicePulseDb is not configured. In development run " +
        "backend/setup-dev-secrets.ps1 or set it with `dotnet user-secrets`; elsewhere supply it " +
        "through the environment or a secret store. See backend/README.md.");
}

// Singleton, so it can be handed to every context instance below; its per-save state is keyed to
// the context it belongs to.
builder.Services.AddSingleton<LiveEventDispatcher>();
builder.Services.AddSingleton<ILivePublisher>(services => services.GetRequiredService<LiveEventDispatcher>());
builder.Services.AddHostedService(services => services.GetRequiredService<LiveEventDispatcher>());
builder.Services.AddSingleton<LiveChangeInterceptor>();

builder.Services.AddDbContext<DevicePulseDbContext>((services, options) =>
{
    // Pushes committed alert and device-status changes to live dashboards. On SaveChanges rather
    // than in each service, so no write path can forget to announce itself.
    options.AddInterceptors(services.GetRequiredService<LiveChangeInterceptor>());

    options.UseSqlServer(
        connectionString,
        sql =>
        {
            // Transient network and failover errors are expected against a real SQL Server;
            // retrying them here is cheaper than surfacing them as 500s.
            sql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null);
            sql.CommandTimeout(30);
        });

    if (builder.Environment.IsDevelopment())
    {
        // Parameter values in logs are a genuine help while developing and a data leak in
        // production, so this is explicitly environment-gated (Appendix D.5).
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
});

// ---------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<IDeviceApiKeyService, DeviceApiKeyService>();

// Singleton: settings are read on nearly every request, so this holds one in-process cache
// rather than querying per request. It creates its own scope when it needs the database.
builder.Services.AddSingleton<IRuntimeSettings, RuntimeSettings>();

builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IDeviceService, DeviceService>();
builder.Services.AddScoped<ITelemetryService, TelemetryService>();
builder.Services.AddScoped<TelemetryBulkIngest>();
builder.Services.AddScoped<IAlertEngine, AlertEngine>();
builder.Services.AddScoped<IAlertService, AlertService>();
builder.Services.AddScoped<IAlertRuleService, AlertRuleService>();
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddScoped<IUserAdminService, UserAdminService>();
builder.Services.AddScoped<IRoleService, RoleService>();
builder.Services.AddScoped<IReferenceDataService, ReferenceDataService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<DatabaseSeeder>();

builder.Services.AddHostedService<OfflineDetectionWorker>();
builder.Services.AddHostedService<RetentionWorker>();

// ---------------------------------------------------------------------------
// Authentication — two schemes, deliberately separate (Appendix C item 3).
// Humans present a JWT; devices present an API key. Neither can be used in the
// other's place.
// ---------------------------------------------------------------------------

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,

            // The default is five minutes, which would silently extend every token's life.
            // Access tokens here are short by design, so the skew is tightened to match.
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        // Lets the SPA distinguish "token expired, refresh and retry" from "you lack
        // permission" without parsing the body.
        options.Events = new JwtBearerEvents
        {
            // A browser cannot set an Authorization header on a WebSocket or EventSource request,
            // so the SignalR client sends the token as access_token in the query string. It is
            // accepted there for the hub path only; every other endpoint still requires the header.
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];

                if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments(LiveHub.Path))
                    context.Token = token;

                return Task.CompletedTask;
            },

            OnAuthenticationFailed = context =>
            {
                if (context.Exception is SecurityTokenExpiredException)
                    context.Response.Headers["X-Token-Expired"] = "true";

                return Task.CompletedTask;
            }
        };
    })
    .AddScheme<DeviceApiKeyOptions, DeviceApiKeyAuthenticationHandler>(
        DeviceApiKeyDefaults.Scheme, _ => { });

// ---------------------------------------------------------------------------
// Authorization — permission-based policies, resolved on demand so a new
// permission needs no matching AddPolicy call (§11, Appendix A.3).
// ---------------------------------------------------------------------------

builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

builder.Services.AddAuthorization(options =>
{
    // Fallback: anything without an explicit [Authorize] or [AllowAnonymous] still requires
    // authentication. The failure mode of forgetting an attribute becomes a 401, not an
    // accidentally public endpoint.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// ---------------------------------------------------------------------------
// CORS — a named allow-list, never AllowAnyOrigin (Appendix D.3)
// ---------------------------------------------------------------------------

const string CorsPolicy = "DevicePulseSpa";

builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicy, policy =>
    {
        if (securityOptions.AllowedCorsOrigins.Length == 0)
        {
            // No configured origins means no cross-origin access. Failing closed is the right
            // default: a misconfigured deployment should break visibly, not open up.
            policy.WithOrigins().AllowAnyHeader().AllowAnyMethod();
            return;
        }

        policy.WithOrigins(securityOptions.AllowedCorsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders(CorrelationIdMiddleware.HeaderName, "X-Token-Expired")
              .AllowCredentials();
    });
});

// ---------------------------------------------------------------------------
// Rate limiting (Appendix D.3)
// ---------------------------------------------------------------------------

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(RateLimitPolicies.Authentication, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            // Partitioned by IP rather than globally: one abusive client must not be able to
            // lock every other user out of signing in.
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = securityOptions.AuthRequestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy(RateLimitPolicies.Ingestion, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            // Keyed on the device when a device key was used, so one noisy device cannot
            // consume the whole fleet's budget.
            partitionKey: context.User.FindFirst(DeviceApiKeyDefaults.DeviceCodeClaim)?.Value
                          ?? context.Connection.RemoteIpAddress?.ToString()
                          ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = securityOptions.IngestionRequestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.OnRejected = async (context, _) =>
    {
        // contentType is passed through, because WriteAsJsonAsync would otherwise replace it
        // with "application/json" and break the single error contract.
        await context.HttpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Type = "https://httpstatuses.io/429",
                Title = "TooManyRequests",
                Status = StatusCodes.Status429TooManyRequests,
                Detail = "Too many requests. Slow down and try again shortly.",
                Extensions = { ["traceId"] = context.HttpContext.TraceIdentifier }
            },
            options: null,
            contentType: "application/problem+json");
    };
});

// ---------------------------------------------------------------------------
// Health checks — two endpoints with different jobs (Appendix D.4)
// ---------------------------------------------------------------------------

builder.Services.AddHealthChecks()
    .AddDbContextCheck<DevicePulseDbContext>(
        name: "sql-server",
        tags: ["ready"]);

// ---------------------------------------------------------------------------
// MVC and OpenAPI
// ---------------------------------------------------------------------------

builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        // Model-binding failures are reshaped into the same ProblemDetails envelope the
        // exception handler produces, so a client only ever has to parse one error shape
        // (Appendix D.1).
        options.InvalidModelStateResponseFactory = context =>
        {
            var errors = context.ModelState
                .Where(e => e.Value?.Errors.Count > 0)
                .ToDictionary(
                    e => e.Key,
                    e => e.Value!.Errors.Select(x => x.ErrorMessage).ToArray());

            var problem = new ValidationProblemDetails(errors)
            {
                Type = "https://httpstatuses.io/400",
                Title = "ValidationFailed",
                Status = StatusCodes.Status400BadRequest,
                Detail = "One or more validation errors occurred.",
                Instance = context.HttpContext.Request.Path
            };

            problem.Extensions["traceId"] =
                context.HttpContext.Items[CorrelationIdMiddleware.ItemKey] as string
                ?? context.HttpContext.TraceIdentifier;

            return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
        };
    })
    .AddJsonOptions(options =>
    {
        // Enums travel as their names, not their numbers: "High" survives a reordering of the
        // enum and is readable in a log or a browser devtools panel; 3 is neither.
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

// Same enum-as-name convention as the REST API, so a pushed alert reads "High" exactly as a
// fetched one does and the client needs one set of types.
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
        options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "DevicePulse API",
        Version = "v1",
        Description =
            "IoT device monitoring, telemetry ingestion, configurable alerting and runtime administration."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the accessToken returned by POST /api/v1/auth/login."
    });

    options.AddSecurityDefinition(DeviceApiKeyDefaults.Scheme, new OpenApiSecurityScheme
    {
        Name = DeviceApiKeyDefaults.HeaderName,
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "A device ingestion key, for the telemetry endpoints only."
    });

    // Microsoft.OpenApi 2.x references a declared scheme through OpenApiSecuritySchemeReference
    // rather than the older inline OpenApiReference shape.
    options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer")] = []
    });

    // Surfaces the XML doc comments in Swagger, so the "why" written next to each endpoint is
    // visible to whoever is exploring the API.
    var xmlPath = Path.Combine(AppContext.BaseDirectory, "DevicePulse.Api.xml");
    if (File.Exists(xmlPath))
        options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// Pipeline. Order matters more here than almost anywhere else in the app.
// ---------------------------------------------------------------------------

// First, so every later component — including the exception handler — shares one id.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();

// Before anything that can throw, so nothing escapes as an unshaped 500.
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "DevicePulse API v1");
        options.DocumentTitle = "DevicePulse API";
    });
}
else
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseCors(CorsPolicy);
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapHub<LiveHub>(LiveHub.Path, options =>
{
    // Without this a socket outlives the access token that opened it, and with it the permission
    // set that decided which groups it joined. Closing it makes the client reconnect with a fresh
    // token, so the push channel has the same bounded staleness as the REST API.
    options.CloseOnAuthenticationExpiration = true;
});

// Liveness: is the process up and able to answer? Deliberately has no database check — if SQL
// Server is down, restarting the API will not help, and a failing liveness probe would put the
// container into a pointless restart loop (Appendix D.4).
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();

// Readiness: are the dependencies actually reachable? This is the one a load balancer should
// use to decide whether to send traffic.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = e.Value.Duration.TotalMilliseconds,

                // The exception message is withheld outside development: a failed database
                // check would otherwise leak the connection string's server and database name
                // to an unauthenticated caller.
                error = app.Environment.IsDevelopment() ? e.Value.Exception?.Message : null
            })
        });
    }
}).AllowAnonymous();

// ---------------------------------------------------------------------------
// Startup: migrate and seed.
//
// Migrating on startup suits a single-instance deployment and keeps local setup to one
// command. It is not safe with several instances starting at once, which is why it is
// explicitly gated — see backend/README.md on running migrations as a deploy step instead.
// ---------------------------------------------------------------------------

await InitializeDatabaseAsync(app);

app.Run();

static async Task InitializeDatabaseAsync(WebApplication app)
{
    var applyMigrations = app.Configuration.GetValue("Database:ApplyMigrationsOnStartup", app.Environment.IsDevelopment());
    var runSeed = app.Configuration.GetValue("Database:SeedOnStartup", true);

    if (!applyMigrations && !runSeed)
        return;

    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        var db = scope.ServiceProvider.GetRequiredService<DevicePulseDbContext>();

        if (applyMigrations)
        {
            logger.LogInformation("Applying database migrations...");
            await db.Database.MigrateAsync();
        }

        if (runSeed)
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
            await seeder.SeedAsync();
        }

        logger.LogInformation("Database is ready.");
    }
    catch (Exception ex)
    {
        // Rethrown rather than swallowed: an API running against an unmigrated database would
        // fail every request in a confusing way. Failing at startup is the clearer signal.
        logger.LogCritical(ex, "Database initialization failed. The application cannot start.");
        throw;
    }
}
