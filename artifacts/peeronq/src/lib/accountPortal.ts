const defaultAccountPortalUrl = "https://portal.peeronq.com";

export function getAccountPortalUrl(): string {
  const configured = import.meta.env.VITE_PEERONQ_ACCOUNT_PORTAL_URL?.trim() || defaultAccountPortalUrl;
  const parsed = new URL(configured);
  if (parsed.protocol !== "https:") {
    throw new Error("VITE_PEERONQ_ACCOUNT_PORTAL_URL must be an HTTPS URL.");
  }
  return configured.replace(/\/$/, "");
}
