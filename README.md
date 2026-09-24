# ColdRefresh

ColdRefresh is a safety-first Windows 11 x64 design for refreshing cold data on NTFS SSDs by eventually rewriting the same verified bytes at the same offsets. **This foundation does not perform production file rewriting.** It establishes the transaction model, fault-injectable contracts, documentation, tests, Windows boundary, and minimal WinUI 3 shell first.

The default policy is a 365-day `LastWriteTime` threshold, 64 MiB chunks, concurrency one, and SHA-256 full-file verification. Unsupported or uncertain files are skipped. The recovery journal should be placed on a different physical disk; same-disk mode is allowed only with an explicit reduced-protection warning.

## Build (Windows 11)

Install the .NET 10 SDK and Windows App SDK prerequisites, then run:

```powershell
dotnet restore ColdRefresh.slnx
dotnet build ColdRefresh.slnx --configuration Release --no-restore
dotnet test ColdRefresh.slnx --configuration Release --no-build
```

Read [the architecture](docs/ARCHITECTURE.md), [safety model](docs/SAFETY_MODEL.md), [failure catalogue](docs/FAILURE_MODES.md), and [journal specification](docs/RECOVERY_JOURNAL.md) before changing transaction code.
