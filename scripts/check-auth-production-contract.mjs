import fs from "node:fs";

const program = fs.readFileSync(
  "auth/src/WarpTalk.AuthService.API/Program.cs",
  "utf8",
);
const authService = fs.readFileSync(
  "auth/src/WarpTalk.AuthService.Application/Services/AuthService.cs",
  "utf8",
);
const googleService = fs.readFileSync(
  "auth/src/WarpTalk.AuthService.Application/Services/GoogleAuthService.cs",
  "utf8",
);

const required = [
  ["production Redis guard", "builder.Environment.IsProduction()"],
  ["Redis distributed cache", "AddStackExchangeRedisCache"],
  ["Resend registration", "AddResendClient"],
  ["verification token hashing", "EmailVerificationTokenHash"],
  ["reset token hashing", "PasswordResetTokenHash"],
  ["real verification email", "SendVerificationEmailAsync"],
];

const failures = required
  .filter(([, marker]) => ![program, authService].some((source) => source.includes(marker)))
  .map(([name]) => `missing Auth production contract: ${name}`);

for (const forbidden of ["Simulate dispatching", "simulated verification"]) {
  if (authService.includes(forbidden) || googleService.includes(forbidden)) {
    failures.push(`forbidden Auth marker remains: ${forbidden}`);
  }
}

if (failures.length > 0) {
  console.error(failures.join("\n"));
  process.exit(1);
}

console.log("Auth production contract passed.");
