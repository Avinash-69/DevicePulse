# DevicePulse

An IoT device monitoring and management platform: register devices, ingest telemetry, detect
device health conditions through runtime-configurable alert rules, and administer users, roles,
permissions and business settings — all without a code change or a redeploy.

ASP.NET Core 10 · Angular 22 · SQL Server · EF Core

---

## What it does

| Area | Implemented |
|---|---|
| **Authentication** | Registration, login, PBKDF2 password hashing, JWT access tokens, single-use rotating refresh tokens, per-account lockout, password change |
| **Authorization** | `User → Role → Permission → Policy`, 28-permission catalog, runtime role and permission administration, separate device API-key scheme |
| **Devices** | Register, edit, retire (not delete), paged/filtered/sorted listing, ingestion key issue and rotation, lifecycle and connectivity tracked separately |
| **Telemetry** | Single and bulk ingestion, idempotent retries, out-of-order tolerance, history, hourly aggregated trends, retention purge |
| **Alerting** | Runtime-configurable rules over a closed metric/operator vocabulary, per-rule cooldown, severity ordering, acknowledge and resolve, offline auto-resolve |
| **Configuration** | Typed settings with bounds and units, server-side validation, version history with reasons, applied without restart |
| **Administration** | User and role management, permission editor, device types and locations, read-only audit trail |
| **Dashboard** | Fleet counters, temperature trend with min/max band, severity breakdown, distribution, "needs attention" list |
| **Background work** | Offline detection sweeper, data retention worker |
| **Simulator** | Separate console app driving a configurable virtual fleet, with measured throughput and latency output |
| **Engineering** | 137 backend tests, 14 frontend tests, ProblemDetails error contract, correlation IDs, rate limiting, health endpoints, Docker, CI |

Not yet built: SignalR real-time push, Redis, a message queue, Keycloak, multi-tenancy, and
notification channels. Those are the project's planned later phases, and nothing in the UI
pretends they exist.

---

## Running it

### Option 1 — Docker (everything at once)

```bash
cp .env.example .env     # then fill in the three required values
docker compose up --build
```

Open <http://localhost:8080>. The API is proxied under the same origin at `/api/v1`.

### Option 2 — Locally

Requires the .NET 10 SDK, Node 22.22+ / 24.15+ / 26+, and a reachable SQL Server.

```bash
# 1. Development secrets (connection string, signing key, seed password).
#    These are written to the per-user secret store, never to a file in the repo.
cd backend
./setup-dev-secrets.ps1

# 2. API — applies migrations and seeds on startup in Development.
cd src/DevicePulse.Api
dotnet run
#    → http://localhost:5082/swagger

# 3. Client, in a second terminal.
cd frontend/devicepulse-web
npm install
npm start
#    → http://localhost:4200
```

Sign in with the seeded Super Admin (`setup-dev-secrets.ps1` prints the password once) and
change that password straight away.

### Option 3 — Generate load

```bash
cd backend/src/DevicePulse.Simulator
dotnet run -- --Simulator:DeviceCount=500 --Simulator:Rounds=10
```

---

## Measured performance

Numbers from an actual run on a developer machine (local SQL Server, Debug build, API and
simulator on the same host) — not projections:

| Devices | Mode | Readings | Throughput | Round latency p50 / p95 |
|---|---|---|---|---|
| 200 | bulk, 1s interval | 1,133 | 196/s | 427 ms / 1,571 ms |
| 500 | bulk, saturated | 5,000 | **542/s** | 564 ms / 2,335 ms |

The bulk path was rewritten after measurement. The first version looped the single-reading
method, costing a `SaveChanges` round trip and a cooldown query per reading, and managed
~120 readings/sec at 200 devices with a p50 round latency of 1,331 ms. Doing the work
set-at-a-time — devices and existing message IDs loaded once, rules and cooldown state loaded
once, one save for the batch — cut p50 round latency by roughly 3× at the same device count.

Reproduce with:

```bash
dotnet run -- --Simulator:DeviceCount=500 --Simulator:Rounds=10 \
              --Simulator:IntervalSeconds=0.01 --Simulator:BurstSize=500
```

---

## Layout

```
DevicePulse/
├── backend/
│   ├── DevicePulse.slnx
│   ├── setup-dev-secrets.ps1
│   ├── src/
│   │   ├── DevicePulse.Api/            modular monolith, folder-based layering
│   │   │   ├── Controllers/            endpoints, routing, permission attributes
│   │   │   ├── Services/               business logic
│   │   │   │   ├── Alerting/           rule evaluator (pure) + alert engine
│   │   │   │   ├── Configuration/      typed runtime settings
│   │   │   │   └── Security/           hashing, tokens, device keys
│   │   │   ├── Data/                   DbContext, EF configuration, seeder
│   │   │   ├── Entities/               domain model
│   │   │   ├── Models/                 request/response DTOs
│   │   │   ├── Authorization/          permission catalog, policies, device scheme
│   │   │   ├── Middleware/             correlation ID, errors, security headers
│   │   │   ├── BackgroundServices/     offline detection, retention
│   │   │   └── Migrations/
│   │   └── DevicePulse.Simulator/      IoT load generator
│   └── tests/DevicePulse.Tests/        unit + integration
├── frontend/devicepulse-web/           Angular SPA
│   └── src/app/
│       ├── core/                       models, API client, auth, interceptors
│       ├── shared/                     UI primitives, charts, pipes
│       ├── layout/                     shell, navigation, toasts
│       └── features/                   one lazily-loaded folder per page
├── docker-compose.yml
└── .github/workflows/ci.yml
```

Backend and frontend are separate trees with separate builds, tests and images, matching how
they would be deployed and pipelined independently.

The API is one project with folder-based layering rather than four class libraries. The same
four concerns exist; they are namespaces instead of compiler-enforced assembly boundaries,
because at this size the project-reference ceremony cost more than it bought. Splitting them
out later remains possible and is a "when it hurts" decision.

---

## Design decisions worth knowing

**The backend is the only security boundary.** The client hides controls the user cannot use,
and that is a convenience. Every endpoint re-checks authentication, permission and ownership;
a hand-rolled `DELETE /api/v1/devices/15` gets a 403 whether or not the UI ever rendered a
button. The integration suite asserts this for Viewer and Operator roles explicitly.

**Permissions live in the token; the catalog lives in code.** Flattening permissions into the
access token means the common path costs no database round trip. The trade-off is that a
permission change takes effect at the next token refresh, which is why access tokens are
short-lived — the staleness window is bounded and deliberate. Permission *keys* are defined in
code as features are built, because a key with no enforcement behind it would be a lie;
what administrators configure at runtime is which roles hold which existing keys.

**Lifecycle and connectivity are different things.** `Registered/Active/Inactive/Retired` is an
administrative decision a human makes. `Online/Offline/Unknown` is an observation the system
derives from telemetry. A brand-new device is `Unknown`, not `Offline` — we have never seen it,
which is not the same as having seen it and lost it.

**Delete retires.** A decommissioned device still has to explain its own history, so `DELETE`
sets `Retired`, hides the device from active views, closes its open alerts and revokes its key.
Its telemetry and alert history stay queryable.

**Telemetry ingestion assumes retries and reordering.** A device on a flaky link re-POSTs a
reading it already delivered; an optional `MessageId` plus a filtered unique index means the
retry returns the original row instead of creating a second one and double-firing a rule. A
late delivery is stored but never drags `LastSeenAt` backwards. A reading stamped in the future
is pulled back to server time.

**Alert severity is stored numerically.** Everything else is stored as a string for readability
in a SQL window, but severity has a meaningful order and the alert list is paged worst-first in
SQL. As a string, `Medium` sorts above `High` — which is how that bug was found.

**Rules use a closed vocabulary.** Metric and operator are enums, served to the UI from the
enums themselves, so the rule editor cannot offer a combination the backend would reject. A
free-text condition field would be an injection surface and untestable. Equality against a
continuously-varying metric is refused outright, because such a rule would silently never fire.

**Offline detection must be a sweeper.** Going offline is the absence of an event — no request
arrives to trigger the transition — so a device that dies silently would stay Online forever if
this were computed on read.

**Settings are typed, not an EAV dump.** Each row declares its type, bounds, unit and category.
That is what lets the backend validate an edit against the setting's own metadata and lets the
admin UI render the right control with no per-setting special casing — a setting added on the
backend simply appears, correctly rendered and validated.

**Secrets never reach the repository.** Connection strings, the JWT signing key and the seed
password come from `dotnet user-secrets` in development and the environment elsewhere. The
seeder refuses to invent a Super Admin password: a hard-coded fallback would ship a known
administrator credential, and a generated one nobody records would leave the deployment with an
unreachable admin account.

**No Start button on the simulator page.** The simulator is a separate console application and
the API has no endpoint that launches a process, so buttons there would be decoration. The page
says so and offers what is real: a command builder, live ingestion state, and single-reading
submission through the actual endpoint.

---

## Testing

```bash
# Backend — 137 tests. Integration tests need a reachable SQL Server and
# skip cleanly (rather than failing) if there is none.
cd backend/tests/DevicePulse.Tests && dotnet run

# Frontend — 14 tests, headless Chrome.
cd frontend/devicepulse-web && npm test -- --watch=false
```

Integration tests run against a real SQL Server on purpose. What they cover cannot be modelled
by the in-memory provider: filtered unique indexes behind idempotency, `rowversion` concurrency
rejection, real cascade behaviour on retirement, and the SQL translation of the dashboard's
grouped queries. Each run creates a uniquely-named database and drops it afterwards, and the
factory asserts which database it connected to — an earlier version layered its configuration
through `ConfigureAppConfiguration`, which is applied *after* `Program.cs` reads configuration,
so user-secrets won and the suite silently ran against the developer's own database.

---

## API

Swagger UI is at `/swagger` in Development. All routes are versioned under `/api/v1`.

| Area | Endpoints |
|---|---|
| Auth | `POST /auth/register`, `/auth/login`, `/auth/refresh`, `/auth/logout`, `/auth/change-password`, `GET /auth/me` |
| Devices | `GET /devices`, `GET /devices/{id}`, `POST /devices`, `PUT /devices/{id}`, `DELETE /devices/{id}` (retires), `POST|DELETE /devices/{id}/api-key` |
| Telemetry | `POST /telemetry` (device key), `POST /telemetry/ingest`, `POST /telemetry/bulk`, `GET /devices/{id}/telemetry`, `/latest`, `/trend` |
| Alerts | `GET /alerts`, `GET /alerts/{id}`, `POST /alerts/{id}/acknowledge`, `/resolve` |
| Alert rules | `GET /alert-rules`, `/vocabulary`, `POST`, `PUT /{id}`, `PATCH /{id}/status`, `DELETE /{id}` |
| Settings | `GET /settings`, `/categories`, `GET|PUT /settings/{key}`, `GET /settings/{key}/history` |
| Reference | `GET|POST /reference/device-types`, `PUT /{id}`, same for `/reference/locations` |
| Admin | `GET|POST /admin/users`, `PUT /{id}`, `PATCH /{id}/status`, `POST /{id}/reset-password`, `GET|POST /admin/roles`, `PUT /admin/roles/{id}/permissions`, `GET /admin/permissions` |
| Audit | `GET /audit` (read-only) |
| Dashboard | `GET /dashboard/summary` |
| Health | `GET /health/live`, `/health/ready` |

Every error is RFC 7807 `ProblemDetails` with a `traceId` that is also returned as the
`X-Correlation-Id` header, so a failure can be traced end to end from a single quoted value.
Every list endpoint returns the same paged envelope.

Devices authenticate with their own key in `X-Device-Key` and hold exactly one permission,
`telemetry.ingest`. A device key can only submit readings for its own device — the device id
comes from the credential, never from the request body.

---

## Notes and known constraints

- **Migrations run at startup**, which suits a single instance and keeps setup to one command.
  Scaling the API out means moving them to a deploy step; several instances racing to migrate
  the same database is a real failure mode. The behaviour is behind
  `Database:ApplyMigrationsOnStartup`.
- **Tokens are kept in `localStorage`**, a bounded trade-off: it survives a refresh, and the
  alternative (httpOnly cookies) would need cookie auth plus CSRF protection on the API. It is
  acceptable while the app ships no third-party scripts and access tokens are short-lived, and
  is the right thing to revisit when identity moves to Keycloak.
- **The in-memory settings cache is per-instance.** A write invalidates immediately, so the TTL
  only bounds staleness *between* instances — relevant once the API is scaled out, harmless
  while it is not. A distributed cache gets introduced when there is a measurement, not before.
- **Docker images are built in CI but not pushed**, because there is no registry or environment
  to deploy to yet. The step exists to prove the Dockerfiles still work.
