using DevicePulse.Simulator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Configuration precedence: appsettings, then user-secrets (so credentials stay out of Git,
// Appendix D.2), then environment variables, then command-line switches. That ordering is what
// makes `--Simulator:DeviceCount=500` a one-off override rather than a file edit (§19/§36).
var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddUserSecrets<Program>(optional: true);

var options = builder.Configuration.GetSection(SimulatorOptions.SectionName).Get<SimulatorOptions>()
              ?? new SimulatorOptions();

var problems = options.Validate().ToList();

if (problems.Count > 0)
{
    Console.Error.WriteLine("The simulator is not configured correctly:");

    foreach (var problem in problems)
        Console.Error.WriteLine($"  - {problem}");

    Console.Error.WriteLine();
    Console.Error.WriteLine("See backend/src/DevicePulse.Simulator/README.md.");
    return 1;
}

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<SimulationEngine>();

builder.Services.AddHttpClient<DevicePulseApiClient>(client =>
{
    client.BaseAddress = new Uri(options.ApiBaseUrl);

    // A bulk request of up to 1000 readings can take a while against a cold database, so the
    // default 100s stays; what matters more is that it is explicit.
    client.Timeout = TimeSpan.FromSeconds(100);
    client.DefaultRequestHeaders.Add("User-Agent", "DevicePulse.Simulator");
});

builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddSimpleConsole(c =>
    {
        c.SingleLine = true;
        c.TimestampFormat = "HH:mm:ss ";
    });
});

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var engine = host.Services.GetRequiredService<SimulationEngine>();

// Ctrl+C cancels cooperatively so the run still prints its summary — a load test whose numbers
// vanish when you stop it is not much use.
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    logger.LogInformation("Stopping after the current round...");
    cts.Cancel();
};

try
{
    await engine.RunAsync(cts.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "The simulator stopped with an unrecoverable error.");
    return 1;
}
