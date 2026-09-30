import fs from "node:fs";

const worker = fs.readFileSync(
  "notification/src/WarpTalk.NotificationService.API/HostedServices/NotificationStreamConsumerService.cs",
  "utf8",
);

// The worker owns the stream mechanics; the idempotent write (inbox receipt, rows and the
// announcement's delivery counters in one transaction) lives in the delivery service it calls.
const delivery = fs.readFileSync(
  "notification/src/WarpTalk.NotificationService.Application/Services/AdminNotificationDeliveryService.cs",
  "utf8",
);

const required = [
  "StreamAutoClaimAsync",
  "DeadLetterStreamName",
  "IAdminNotificationDeliveryService",
  "MarkAnnouncementFailedAsync",
  "Environment.MachineName",
  "Environment.ProcessId",
];
const requiredInDelivery = [
  "NotificationInboxMessage",
  "HasProcessedAsync",
  "BeginTransactionAsync",
  "RecordChunkDeliveredAsync",
];
const forbidden = ['ConsumerName = "worker-1"', "Mocking empty list"];

const failures = [
  ...required
    .filter((marker) => !worker.includes(marker))
    .map((marker) => `notification stream worker is missing: ${marker}`),
  ...requiredInDelivery
    .filter((marker) => !delivery.includes(marker))
    .map((marker) => `admin notification delivery service is missing: ${marker}`),
  ...forbidden
    .filter((marker) => worker.includes(marker))
    .map((marker) => `notification stream worker still contains: ${marker}`),
];

if (failures.length > 0) {
  console.error(failures.join("\n"));
  process.exit(1);
}

console.log("Notification stream reliability contract passed.");
