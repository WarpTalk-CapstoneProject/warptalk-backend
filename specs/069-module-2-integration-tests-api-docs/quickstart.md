# Quickstart - WT-78 Integration Harness

## Local

```bash
cd warptalk-backend

dotnet test transcript/tests/WarpTalk.TranscriptService.IntegrationTests/WarpTalk.TranscriptService.IntegrationTests.csproj -v minimal

dotnet build transcript/WarpTalk.TranscriptService.slnx -v minimal
```

## CI Suggestion

```bash
dotnet restore transcript/tests/WarpTalk.TranscriptService.IntegrationTests/WarpTalk.TranscriptService.IntegrationTests.csproj
dotnet test transcript/tests/WarpTalk.TranscriptService.IntegrationTests/WarpTalk.TranscriptService.IntegrationTests.csproj -c Release --no-restore
```

## Notes

- The integration harness is contract-focused and validates coverage for Module 2 API/realtime flow and runtime persistence boundaries.
- Full infra end-to-end validation (Redis, gRPC peers, PostgreSQL) remains in environment-level integration pipelines.
