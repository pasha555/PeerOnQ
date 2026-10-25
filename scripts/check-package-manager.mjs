const userAgent = process.env.npm_config_user_agent ?? "";

if (!userAgent.startsWith("pnpm/")) {
  console.error("PeerOnQ requires pnpm. Do not use npm or yarn.");
  process.exit(1);
}
