# Vairas.ChatBackend — code coverage

**Line coverage 89.47 %** (1130/1263 executable lines), branch coverage 67.05 % — threshold 70 % **met**. Generated 2026-11-25.

| Assembly | Line % | Lines | Branch % |
|---|---:|---:|---:|
| Vairas.ChatBackend | 82.7 | 454/549 | 57.8 |
| Vairas.ChatBackend.Business | 95.18 | 513/539 | 72.08 |
| Vairas.ChatBackend.Domain | 87.32 | 62/71 | 80.0 |
| Vairas.ChatBackend.Persistance | 97.12 | 101/104 | 91.18 |

## How it was measured

- Tool: coverlet (`XPlat Code Coverage`) driven by `Backend/local/coverage.py`; settings `Vairas.ChatBackend/coverage.runsettings` (migrations and test assemblies excluded).
- Test projects: `Vairas.ChatBackend.ServiceTests`, `Vairas.ChatBackend.Tests`; line hits of all test projects of the service are merged (maximum per line), so shared code is never counted twice.
- Filter: `TestCategory!=Broker`. Broker tests need the Service Bus emulator and are executed (and passed) separately by `local/test_all.py`.
- The numbers are measured line coverage, not a statement about functional completeness; acceptance status is tracked in `Documentation/Final-Verification/AC-Traceability-Matrix.xlsx`.

## Reproduce

```powershell
cd Backend
py -3 local/coverage.py --services Vairas.ChatBackend --filter "TestCategory!=Broker" --threshold 70
```

## Files

`coverage-summary.json` (machine-readable, per file) and `index.html` (per-file table, lowest coverage first) are in this folder.

## Least covered files

| File | Line % | Lines |
|---|---:|---:|
| `Vairas.ChatBackend.Business/IChatStore.cs` | 0.0 | 0/1 |
| `Startup/ChatProvisioning.cs` | 0.0 | 0/39 |
| `Startup/SyntheticModelProbe.cs` | 0.0 | 0/12 |
| `HealthChecks/DatabaseReadiness.cs` | 0.0 | 0/11 |
| `Vairas.ChatBackend.Domain/UnprocessableEntityException.cs` | 0.0 | 0/1 |
| `Vairas.ChatBackend.Persistance/ChatDesignFactory.cs` | 0.0 | 0/1 |
| `Vairas.ChatBackend.Domain/ChatErrors.cs` | 36.36 | 4/11 |
| `Configuration/ConfigureKestrel.cs` | 40.0 | 2/5 |
