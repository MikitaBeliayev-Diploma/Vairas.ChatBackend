# Vairas Chat Backend

School-scoped assistant conversations (ASP.NET Core 10 minimal API, PostgreSQL, EF Core/Npgsql). It keeps
conversation history, runs the model adapter with bounded tool calls through the MCP server, produces action
previews and, when the confirmed-lesson-actions profile is enabled, records the exact user confirmation and
dispatches the confirmed write. Real personal data is never sent to a model; a local Azure OpenAI wire double is
used in the e2e stack.

## Projects

| Project | Responsibility |
|---|---|
| `Vairas.ChatBackend` | HTTP API: endpoints, `ChatApplication` composition root, `Configuration/Configure*`, owner/MCP/model adapters, startup commands |
| `Vairas.ChatBackend.Business` | Chat service, run service, ports and contracts |
| `Vairas.ChatBackend.Domain` | Conversation, run and preview entities |
| `Vairas.ChatBackend.Persistance` | `ChatDbContext`, migrations, store |
| `Vairas.ChatBackend.Tests` / `.ServiceTests` | Unit/PostgreSQL tests and multi-service HTTP scenarios |

## Dependencies

PostgreSQL 17, Authentication, School Management, the MCP server and (optionally) an Azure OpenAI deployment.
Local origin `https://localhost:7800`.

## Essential configuration

Options are bound and validated at startup (`Configuration/ConfigureOptions.cs`). Use `__` in environment variables.

| Section | Notes |
|---|---|
| `ConnectionStrings:Chat` | restricted runtime role (required outside Testing) |
| `Authentication` | HTTPS `Authority`/`Issuer`; `WorkloadClients` allowlist |
| `Dependencies` | HTTPS `AuthBaseUrl`, `SchoolBaseUrl`, `BusinessApiBaseUrl`; workload `ClientSecret` |
| `Chat` | `BrowserOrigins` (exact HTTPS origins), bounded run/model/preview limits, `ConfirmedLessonActionsEnabled`, `EmergencyDisabled` |
| `Mcp` | `Enabled`, `Endpoint` |
| `Model` | disabled by default; enabling requires `AllowSyntheticCloud`, HTTPS `Endpoint`, `Deployment`, `Region` |
| `Store:DeploymentId` | registered model deployment identity |

## Run locally

```powershell
dotnet run --project Vairas.ChatBackend --launch-profile https
dotnet run --project Vairas.ChatBackend -- --initialize [--register-model]   # operator command: migrate, grant
```

The complete local stack is started from `../local/Start.ps1`. The confirmed-action profile is described in
[docs/confirmed-lesson-actions.md](docs/confirmed-lesson-actions.md).

## Tests

```powershell
dotnet test Vairas.ChatBackend.slnx             # Docker required for PostgreSQL tests
./scripts/verify.ps1 -SkipIntegration           # unit tests with the coverage gate
```

API exports: `api/`. Extended notes, design sources, validation reports and evidence:
`../../Documentation/Backend/Vairas.ChatBackend/`.

## Tests and coverage

Requirements: .NET SDK 10 and Docker (PostgreSQL integration tests start a throw-away container via Testcontainers).

```powershell
dotnet test Vairas.ChatBackend.slnx                  # unit + integration tests
dotnet test Vairas.ChatBackend.ServiceTests/Vairas.ChatBackend.ServiceTests.csproj   # end-to-end smoke test (not part of the solution)
dotnet test Vairas.ChatBackend.slnx --collect:"XPlat Code Coverage" --settings coverage.runsettings --results-directory TestResults   # measure coverage
```

**Coverage report:** [docs/coverage/README.md](docs/coverage/README.md) (summary, per-assembly table, least covered files); machine-readable `docs/coverage/coverage-summary.json` and `docs/coverage/index.html` (per-file table). The raw cobertura XML of a local run is written to `TestResults/`.