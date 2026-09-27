namespace PeerOnQ.Observability.Tests;

public sealed class DeploymentConfigurationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void DevelopmentCompose_UsesSupportedRedisAndSingleAspNetBindingSetting()
    {
        var compose = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml");

        Assert.Contains("image: redis:8.4.4-alpine", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("redis:8.2.1-alpine", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("ASPNETCORE_URLS", compose, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_HTTP_PORTS: \"8080\"", compose, StringComparison.Ordinal);
        Assert.Contains("PEERONQ_TURN_REQUIRE_TLS: \"true\"", compose, StringComparison.Ordinal);
        Assert.Contains(":5349:5349/tcp", compose, StringComparison.Ordinal);

        foreach (var dockerfile in new[]
                 {
                     "src/PeerOnQ.Admin.Api/Dockerfile",
                     "src/PeerOnQ.Cloud.Api/Dockerfile",
                     "src/PeerOnQ.Downloads.Service/Dockerfile",
                     "src/PeerOnQ.Presence.Server/Dockerfile",
                     "src/PeerOnQ.Signaling.Server/Dockerfile",
                 })
        {
            var contents = Read(dockerfile);
            Assert.DoesNotContain("ASPNETCORE_URLS", contents, StringComparison.Ordinal);
            Assert.Contains("ASPNETCORE_HTTP_PORTS=8080", contents, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Compose_ReservesTheOpenTelemetryAddressOutsideTheDynamicPool()
    {
        var development = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml");
        var staging = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml");
        var ha = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.ha.yml");

        foreach (var compose in new[] { development, staging })
        {
            Assert.Contains(
                "- subnet: 172.29.61.0/24\n          ip_range: 172.29.61.128/25",
                Normalize(compose),
                StringComparison.Ordinal);
        }

        Assert.Contains("ipv4_address: 172.29.61.10", development, StringComparison.Ordinal);
        Assert.Contains("fluentd-address: 172.29.61.10:24224", development, StringComparison.Ordinal);
        Assert.Contains("fluentd-address: 172.29.61.10:24225", development, StringComparison.Ordinal);
        Assert.Contains("fluentd-address: 172.29.61.10:24224", ha, StringComparison.Ordinal);
        Assert.DoesNotContain("172.29.61.11", development, StringComparison.Ordinal);
        Assert.DoesNotContain("172.29.61.11", ha, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenTelemetryConfiguration_UsesCurrentComponentNames()
    {
        var config = Read("src/PeerOnQ.Infrastructure.Deployment/observability/otel-collector.yml");

        Assert.Contains("fluent_forward/signaling:", config, StringComparison.Ordinal);
        Assert.Contains("fluent_forward/coturn:", config, StringComparison.Ordinal);
        Assert.Contains("otlp_grpc/tempo:", config, StringComparison.Ordinal);
        Assert.Contains("otlp_http/loki:", config, StringComparison.Ordinal);
        Assert.DoesNotContain("fluentforward/", config, StringComparison.Ordinal);
        Assert.DoesNotContain("otlphttp/", config, StringComparison.Ordinal);
    }

    [Fact]
    public void DevelopmentCompose_AssignsStableDnsAliasesToObservabilityServices()
    {
        var compose = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml");

        foreach (var serviceName in new[]
                 {
                     "otel-collector",
                     "prometheus",
                     "alertmanager",
                     "blackbox-exporter",
                     "node-exporter",
                     "loki",
                     "tempo",
                     "grafana",
                 })
        {
            var service = ExtractService(compose, serviceName);
            Assert.Contains("observability:", service, StringComparison.Ordinal);
            Assert.Contains($"aliases: [{serviceName}]", service, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TempoConfiguration_DisablesAnonymousUsageReporting()
    {
        var config = Read("src/PeerOnQ.Infrastructure.Deployment/observability/tempo.yml");

        Assert.Contains("usage_report:\n  reporting_enabled: false", Normalize(config), StringComparison.Ordinal);
    }

    [Fact]
    public void TurnConfiguration_EnablesTlsOnlyWhenCertificatesExistAndUsesWritablePidPath()
    {
        var baseline = Read("src/PeerOnQ.Turn.Configuration/turnserver.conf");
        var entrypoint = Read("src/PeerOnQ.Turn.Configuration/docker-entrypoint.sh");

        Assert.DoesNotContain("tls-listening-port=", baseline, StringComparison.Ordinal);
        Assert.Contains("pidfile=/tmp/turnserver.pid", baseline, StringComparison.Ordinal);
        Assert.Contains("--tls-listening-port=5349", entrypoint, StringComparison.Ordinal);
    }

    [Fact]
    public void GrafanaConfiguration_ProvisionsRealOperationsDashboard()
    {
        var compose = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml");
        var staging = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml");
        var provider = Read("src/PeerOnQ.Infrastructure.Deployment/observability/grafana/provisioning/dashboards/dashboards.yml");
        var dashboard = Read("src/PeerOnQ.Infrastructure.Deployment/observability/grafana/dashboards/peeronq-operations.json");

        Assert.Contains(
            "GF_SERVER_ROOT_URL: https://${PEERONQ_GRAFANA_HOST:-grafana.dev.localhost}:${PEERONQ_HTTPS_PORT:-8443}/",
            compose,
            StringComparison.Ordinal);
        Assert.Contains(
            "GF_SERVER_ROOT_URL: https://${PEERONQ_GRAFANA_HOST:?set PEERONQ_GRAFANA_HOST}/",
            staging,
            StringComparison.Ordinal);
        Assert.Contains("/var/lib/grafana/dashboards", provider, StringComparison.Ordinal);
        Assert.Contains("PeerOnQ Operations", dashboard, StringComparison.Ordinal);
        Assert.Contains("peeronq_online_devices", dashboard, StringComparison.Ordinal);
        Assert.Contains("peeronq_active_sessions", dashboard, StringComparison.Ordinal);
        Assert.Contains("probe_success", dashboard, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsDevelopmentController_ManagesOperationsSecretsOutsideTheEnvironmentFile()
    {
        var controller = Read("scripts/windows/peeronq-phase6-dev.ps1");

        Assert.Contains("Initialize-LocalOperationsArtifacts", controller, StringComparison.Ordinal);
        Assert.Contains("$GrafanaPasswordFile", controller, StringComparison.Ordinal);
        Assert.Contains("$PostgresBackupPgpassFile", controller, StringComparison.Ordinal);
        Assert.Contains("$PostgresRestorePgpassFile", controller, StringComparison.Ordinal);
        Assert.Contains("$env:ComSpec /d /c", controller, StringComparison.Ordinal);
        Assert.Contains("--password-from-stdin", controller, StringComparison.Ordinal);
        Assert.Contains("-not ($output -match 'successfully')", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("reset-admin-password $password", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsWorkspaceController_StartsDockerBoundedlyAndOpensEveryDevelopmentPanel()
    {
        var workspace = Read("scripts/windows/peeronq-workspace-dev.ps1");
        var phase3 = Read("scripts/windows/peeronq-phase3-local.ps1");
        var phase6 = Read("scripts/windows/peeronq-phase6-dev.ps1");
        var preview = Read("scripts/windows/peeronq-dev.ps1");
        var certificateGenerator = Read("scripts/windows/PeerOnQ.LocalCertificate.cs");
        var dockerIgnore = Read(".dockerignore");

        Assert.Contains("Ensure-DockerReady", workspace, StringComparison.Ordinal);
        Assert.Contains("Docker Desktop.exe", workspace, StringComparison.Ordinal);
        Assert.Contains("WaitForExit($timeoutMilliseconds)", workspace, StringComparison.Ordinal);
        Assert.Contains("-ConfigureFirewall", workspace, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5555/", workspace, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5555/desktop-preview", workspace, StringComparison.Ordinal);
        Assert.Contains("https://admin.dev.localhost:$httpsPort/", workspace, StringComparison.Ordinal);
        Assert.Contains("https://portal.dev.localhost:$httpsPort/", workspace, StringComparison.Ordinal);
        Assert.Contains("https://grafana.dev.localhost:$httpsPort/", workspace, StringComparison.Ordinal);
        Assert.Contains("http://localhost:$prometheusPort/", workspace, StringComparison.Ordinal);
        Assert.Contains("WaitForExit($timeoutMilliseconds)", phase3, StringComparison.Ordinal);
        Assert.Contains("Signaling__Attestation__AllowDevelopmentTofuFallback", phase3, StringComparison.Ordinal);
        Assert.Contains("WaitForExit($timeoutMilliseconds)", phase6, StringComparison.Ordinal);
        Assert.Contains("Repair-StaleDevelopmentSecretMounts", phase6, StringComparison.Ordinal);
        Assert.Contains("Stop-RemainingProjectContainers", phase6, StringComparison.Ordinal);
        Assert.Contains("label=com.docker.compose.project", phase6, StringComparison.Ordinal);
        Assert.Contains("PEERONQ_SIGNALING_ATTESTATION_PUBLIC_KEY_FILE", phase6, StringComparison.Ordinal);
        Assert.Contains("Wait-ForUrl 'Grafana' \"$GrafanaUrl/api/health\"", phase6, StringComparison.Ordinal);
        Assert.Contains("Wait-ForUrl 'Prometheus' \"$PrometheusUrl/-/ready\"", phase6, StringComparison.Ordinal);
        Assert.Contains(
            "$env:VITE_PEERONQ_GRAFANA_URL = \"https://grafana.dev.localhost:$phase6HttpsPort\"",
            preview,
            StringComparison.Ordinal);
        Assert.Contains("ECCurve.NamedCurves.nistP256", certificateGenerator, StringComparison.Ordinal);
        Assert.Contains("app-updates", dockerIgnore, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase6Acceptance_UsesTheCurrentSignalingProtocol()
    {
        var acceptance = Read("src/PeerOnQ.Infrastructure.Deployment/acceptance/Program.cs");
        var project = Read("src/PeerOnQ.Infrastructure.Deployment/acceptance/PeerOnQ.Phase6.Acceptance.csproj");

        Assert.Contains("protocolVersion = SignalingProtocol.CurrentVersion", acceptance, StringComparison.Ordinal);
        Assert.Contains("clientCapabilities = new", acceptance, StringComparison.Ordinal);
        Assert.Contains("capabilities = Array.Empty<string>()", acceptance, StringComparison.Ordinal);
        Assert.Contains("SignalingServerCapabilityNames.AuthenticatedRegistration", acceptance, StringComparison.Ordinal);
        Assert.Contains("PeerOnQ.Transport.csproj", project, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase7CustomerPortal_HasIsolatedStorageAndLeastPrivilegeDatabaseAccess()
    {
        var compose = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml");
        var permissions = Read("src/PeerOnQ.Infrastructure.Deployment/postgres/apply-runtime-permissions.sql");

        Assert.Contains("portal-ui:", compose, StringComparison.Ordinal);
        Assert.Contains("customer-storage-init: { condition: service_completed_successfully }", compose, StringComparison.Ordinal);
        Assert.Contains("chmod 0700 /customer-data-protection /customer-mail", compose, StringComparison.Ordinal);
        Assert.Contains("\"CustomerAccounts\", \"CustomerSessions\", \"CustomerAccountTokens\"", permissions, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT, INSERT ON TABLE \"CustomerSecurityEvents\" TO peeronq_cloud_runtime", permissions, StringComparison.Ordinal);
        Assert.Contains("has_table_privilege('peeronq_admin_runtime', '\"CustomerAccounts\"', 'SELECT')", permissions, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionContainerInputs_ArePinnedToImmutableDigests()
    {
        var production = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.production.yml");
        var productionImages = production.Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("image: ", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(productionImages);
        Assert.All(productionImages, image =>
            Assert.Matches("^image: [a-z0-9./_-]+:[A-Za-z0-9._-]+@sha256:[0-9a-f]{64}$", image));

        foreach (var dockerfile in new[]
                 {
                     "artifacts/peeronq/Dockerfile",
                     "artifacts/peeronq-admin/Dockerfile",
                     "artifacts/peeronq-portal/Dockerfile",
                     "src/PeerOnQ.Admin.Api/Dockerfile",
                     "src/PeerOnQ.Cloud.Api/Dockerfile",
                     "src/PeerOnQ.Downloads.Service/Dockerfile",
                     "src/PeerOnQ.Infrastructure.Deployment/Dockerfile.migrations",
                     "src/PeerOnQ.Presence.Server/Dockerfile",
                     "src/PeerOnQ.Signaling.Server/Dockerfile",
                     "src/PeerOnQ.Turn.Configuration/Dockerfile",
                 })
        {
            var fromLines = Read(dockerfile).Split('\n', StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith("FROM ", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(fromLines);
            Assert.All(fromLines, line => Assert.Contains("@sha256:", line, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void SingleRegionHaOverlay_AddsActiveActiveSignalingWithoutFalseDataTierHaClaims()
    {
        var ha = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.ha.yml");
        var nginx = Read("src/PeerOnQ.Infrastructure.Deployment/nginx/peeronq.conf.template");
        var proxyParameters = Read("src/PeerOnQ.Infrastructure.Deployment/nginx/proxy_params");
        var cloudTargets = Read("src/PeerOnQ.Infrastructure.Deployment/observability/cloud-api-targets-ha.json");
        var presenceTargets = Read("src/PeerOnQ.Infrastructure.Deployment/observability/presence-targets-ha.json");
        var downloadTargets = Read("src/PeerOnQ.Infrastructure.Deployment/observability/downloads-targets-ha.json");

        Assert.Contains("cloud-api-ha-2:", ha, StringComparison.Ordinal);
        Assert.Contains("presence-ha-2:", ha, StringComparison.Ordinal);
        Assert.Contains("downloads-ha-2:", ha, StringComparison.Ordinal);
        Assert.Contains("signaling-ha-2:", ha, StringComparison.Ordinal);
        Assert.Contains("server cloud-api-ha-2:8080 resolve max_fails=1 fail_timeout=10s backup;", ha, StringComparison.Ordinal);
        Assert.Contains("server presence-ha-2:8080 resolve max_fails=1 fail_timeout=10s backup;", ha, StringComparison.Ordinal);
        Assert.Contains("server downloads-ha-2:8080 resolve max_fails=1 fail_timeout=10s backup;", ha, StringComparison.Ordinal);
        Assert.Contains("downloads-verified-cache-ha-2:/var/lib/peeronq/download-cache", ha, StringComparison.Ordinal);
        Assert.Contains("Signaling__Cluster__InstanceId: signaling-ha-1", ha, StringComparison.Ordinal);
        Assert.Contains("Signaling__Cluster__InstanceId: signaling-ha-2", ha, StringComparison.Ordinal);
        Assert.Contains("server signaling-ha-2:8080 resolve max_fails=1 fail_timeout=10s;", ha, StringComparison.Ordinal);
        Assert.Contains("signaling-data-ha-2:/app/data", ha, StringComparison.Ordinal);
        Assert.Contains("signaling-targets-ha.json:/etc/prometheus/targets/signaling.json:ro", ha, StringComparison.Ordinal);
        Assert.Contains("cloud-api-targets-ha.json:/etc/prometheus/targets/cloud-api.json:ro", ha, StringComparison.Ordinal);
        Assert.Contains("presence-targets-ha.json:/etc/prometheus/targets/presence.json:ro", ha, StringComparison.Ordinal);
        Assert.Contains("downloads-targets-ha.json:/etc/prometheus/targets/downloads.json:ro", ha, StringComparison.Ordinal);
        Assert.Contains("cloud-api-ha-2:8080", cloudTargets, StringComparison.Ordinal);
        Assert.Contains("presence-ha-2:8080", presenceTargets, StringComparison.Ordinal);
        Assert.Contains("downloads-ha-2:8080", downloadTargets, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres-ha-2:", ha, StringComparison.Ordinal);
        Assert.DoesNotContain("redis-ha-2:", ha, StringComparison.Ordinal);
        Assert.Contains("least_conn;", nginx, StringComparison.Ordinal);
        Assert.Contains("${PEERONQ_CLOUD_API_UPSTREAMS}", nginx, StringComparison.Ordinal);
        Assert.Contains("${PEERONQ_SIGNALING_UPSTREAMS}", nginx, StringComparison.Ordinal);
        Assert.Contains("location = /health/ready { proxy_pass http://cloud_api/health/ready", nginx, StringComparison.Ordinal);
        Assert.Contains("location = /health/ready { proxy_pass http://presence_server/health/ready", nginx, StringComparison.Ordinal);
        Assert.Contains("location = /health/ready { proxy_pass http://downloads_service/health/ready", nginx, StringComparison.Ordinal);
        Assert.Contains("proxy_next_upstream_tries 3;", proxyParameters, StringComparison.Ordinal);
    }

    [Fact]
    public void SignalingCluster_UsesItsOwnRedisAclNamespaceAndGeneratedLocalSecret()
    {
        var compose = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml");
        var redisEntrypoint = Read("src/PeerOnQ.Infrastructure.Deployment/redis/entrypoint.sh");
        var aclSmoke = Read("src/PeerOnQ.Infrastructure.Deployment/acceptance/redis-acl-smoke.sh");
        var controller = Read("scripts/windows/peeronq-phase6-dev.ps1");
        var phase9Harness = Read("scripts/windows/test-phase9-single-region-ha.ps1");
        var bootstrap = Read("scripts/linux/bootstrap-peeronq-production.sh");
        var installer = Read("scripts/linux/peeronq-server-installer.sh");

        Assert.Contains("Signaling__Cluster__Enabled: \"true\"", compose, StringComparison.Ordinal);
        Assert.Contains("user=peeronq_signaling,password=${PEERONQ_REDIS_SIGNALING_PASSWORD", compose, StringComparison.Ordinal);
        Assert.Contains("redis: { condition: service_healthy }", compose, StringComparison.Ordinal);
        Assert.Contains("user peeronq_signaling on", redisEntrypoint, StringComparison.Ordinal);
        Assert.Contains("~peeronq:{signaling}:*", redisEntrypoint, StringComparison.Ordinal);
        Assert.Contains("&peeronq:{signaling}:*", redisEntrypoint, StringComparison.Ordinal);
        Assert.Contains("expect_denied signaling GET \"$token_key\"", aclSmoke, StringComparison.Ordinal);
        Assert.Contains("expect_denied cloud GET \"$signaling_key\"", aclSmoke, StringComparison.Ordinal);
        Assert.Contains("Initialize-LocalSignalingRedisSecret", controller, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::ReadAllLines($EnvironmentFile)", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("[IO.File]::ReadLines($EnvironmentFile)", controller, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::ReadAllLines($EnvironmentFile)", phase9Harness, StringComparison.Ordinal);
        Assert.DoesNotContain("[IO.File]::ReadLines($EnvironmentFile)", phase9Harness, StringComparison.Ordinal);
        Assert.Contains("PEERONQ_REDIS_SIGNALING_PASSWORD=$redis_signaling", bootstrap, StringComparison.Ordinal);
        Assert.Contains("PEERONQ_SIGNALING_INSTANCE_ID=signaling-$REGION-1", bootstrap, StringComparison.Ordinal);
        Assert.Contains("openssl rand -hex 32", installer, StringComparison.Ordinal);
        Assert.Contains("append_environment_values PEERONQ_REDIS_SIGNALING_PASSWORD", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionIngress_KeepsAdminRestrictedAndBootstrapsEveryPublicHost()
    {
        var bootstrap = Read("scripts/linux/bootstrap-peeronq-production.sh");
        var development = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml");
        var staging = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml");
        var nginx = Read("src/PeerOnQ.Infrastructure.Deployment/nginx/peeronq.conf.template");

        Assert.Contains("PEERONQ_PORTAL_HOST=portal.$BASE_DOMAIN", bootstrap, StringComparison.Ordinal);
        Assert.Contains("PEERONQ_HTTPS_PORT=443", bootstrap, StringComparison.Ordinal);
        Assert.Contains("PEERONQ_HTTPS_BIND_ADDRESS=0.0.0.0", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("PEERONQ_HTTPS_PORT=8443", bootstrap, StringComparison.Ordinal);
        Assert.Contains("-d \"portal.$BASE_DOMAIN\"", bootstrap, StringComparison.Ordinal);
        Assert.Contains("PEERONQ_PORTAL_HOST: ${PEERONQ_PORTAL_HOST:?set PEERONQ_PORTAL_HOST}", staging, StringComparison.Ordinal);
        Assert.Contains("${PEERONQ_HTTPS_BIND_ADDRESS:-127.0.0.1}:${PEERONQ_HTTPS_PORT:-8443}:443", development, StringComparison.Ordinal);
        Assert.Contains("- \"${PEERONQ_HTTPS_BIND_ADDRESS:-0.0.0.0}:443:443\"", staging, StringComparison.Ordinal);
        Assert.Contains("- \"127.0.0.1:${PEERONQ_PROMETHEUS_PORT:-9090}:9090\"", staging, StringComparison.Ordinal);
        Assert.Contains("- \"127.0.0.1:${PEERONQ_GRAFANA_PORT:-3000}:3000\"", staging, StringComparison.Ordinal);
        Assert.Contains("customer-data-protection:", staging, StringComparison.Ordinal);
        Assert.Contains("customer-mail:", staging, StringComparison.Ordinal);
        foreach (var operatorHost in new[] { "PEERONQ_ADMIN_HOST", "PEERONQ_GRAFANA_HOST", "PEERONQ_PROMETHEUS_HOST" })
        {
            Assert.Contains(
                $"server_name ${{{operatorHost}}};\n  allow ${{PEERONQ_ADMIN_ALLOWED_CIDR}};\n  deny all;",
                Normalize(nginx),
                StringComparison.Ordinal);
        }

        var portal = Assert.Single(Normalize(nginx).Split("\nserver {", StringSplitOptions.None),
            block => block.Contains("server_name ${PEERONQ_PORTAL_HOST};", StringComparison.Ordinal));
        Assert.DoesNotContain("PEERONQ_ADMIN_ALLOWED_CIDR", portal, StringComparison.Ordinal);
        Assert.DoesNotContain("deny all;", portal, StringComparison.Ordinal);
        Assert.Contains("listen 443 ssl;", portal, StringComparison.Ordinal);
        Assert.Contains("ssl_protocols TLSv1.2 TLSv1.3;", portal, StringComparison.Ordinal);
        Assert.Contains("location /portal/v1/ { proxy_pass http://cloud_api; include /etc/nginx/proxy_params; }", portal, StringComparison.Ordinal);
        Assert.Contains("location / { proxy_pass http://portal_ui; include /etc/nginx/proxy_params; }", portal, StringComparison.Ordinal);
        Assert.Contains("server_name ${PEERONQ_WEB_HOST} ${PEERONQ_WEB_WWW_HOST};", nginx, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformUpgrade_KeepsAdminSeparatedFromTheRootUpdater()
    {
        var development = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml");
        var staging = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml");
        var production = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.production.yml");
        var nginx = Normalize(Read("src/PeerOnQ.Infrastructure.Deployment/nginx/peeronq.conf.template"));
        var service = Read("scripts/linux/peeronq-platform-upgrade-agent.service");
        var pathUnit = Read("scripts/linux/peeronq-platform-upgrade-agent.path");
        var agent = Read("scripts/linux/peeronq-platform-upgrade-agent.sh");
        var installer = Read("scripts/linux/peeronq-server-installer.sh");
        var builder = Read("scripts/windows/build-peeronq-server-run.ps1");
        var deploymentGuide = Read("docs/DEPLOYMENT.md");
        var developmentAdmin = ExtractService(development, "admin-api");
        var stagingAdmin = ExtractService(staging, "admin-api");

        Assert.Contains("PeerOnQ__PlatformUpgrade__Enabled: \"false\"", developmentAdmin, StringComparison.Ordinal);
        Assert.Contains("PeerOnQ__PlatformUpgrade__RequestSpoolDirectory: /var/lib/peeronq/platform-upgrade/inbox", developmentAdmin, StringComparison.Ordinal);
        Assert.Contains("PeerOnQ__PlatformUpgrade__StatusDirectory: /var/lib/peeronq/platform-upgrade/status", developmentAdmin, StringComparison.Ordinal);
        Assert.Contains("/tmp:size=272m,mode=1777", developmentAdmin, StringComparison.Ordinal);
        Assert.DoesNotContain("platform-upgrade/inbox", string.Join('\n', developmentAdmin.Split('\n').Where(line => line.TrimStart().StartsWith("- ", StringComparison.Ordinal))), StringComparison.Ordinal);

        Assert.Contains("PeerOnQ__PlatformUpgrade__RequestSpoolDirectory: /var/lib/peeronq/platform-upgrade/inbox", stagingAdmin, StringComparison.Ordinal);
        Assert.Contains("/platform-upgrade/inbox", stagingAdmin, StringComparison.Ordinal);
        Assert.Contains("/platform-upgrade/status:ro", stagingAdmin, StringComparison.Ordinal);
        Assert.DoesNotContain("PEERONQ_PLATFORM_UPGRADE_INBOX_DIR", stagingAdmin, StringComparison.Ordinal);
        Assert.DoesNotContain("PEERONQ_PLATFORM_UPGRADE_STATUS_DIR", stagingAdmin, StringComparison.Ordinal);
        Assert.DoesNotContain("docker.sock", stagingAdmin, StringComparison.Ordinal);
        Assert.DoesNotContain("/platform-upgrade/processing", stagingAdmin, StringComparison.Ordinal);
        Assert.DoesNotContain("/platform-upgrade/archive", stagingAdmin, StringComparison.Ordinal);
        Assert.DoesNotContain("/var/log/peeronq", stagingAdmin, StringComparison.Ordinal);
        Assert.Contains("PeerOnQ__PlatformUpgrade__Enabled: ${PEERONQ_PLATFORM_UPGRADE_ENABLED:?set PEERONQ_PLATFORM_UPGRADE_ENABLED}", production, StringComparison.Ordinal);

        Assert.Contains("location = /admin/v1/platform-upgrades/stage {\n    client_max_body_size 260m;\n    proxy_request_buffering off;", nginx, StringComparison.Ordinal);
        Assert.Contains("PathChanged=/var/lib/peeronq/platform-upgrade/inbox", pathUnit, StringComparison.Ordinal);
        Assert.Contains("ProtectSystem=strict", service, StringComparison.Ordinal);
        Assert.Contains("PrivateNetwork=true", service, StringComparison.Ordinal);
        Assert.Contains("Restart=on-failure", service, StringComparison.Ordinal);
        Assert.Contains("RestartSec=30s", service, StringComparison.Ordinal);
        Assert.Contains("/usr/local/libexec /etc/systemd/system", service, StringComparison.Ordinal);
        Assert.DoesNotContain("docker.sock", service, StringComparison.Ordinal);
        Assert.Contains("--platform-upgrade-keyring", installer, StringComparison.Ordinal);
        Assert.Contains("--platform-upgrade-signer-fingerprint", installer, StringComparison.Ordinal);
        Assert.Contains("must not contain secret-key material", installer, StringComparison.Ordinal);
        Assert.Contains("chmod 1730 \"$PLATFORM_UPGRADE_ROOT/inbox\"", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("INVOCATION_ID", installer, StringComparison.Ordinal);
        Assert.Contains("agent_source=\"$release_path/scripts/linux/peeronq-platform-upgrade-agent.sh\"", installer, StringComparison.Ordinal);
        Assert.Contains("service_source=\"$release_path/scripts/linux/peeronq-platform-upgrade-agent.service\"", installer, StringComparison.Ordinal);
        Assert.Contains("path_source=\"$release_path/scripts/linux/peeronq-platform-upgrade-agent.path\"", installer, StringComparison.Ordinal);
        Assert.Contains("atomic_install_root_file \"$transaction/agent\"", installer, StringComparison.Ordinal);
        Assert.Contains("atomic_install_root_file \"$transaction/service\"", installer, StringComparison.Ordinal);
        Assert.Contains("atomic_install_root_file \"$transaction/path\"", installer, StringComparison.Ordinal);
        Assert.Contains("systemctl start --no-block peeronq-platform-upgrade-agent.service", installer, StringComparison.Ordinal);
        Assert.Contains("start retained previous application release", installer, StringComparison.Ordinal);
        Assert.Contains("up -d --build --wait --wait-timeout 300 --remove-orphans", installer, StringComparison.Ordinal);
        Assert.Contains("compose \"$old_current\" up -d --build --wait --wait-timeout 300 --remove-orphans", installer, StringComparison.Ordinal);
        Assert.Contains("scripts/linux/peeronq-platform-upgrade-agent.sh", builder, StringComparison.Ordinal);
        Assert.Contains("Move-Item -LiteralPath $workingOutputPath -Destination $outputPath", builder, StringComparison.Ordinal);
        Assert.Contains("exceeds the 256 MiB Admin platform-upgrade limit", builder, StringComparison.Ordinal);
        Assert.Contains("sync -f \"$temp_status\"", agent, StringComparison.Ordinal);
        Assert.Contains("[ \"$state\" = \"succeeded\" ]", agent, StringComparison.Ordinal);
        Assert.Contains("initialize_operation_log \"$operation_id\"", agent, StringComparison.Ordinal);
        Assert.Contains("request rejected: claimed file set was unsafe or non-canonical", agent, StringComparison.Ordinal);
        Assert.Contains("printf '\"schemaVersion\":1,'", agent, StringComparison.Ordinal);
        var terminalCleanup = agent.LastIndexOf("rm -rf \"$CLAIMED_DIR\"", StringComparison.Ordinal);
        var terminalGateRelease = agent.LastIndexOf("release_active_request \"$OPERATION_ID\" locked", StringComparison.Ordinal);
        Assert.True(terminalCleanup >= 0 && terminalGateRelease > terminalCleanup);

        var stageStart = agent.IndexOf("process_stage() {", StringComparison.Ordinal);
        var applyStart = agent.IndexOf("process_apply() {", stageStart, StringComparison.Ordinal);
        var rollbackStart = agent.IndexOf("process_rollback() {", applyStart, StringComparison.Ordinal);
        Assert.True(stageStart >= 0 && applyStart > stageStart && rollbackStart > applyStart);
        Assert.DoesNotContain("run_installer", agent[stageStart..applyStart], StringComparison.Ordinal);
        Assert.Contains("run_installer \"$bundle\" preflight", agent[applyStart..rollbackStart], StringComparison.Ordinal);

        var installerRollbackStart = installer.IndexOf("rollback() {", StringComparison.Ordinal);
        var installerRollbackEnd = installer.IndexOf("\nMODE=install", installerRollbackStart, StringComparison.Ordinal);
        Assert.True(installerRollbackStart >= 0 && installerRollbackEnd > installerRollbackStart);
        Assert.DoesNotContain("install_platform_upgrade_agent", installer[installerRollbackStart..installerRollbackEnd], StringComparison.Ordinal);
        Assert.Contains("updater and its pinned trust are\nforward-only", Normalize(deploymentGuide), StringComparison.Ordinal);

        var activateCurrent = installer.LastIndexOf("set_link \"$INSTALL_ROOT/current\" \"$target\"", StringComparison.Ordinal);
        var drainBootstrapRace = installer.LastIndexOf("start_pending_platform_upgrade_request", StringComparison.Ordinal);
        Assert.True(activateCurrent >= 0 && drainBootstrapRace > activateCurrent);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ExtractService(string compose, string serviceName)
    {
        var normalized = Normalize(compose);
        var marker = $"  {serviceName}:\n";
        var start = normalized.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Compose service '{serviceName}' was not found.");
        var nextService = FindNextTopLevelKey(normalized, start + marker.Length);
        return nextService < 0 ? normalized[start..] : normalized[start..nextService];
    }

    private static int FindNextTopLevelKey(string value, int start)
    {
        var candidate = value.IndexOf("\n  ", start, StringComparison.Ordinal);
        while (candidate >= 0 && candidate + 3 < value.Length && char.IsWhiteSpace(value[candidate + 3]))
        {
            candidate = value.IndexOf("\n  ", candidate + 3, StringComparison.Ordinal);
        }

        return candidate;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PROJECT_MAP.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PeerOnQ repository root.");
    }
}
