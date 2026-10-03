# DevicePulse backend

ASP.NET Core 10 Web API, an EF Core data layer against SQL Server, and a console IoT simulator.

See the [root README](../README.md) for what the system does and why it is built this way. This
file covers running, configuring and extending the backend.

---

## Projects

| Project | Purpose |
|---|---|
| `src/DevicePulse.Api` | The API. A modular monolith with folder-based layering. |
| `src/DevicePulse.Simulator` | Console app that drives a virtual device fleet through the real endpoints. |
| `tests/DevicePulse.Tests` | Unit and integration tests (137). |

---

## First run

```powershell
# Writes the connection string, a generated JWT signing key and a generated Super Admin
# password to the per-user secret store. Nothing sensitive is written into the repository.
./setup-dev-secrets.ps1

# Use a named instance if the default one is not what you have:
./setup-dev-secrets.ps1 -SqlServer "localhost\SQLEXPRESS"
```

It prints the Super Admin password once. Save it, then:

```bash
cd src/DevicePulse.Api
dotnet run
```

In Development the API applies migrations and seeds on startup, so there is no separate database
step. Swagger is at <http://localhost:5082/swagger>.

### Doing it by hand

```bash
dotnet user-secrets set "ConnectionStrings:DevicePulseDb" \
  "Server=localhost;Database=DevicePulseDb;Trusted_Connection=True;TrustServerCertificate=True" \
  --project src/DevicePulse.Api

dotnet user-secrets set "Jwt:SigningKey" "$(openssl rand -base64 48)" --project src/DevicePulse.Api
dotnet user-secrets set "Seed:SuperAdminPassword" "<a strong password>" --project src/DevicePulse.Api
```

The API refuses to start without a connection string and a signing key of at least 32
characters, with a message naming what is missing — an options validation failure at boot is far
cheaper to diagnose than a failure on the first login.

---

## Configuration

Resolved in the standard order: `appsettings.json`, then `appsettings.{Environment}.json`, then
user secrets (Development only), then environment variables, then command-line arguments.

Every section is bound to a typed options class and validated with `ValidateOnStart`.

| Key | Default | Notes |
|---|---|---|
| `ConnectionStrings:DevicePulseDb` | *(none)* | Required. Never committed. |
| `Jwt:SigningKey` | *(none)* | Required, ≥ 32 characters. Never committed. |
| `Jwt:AccessTokenMinutes` | `15` | Short by design: permissions ride in the token, so this bounds how stale they can be. |
| `Jwt:RefreshTokenDays` | `7` | |
| `Security:PasswordMinLength` | `10` | Plus upper, lower, digit and symbol requirements. |
| `Security:MaxFailedLoginAttempts` | `5` | Per-account lockout. |
| `Security:LockoutMinutes` | `15` | |
| `Security:AuthRequestsPerMinute` | `10` | Per client IP, on `/api/v1/auth/*`. |
| `Security:IngestionRequestsPerMinute` | `3000` | Per device, on telemetry endpoints. |
| `Security:AllowedCorsOrigins` | `[]` | Explicit allow-list. Empty means no cross-origin access — it fails closed. |
| `Database:ApplyMigrationsOnStartup` | `true` in Development | See the caveat below. |
| `Database:SeedOnStartup` | `true` | Idempotent; fills in only what is missing. |
| `Seed:SuperAdminEmail` | `superadmin@devicepulse.local` | |
| `Seed:SuperAdminPassword` | *(none)* | Required to create the first admin. Never committed. |
| `Seed:SeedSampleData` | `false` | Development only; creates 24 sample devices. |

Business configuration — offline timeout, retention windows, alert thresholds, plausibility
bounds — is **not** here. It lives in the `SystemSettings` table and is edited through the API at
runtime. The distinction is deliberate: infrastructure and secrets belong in deployment
configuration, business behaviour belongs in the database where an administrator can change it
without a redeploy.

### Migrating on startup

Convenient for a single instance and for local setup. Not safe with several instances starting
at once, which is why it is behind a flag. To run migrations as a deploy step instead:

```bash
dotnet ef migrations bundle --project src/DevicePulse.Api --self-contained -o migrate
./migrate --connection "<connection string>"
```

...and set `Database:ApplyMigrationsOnStartup=false`.

---

## Tests

```bash
cd tests/DevicePulse.Tests
dotnet run                      # all 137
dotnet run -- -method "DevicePulse.Tests.Unit.AlertRuleEvaluatorTests.*"
```

`dotnet run`, not `dotnet test`: the project is xUnit v3, which hosts the Microsoft Testing
Platform and is executed directly. xUnit v3 was chosen over v2 for `Assert.Skip`, which lets the
integration suite skip cleanly when no SQL Server is reachable instead of reporting false
failures.

Integration tests create a uniquely-named database per run and drop it afterwards. They point at
`localhost` with integrated authentication by default; CI overrides that with
`DEVICEPULSE_TEST_SQL_SERVER`, `DEVICEPULSE_TEST_SQL_USER` and `DEVICEPULSE_TEST_SQL_PASSWORD`,
because a Linux runner has no Windows identity to trust.

---

## Simulator

```bash
cd src/DevicePulse.Simulator

dotnet user-secrets set "Simulator:Email" "superadmin@devicepulse.local"
dotnet user-secrets set "Simulator:Password" "<the password>"

dotnet run
dotnet run -- --Simulator:DeviceCount=500 --Simulator:Rounds=10 --Simulator:BurstSize=500
```

The account needs `telemetry.ingest`. In development the seeded Super Admin has it; a real
deployment would use a dedicated service account holding that one permission and nothing else.

Key parameters: `DeviceCount`, `IntervalSeconds`, `Rounds` (0 = until Ctrl+C), `BurstMode`,
`BurstSize`, `TemperatureMin`/`Max`, `AnomalyProbability`, `FailureProbability`,
`BatteryDrainPerReading`, `RandomSeed`.

`RandomSeed` is fixed by default so two runs generate identical telemetry — without it, a
throughput difference between runs could just be different data rather than a real change.
Note that re-running with the same seed and round count produces the same `MessageId`s, so the
API correctly reports the second run's readings as duplicates; change the seed for a fresh run.

---

## Adding things

**A permission.** Add a constant and a `Definition` to `Authorization/Permissions.cs`, then put
`[HasPermission(Permissions.YourKey)]` on the action. The seeder inserts it on next startup, the
policy provider resolves it on demand, and it appears in the role editor. A unit test fails if a
constant is declared without a catalog entry. Mirror the key in the client's
`core/auth/permissions.ts` — that copy only decides what the UI offers, so drift there changes a
menu item, never an access decision.

**A runtime setting.** Add a constant and a `Definition` (type, default, bounds, unit, category)
to `Services/Configuration/SettingKeys.cs`, and read it through `IRuntimeSettings`. The seeder
inserts it, the admin UI renders the right control from its metadata, and validation comes from
the declaration. Unit tests fail if the default does not parse as its declared type or falls
outside its own bounds.

**An entity or a mapping change.** Add the entity, add an `IEntityTypeConfiguration` under
`Data/Configurations` (they are discovered automatically), then:

```bash
dotnet ef migrations add YourChange --project src/DevicePulse.Api --output-dir Migrations
```

**An alert metric.** Add it to the `AlertMetric` enum, resolve it in
`AlertRuleEvaluator.ResolveMetric`, give it a unit in `UnitFor`, and add a validation range in
`AlertRuleService.Validate`. The vocabulary endpoint and the rule editor pick it up with no
further change; a unit test fails if an enum member is added without being resolvable.

---

## Things to know before changing code here

- **EF cannot filter or order on a projected object.** `.Select(...).Where(x => x.Inner.Id == 1)`
  fails to translate at runtime, not at compile time. Filter and order before projecting. Two
  such bugs were caught by the integration suite rather than by the compiler.
- **Grouped queries with several conditional counts over a join** are also untranslatable. Group
  on the foreign key and attach names from a separate lookup.
- **`.cs` files carry a UTF-8 BOM** (and `.editorconfig` requires it). Without one, the compiler
  on Windows falls back to the system code page and silently corrupts the degree signs and em
  dashes inside string literals.
- **`await` is not allowed in a `catch` filter.** The duplicate-key handlers re-query inside the
  catch body for this reason.
- **The audit service stages entries on the caller's unit of work.** It never calls
  `SaveChanges`; the caller commits the business change and its audit record together, so the
  two cannot disagree about what happened.
- **Only pass a projection to `_audit.Record`, never a whole entity.** That is what keeps
  password hashes and tokens out of the audit trail structurally rather than by remembering.
- **Zero build warnings, and `dotnet format --verify-no-changes` is a CI gate.** Run
  `dotnet format DevicePulse.slnx` before pushing if the formatter complains.
