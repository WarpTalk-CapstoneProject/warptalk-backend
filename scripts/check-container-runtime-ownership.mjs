import fs from "node:fs";

const dockerfiles = [
  "auth/Dockerfile",
  "workspace/Dockerfile",
  "translation-room/Dockerfile",
  "transcript/Dockerfile",
  "notification/Dockerfile",
  "meeting/Dockerfile",
  "assistant/Dockerfile",
  "billing/Dockerfile",
  "gateway/src/WarpTalk.Gateway/Dockerfile",
];

const expectedCopy =
  "COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .";
const failures = [];

for (const dockerfile of dockerfiles) {
  const source = fs.readFileSync(dockerfile, "utf8");
  if (!source.includes(expectedCopy)) {
    failures.push(
      `${dockerfile}: published runtime files are not owned by the non-root app user`,
    );
  }
  if (!source.includes("USER $APP_UID")) {
    failures.push(`${dockerfile}: runtime does not select the non-root app user`);
  }
}

if (failures.length > 0) {
  console.error(failures.join("\n"));
  process.exit(1);
}

console.log(
  `Container runtime ownership validation passed: ${dockerfiles.length} Dockerfiles.`,
);
