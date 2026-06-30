import { Polar } from "@polar-sh/sdk";

// Initialize Polar SDK once and export for reuse
export const polarClient = new Polar({
  server: process.env.NODE_ENV === "production" ? undefined : "sandbox",
  accessToken: process.env.POLAR_ACCESS_TOKEN,
});

// Export organization ID for convenience
export const POLAR_ORGANIZATION_ID = process.env.POLAR_ORGANIZATION_ID || "";

// Validate that required environment variables are set
if (!process.env.POLAR_ACCESS_TOKEN) {
  console.warn(
    "POLAR_ACCESS_TOKEN not configured - license validation will fail",
  );
}

if (!POLAR_ORGANIZATION_ID) {
  console.warn(
    "POLAR_ORGANIZATION_ID not configured - license validation will fail",
  );
}
