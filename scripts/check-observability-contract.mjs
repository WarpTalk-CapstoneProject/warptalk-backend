import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const programs = [
  "auth/src/WarpTalk.AuthService.API/Program.cs",
  "workspace/src/WarpTalk.WorkspaceService.API/Program.cs",
  "translation-room/src/WarpTalk.TranslationRoomService.API/Program.cs",
  "transcript/src/WarpTalk.TranscriptService.API/Program.cs",
  "notification/src/WarpTalk.NotificationService.API/Program.cs",
  "meeting/src/WarpTalk.MeetingService.API/Program.cs",
  "assistant/src/WarpTalk.AssistantService.API/Program.cs",
  "billing/src/WarpTalk.BillingService.API/Program.cs",
  "gateway/src/WarpTalk.Gateway/Program.cs",
];

for (const program of programs) {
  const source = await readFile(program, "utf8");
  const registrations = source.match(/AddWarpTalkObservability\s*\(/g) ?? [];
  assert.equal(
    registrations.length,
    1,
    `${program} must register shared observability exactly once`,
  );
}

const extension = await readFile(
  "shared/WarpTalk.Shared/Extensions/ObservabilityServiceCollectionExtensions.cs",
  "utf8",
);
for (const required of [
  ".WithTracing(",
  ".WithMetrics(",
  ".AddAspNetCoreInstrumentation(",
  ".AddHttpClientInstrumentation(",
  ".AddRuntimeInstrumentation(",
  "logging.AddOpenTelemetry(",
  ".AddOtlpExporter(",
  "deployment.environment.name",
]) {
  assert.ok(extension.includes(required), `missing observability wiring: ${required}`);
}

const project = await readFile(
  "shared/WarpTalk.Shared/WarpTalk.Shared.csproj",
  "utf8",
);
for (const packageName of [
  "OpenTelemetry.Exporter.OpenTelemetryProtocol",
  "OpenTelemetry.Extensions.Hosting",
  "OpenTelemetry.Instrumentation.AspNetCore",
  "OpenTelemetry.Instrumentation.Http",
  "OpenTelemetry.Instrumentation.Runtime",
]) {
  assert.ok(project.includes(packageName), `missing package: ${packageName}`);
}

console.log("Backend observability contract: PASS (9 entrypoints)");
