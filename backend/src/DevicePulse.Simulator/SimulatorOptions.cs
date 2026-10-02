namespace DevicePulse.Simulator;

/// <summary>
/// Every knob from §19 of the master reference. Bound from appsettings plus command-line
/// overrides, so a load-test run is one command rather than a code edit.
/// </summary>
public sealed class SimulatorOptions
{
    public const string SectionName = "Simulator";

    /// <summary>Base address of the API, e.g. http://localhost:5082.</summary>
    public string ApiBaseUrl { get; set; } = "http://localhost:5082";

    /// <summary>Credentials of a user holding telemetry.ingest. The simulator reports on behalf of many devices, so it authenticates as a user rather than as one device.</summary>
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>How many devices to simulate. The §36 targets are 10 / 100 / 500 / 1000.</summary>
    public int DeviceCount { get; set; } = 10;

    /// <summary>Seconds between reporting rounds.</summary>
    public double IntervalSeconds { get; set; } = 5;

    /// <summary>Stop after this many rounds. Zero means run until interrupted.</summary>
    public int Rounds { get; set; }

    /// <summary>Register the devices it needs if they do not already exist.</summary>
    public bool AutoProvisionDevices { get; set; } = true;

    /// <summary>Prefix for auto-provisioned device codes.</summary>
    public string DeviceCodePrefix { get; set; } = "SIM";

    public double TemperatureMin { get; set; } = 18;
    public double TemperatureMax { get; set; } = 45;

    /// <summary>Starting battery percentage for every simulated device.</summary>
    public double BatteryStart { get; set; } = 100;

    /// <summary>Percentage points of battery drained per reading. Small by default so a long run shows a realistic decline.</summary>
    public double BatteryDrainPerReading { get; set; } = 0.05;

    public int SignalStrengthMin { get; set; } = -110;
    public int SignalStrengthMax { get; set; } = -50;

    /// <summary>
    /// Chance (0..1) that a device skips a round entirely, simulating a dropped connection.
    /// This is what exercises the offline-detection worker and the offline alert rule.
    /// </summary>
    public double FailureProbability { get; set; } = 0.02;

    /// <summary>Chance (0..1) that a device emits an abnormal spike, to exercise the alert rules.</summary>
    public double AnomalyProbability { get; set; } = 0.05;

    /// <summary>Send each round as one bulk request instead of one request per device.</summary>
    public bool BurstMode { get; set; } = true;

    /// <summary>Maximum readings per bulk request. Rounds larger than this are split into several.</summary>
    public int BurstSize { get; set; } = 200;

    /// <summary>
    /// Fixed RNG seed, so two runs produce identical telemetry. Essential for comparing
    /// load-test results — without it, a change in throughput could just be different data.
    /// </summary>
    public int RandomSeed { get; set; } = 1337;

    /// <summary>Attach a MessageId to every reading so the API can dedupe retries (Appendix D.1).</summary>
    public bool SendMessageIds { get; set; } = true;

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiBaseUrl))
            yield return "Simulator:ApiBaseUrl is required.";

        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
            yield return "Simulator:Email and Simulator:Password are required (a user holding telemetry.ingest).";

        if (DeviceCount < 1)
            yield return "Simulator:DeviceCount must be at least 1.";

        if (IntervalSeconds <= 0)
            yield return "Simulator:IntervalSeconds must be greater than zero.";

        if (TemperatureMin >= TemperatureMax)
            yield return "Simulator:TemperatureMin must be below TemperatureMax.";

        if (SignalStrengthMin >= SignalStrengthMax)
            yield return "Simulator:SignalStrengthMin must be below SignalStrengthMax.";

        if (FailureProbability is < 0 or > 1)
            yield return "Simulator:FailureProbability must be between 0 and 1.";

        if (AnomalyProbability is < 0 or > 1)
            yield return "Simulator:AnomalyProbability must be between 0 and 1.";

        if (BurstSize is < 1 or > 1000)
            yield return "Simulator:BurstSize must be between 1 and 1000 (the API caps a bulk request at 1000).";
    }
}
