import fs from "node:fs";

const worker = fs.readFileSync(
  "notification/src/WarpTalk.NotificationService.API/HostedServices/NotificationStreamConsumerService.cs",
  "utf8",
);

const required = [
  "StreamAutoClaimAsync",
  "DeadLetterStreamName",
  "NotificationInboxMessage",
  "Environment.MachineName",
  "Environment.ProcessId",
];
const forbidden = ['ConsumerName = "worker-1"', "Mocking empty list"];

const failures = [
  ...required
    .filter((marker) => !worker.includes(marker))
    .map((marker) => `notification stream worker is missing: ${marker}`),
  ...forbidden
    .filter((marker) => worker.includes(marker))
    .map((marker) => `notification stream worker still contains: ${marker}`),
];

if (failures.length > 0) {
  console.error(failures.join("\n"));
  process.exit(1);
}

console.log("Notification stream reliability contract passed.");
