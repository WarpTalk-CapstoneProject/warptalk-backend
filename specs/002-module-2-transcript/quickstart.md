# Quickstart: Module 2 Backend

## 1. Prerequisites
- Docker & Docker Compose
- .NET 9 SDK
- Git

## 2. Running Local Infrastructure
Start the required databases and message broker:

```bash
cd warptalk-infrastructure
docker-compose -f docker-compose.yml -f docker-compose.override.yml up -d redis pgbouncer postgres
```

## 3. Running TranscriptService
Navigate to the transcript service API and run:

```bash
cd warptalk-backend/transcript/src/WarpTalk.TranscriptService.API
dotnet restore
dotnet run
```

## 4. Running GatewayService
To test the real-time SignalR endpoints alongside Redis, start the Gateway:

```bash
cd warptalk-backend/gateway/src/WarpTalk.Gateway
dotnet run
```

## 5. Simulating AI Worker
You can simulate the AI worker pushing STT results to Redis Streams via CLI:

```bash
redis-cli
127.0.0.1:6379> XADD stt:results:{roomId} * segmentId "uuid1" participantId "uuid2" text "Hello world"
```
You should see the TranscriptService console log indicating the segment was consumed and persisted.
