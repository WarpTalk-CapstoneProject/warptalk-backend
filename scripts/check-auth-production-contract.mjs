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

// --- Self-registration must not auto-verify an address nobody proved they own. ---
// This defaulted to true and was set in no config file at all, which silently disabled the
// spec-137 Safe Matching Rule in GoogleAuthService everywhere including production. The default
// and the explicit setting are both pinned, because either one drifting re-opens it.
const authSettings = fs.readFileSync(
  "auth/src/WarpTalk.AuthService.Domain/Settings/AuthSettings.cs",
  "utf8",
);

if (!/AutoVerifySelfRegistration\s*{\s*get;\s*set;\s*}\s*=\s*false\s*;/.test(authSettings)) {
  failures.push(
    "AuthSettings.AutoVerifySelfRegistration must default to false: true marks a self-registered " +
      "address verified without the user proving they control it, which disables the spec-137 " +
      "anti-takeover guard in GoogleAuthService.",
  );
}

const authAppSettings = JSON.parse(
  fs.readFileSync("auth/src/WarpTalk.AuthService.API/appsettings.json", "utf8"),
);

if (authAppSettings?.AuthSettings?.AutoVerifySelfRegistration !== false) {
  failures.push(
    "appsettings.json must set AuthSettings.AutoVerifySelfRegistration explicitly to false, " +
      "rather than inheriting it. It being absent is how this shipped switched on.",
  );
}

// --- Google sign-in must never accept a credential it cannot attribute to our OAuth client. ---
// The userinfo endpoint honours an access token from ANY Google app, so reaching it without
// first proving provenance via tokeninfo is account takeover with no credential.
const googleVerifier = fs.readFileSync(
  "auth/src/WarpTalk.AuthService.Infrastructure/Security/GoogleTokenVerifier.cs",
  "utf8",
);

if (!googleVerifier.includes("TokenInfoEndpoint")) {
  failures.push(
    "GoogleTokenVerifier must gate the access-token path on Google's tokeninfo endpoint. " +
      "userinfo alone accepts a token minted by any OAuth client, including an attacker's own app.",
  );
}

if (!/!IsOurClient\(audience\)\s*&&\s*!IsOurClient\(authorizedParty\)/.test(googleVerifier)) {
  failures.push(
    "GoogleTokenVerifier must reject an access token whose aud/azp is not our configured client id.",
  );
}

// A bare `catch` with no exception filter around the ID-token branch is exactly how the
// takeover fallthrough was written. Any catch here must name what it is catching.
if (/catch\s*\(\s*Exception\s*\)\s*\n/.test(googleVerifier)) {
  failures.push(
    "GoogleTokenVerifier must not contain an unfiltered `catch (Exception)`: that is how a " +
      "failed ID-token validation silently fell through to the unverified userinfo path.",
  );
}

if (failures.length > 0) {
  console.error(failures.join("\n"));
  process.exit(1);
}

console.log("Auth production contract passed.");
