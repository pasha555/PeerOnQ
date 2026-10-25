import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

// Exercise Compose's real extends/override merge, without reading a developer's .env
// or starting containers. All credentials below are inert configuration fixtures.
const root = fileURLToPath(new URL("../", import.meta.url));
const deployment = join(root, "src/PeerOnQ.Infrastructure.Deployment");
const files = Object.fromEntries(
  ["development", "staging", "production"].map((name) => [
    name,
    join(deployment, `docker-compose.${name}.yml`),
  ]),
);
const fixture = {};
for (const file of Object.values(files)) {
  for (const [, key] of readFileSync(file, "utf8").matchAll(/\$\{(PEERONQ_[A-Z0-9_]+):\?/g))
    fixture[key] = "compose-contract-fixture";
}
for (const [key, host] of Object.entries({
  WEB: "peeronq.com",
  WEB_WWW: "www.peeronq.com",
  API: "api.peeronq.com",
  PORTAL: "portal.peeronq.com",
  DOWNLOAD: "download.peeronq.com",
  ADMIN: "admin.peeronq.com",
  GRAFANA: "grafana.peeronq.com",
  PROMETHEUS: "prometheus.peeronq.com",
  UPDATE: "updates.peeronq.com",
  PRESENCE: "presence.peeronq.com",
  SIGNAL: "signal.peeronq.com",
}))
  fixture[`PEERONQ_${key}_HOST`] = host;
fixture.PEERONQ_ADMIN_ALLOWED_CIDR = "192.0.2.10/32";
fixture.PEERONQ_TURN_PUBLIC_HOST = "turn.peeronq.com";
fixture.PEERONQ_TURN_EXTERNAL_IP = "192.0.2.20";
fixture.PEERONQ_PLATFORM_UPGRADE_ENABLED = "false";

const temporary = mkdtempSync(join(tmpdir(), "peeronq-compose-contract-"));
for (const key of Object.keys(fixture)) {
  if (/_(DIR|FILE|CONFIG)$/.test(key)) fixture[key] = join(temporary, key).replaceAll("\\", "/");
}
const envFile = join(temporary, "fixture.env");
const environment = Object.fromEntries(
  Object.entries(process.env).filter(([key]) => !/^(PEERONQ_|COMPOSE_)/i.test(key)),
);
function render(names, overrides = {}) {
  writeFileSync(
    envFile,
    Object.entries({ ...fixture, ...overrides })
      .map(([key, value]) => `${key}=${value}`)
      .join("\n"),
  );
  return JSON.parse(
    execFileSync(
      "docker",
      [
        "compose",
        "--env-file",
        envFile,
        ...names.flatMap((name) => ["-f", files[name]]),
        "config",
        "--format",
        "json",
      ],
      { cwd: root, env: environment, encoding: "utf8", maxBuffer: 4 * 1024 * 1024 },
    ),
  );
}

try {
  // Both omitted and explicitly inherited development ports must produce portless links.
  for (const port of [undefined, "8443", "443"]) {
    for (const names of [["staging"], ["staging", "production"]]) {
      const model = render(names, port ? { PEERONQ_HTTPS_PORT: port } : {});
      const args = model.services["web-ui"].build.args;
      assert.equal(model.services["cloud-api"].environment.PeerOnQ__CustomerPortal__EnableMfa, "false");
      assert.equal(model.services["cloud-api"].environment.PeerOnQ__CustomerPortal__Mail__FromAddress, "peeronq@peeronq.com");
      // Real SMTP must be reachable without opening the private shared networks or app ports.
      const mailNetwork = "customer-mail-egress";
      assert.ok(Object.hasOwn(model.services["cloud-api"].networks, mailNetwork), "Cloud API has no SMTP outbound route");
      assert.deepEqual(Object.keys(model.services["cloud-api"].networks).sort(), ["control", mailNetwork, "observability"].sort());
      assert.equal(Boolean(model.networks[mailNetwork].internal), false);
      assert.equal(model.networks.control.internal, true);
      assert.equal(model.networks.observability.internal, true);
      for (const name of ["postgres", "redis"]) {
        for (const network of Object.keys(model.services[name].networks)) {
          assert.equal(model.networks[network].internal, true, `${name} has an external network`);
        }
      }
      for (const [name, service] of Object.entries(model.services)) {
        if (name !== "cloud-api") assert.ok(!Object.hasOwn(service.networks ?? {}, mailNetwork), `${name} joined customer mail egress`);
      }
      assert.equal(args.VITE_PEERONQ_ACCOUNT_PORTAL_URL, "https://portal.peeronq.com");
      assert.equal(args.VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL, "https://download.peeronq.com");
      assert.equal(
        model.services["cloud-api"].environment.PeerOnQ__CustomerPortal__PortalBaseUrl,
        args.VITE_PEERONQ_ACCOUNT_PORTAL_URL,
      );
      for (const name of [
        "web-ui",
        "portal-ui",
        "cloud-api",
        "admin-ui",
        "admin-api",
        "presence",
        "downloads",
        "signaling",
        "signaling-metrics",
        "postgres",
        "redis",
        "loki",
        "tempo",
        "blackbox-exporter",
        "node-exporter",
        "alertmanager",
      ])
        assert.equal(model.services[name].ports?.length ?? 0, 0, `${name} publishes a direct port`);
      for (const name of ["grafana", "prometheus", "otel-collector"])
        for (const binding of model.services[name].ports ?? [])
          assert.equal(binding.host_ip, "127.0.0.1", `${name} publishes a non-loopback port`);
      assert.equal(model.services.proxy.environment.PEERONQ_ADMIN_ALLOWED_CIDR, "192.0.2.10/32");
      assert(
        model.services.proxy.ports.some(
          (binding) => binding.target === 443 && binding.published === "443",
        ),
      );
    }
  }
  for (const mode of ["Closed", "InvitationOnly", "Open"]) {
    for (const enabled of ["false", "true"]) {
      const model = render(["staging", "production"], {
        PEERONQ_CUSTOMER_REGISTRATION_MODE: mode,
        PEERONQ_CUSTOMER_MFA_ENABLED: enabled,
        PEERONQ_CUSTOMER_REQUIRE_EMAIL_VERIFICATION: "true",
        PEERONQ_CUSTOMER_MAIL_PROVIDER: "Smtp",
        PEERONQ_CUSTOMER_MAIL_FROM_ADDRESS: "support@example.test",
      });
      const customer = model.services["cloud-api"].environment;
      assert.equal(customer.PeerOnQ__CustomerPortal__RegistrationMode, mode);
      assert.equal(customer.PeerOnQ__CustomerPortal__EnableMfa, enabled);
      assert.equal(customer.PeerOnQ__CustomerPortal__RequireEmailVerification, "true");
      assert.equal(customer.PeerOnQ__CustomerPortal__Mail__FromAddress, "support@example.test");
      assert.equal(customer.PeerOnQ__CustomerPortal__Mail__SmtpUseTls, "true");
      assert.ok(!Object.keys(model.services["admin-api"].environment).some((key) => key.includes("CustomerPortal")));
    }
  }
  const development = render(["development"], {
    PEERONQ_PORTAL_HOST: "portal.dev.localhost",
    PEERONQ_DOWNLOAD_HOST: "download.dev.localhost",
    PEERONQ_HTTPS_PORT: "8443",
  });
  assert.deepEqual(Object.keys(development.services["cloud-api"].networks).sort(), ["control", "observability"]);
  assert.equal(
    development.services["web-ui"].build.args.VITE_PEERONQ_ACCOUNT_PORTAL_URL,
    "https://portal.dev.localhost:8443",
  );
  assert.equal(
    development.services["web-ui"].build.args.VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL,
    "https://download.dev.localhost:8443",
  );
  console.log(
    "PASS: merged HTTPS URLs, private ports and isolated Cloud API mail egress; development unchanged.",
  );
} finally {
  rmSync(temporary, { recursive: true, force: true });
}
