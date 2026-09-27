using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Observability;

namespace PeerOnQ.Admin.Api.Tests;

public sealed class PlatformUpgradeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Options_require_disjoint_absolute_non_root_directories_and_a_bounded_bundle()
    {
        using var directory = new TemporaryDirectory();
        var validator = new PlatformUpgradeOptionsValidator();
        var valid = new PlatformUpgradeOptions
        {
            Enabled = true,
            RequestSpoolDirectory = directory.Inbox,
            StatusDirectory = directory.Status,
        };

        var enabledValidation = validator.Validate(null, valid);
        Assert.Equal(OperatingSystem.IsLinux(), enabledValidation.Succeeded);

        valid.StatusDirectory = valid.RequestSpoolDirectory;
        Assert.True(validator.Validate(null, valid).Failed);

        valid.Enabled = false;
        valid.RequestSpoolDirectory = "relative";
        valid.StatusDirectory = "relative";
        Assert.True(validator.Validate(null, valid).Succeeded);

        valid.MaximumBundleBytes = PlatformUpgradeOptions.MaximumBundleBytesLimit + 1;
        Assert.True(validator.Validate(null, valid).Failed);
    }

    [Fact]
    public void Status_parser_accepts_only_the_exact_bounded_contract()
    {
        var parsed = PlatformUpgradeStatusValidation.Parse(StatusJson());

        Assert.Equal("idle", parsed.State);
        Assert.Equal("1.2.3", parsed.CurrentVersion);
        Assert.Single(parsed.Checks);
        Assert.Null(PlatformUpgradeStatusValidation.Parse(StatusJson(currentVersion: null)).CurrentVersion);

        var unknown = JsonNode.Parse(StatusJson())!.AsObject();
        unknown["unexpected"] = true;
        Assert.Throws<InvalidDataException>(() =>
            PlatformUpgradeStatusValidation.Parse(JsonSerializer.SerializeToUtf8Bytes(unknown, WebJson)));

        var nullChecks = JsonNode.Parse(StatusJson())!.AsObject();
        nullChecks["checks"] = null;
        Assert.Throws<InvalidDataException>(() =>
            PlatformUpgradeStatusValidation.Parse(JsonSerializer.SerializeToUtf8Bytes(nullChecks, WebJson)));

        Assert.Throws<InvalidDataException>(() =>
            PlatformUpgradeStatusValidation.Parse(StatusJson(currentVersion: "1.2.3-rc.1")));

        var duplicate = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(StatusJson()).Replace(
                "\"state\":\"idle\"",
                "\"state\":\"idle\",\"state\":\"failed\"",
                StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => PlatformUpgradeStatusValidation.Parse(duplicate));
    }

    [Fact]
    public void Status_parser_requires_actionable_state_and_versions()
    {
        var ready = JsonNode.Parse(StatusJson())!.AsObject();
        ready["state"] = "ready";
        ready["operationId"] = new string('a', 32);
        ready["targetVersion"] = "1.2.4";
        ready["canApply"] = true;

        Assert.True(PlatformUpgradeStatusValidation.Parse(
            JsonSerializer.SerializeToUtf8Bytes(ready, WebJson)).CanApply);

        var readyWithoutTarget = ready.DeepClone().AsObject();
        readyWithoutTarget["targetVersion"] = null;
        Assert.Throws<InvalidDataException>(() => PlatformUpgradeStatusValidation.Parse(
            JsonSerializer.SerializeToUtf8Bytes(readyWithoutTarget, WebJson)));

        var readyWithoutCurrent = ready.DeepClone().AsObject();
        readyWithoutCurrent["currentVersion"] = null;
        Assert.Throws<InvalidDataException>(() => PlatformUpgradeStatusValidation.Parse(
            JsonSerializer.SerializeToUtf8Bytes(readyWithoutCurrent, WebJson)));

        var succeeded = JsonNode.Parse(StatusJson(currentVersion: "1.2.4"))!.AsObject();
        succeeded["state"] = "succeeded";
        succeeded["operationId"] = new string('b', 32);
        succeeded["rollbackVersion"] = "1.2.3";
        succeeded["canRollback"] = true;

        Assert.True(PlatformUpgradeStatusValidation.Parse(
            JsonSerializer.SerializeToUtf8Bytes(succeeded, WebJson)).CanRollback);

        var rollbackInWrongState = succeeded.DeepClone().AsObject();
        rollbackInWrongState["state"] = "failed";
        Assert.Throws<InvalidDataException>(() => PlatformUpgradeStatusValidation.Parse(
            JsonSerializer.SerializeToUtf8Bytes(rollbackInWrongState, WebJson)));

        var rollbackWithoutVersion = succeeded.DeepClone().AsObject();
        rollbackWithoutVersion["rollbackVersion"] = null;
        Assert.Throws<InvalidDataException>(() => PlatformUpgradeStatusValidation.Parse(
            JsonSerializer.SerializeToUtf8Bytes(rollbackWithoutVersion, WebJson)));
    }

    [Fact]
    public void Status_reader_reports_disabled_and_unavailable_states_honestly()
    {
        using var directory = new TemporaryDirectory();
        var environment = new TestEnvironment();
        var time = new FixedTimeProvider(Now);
        var disabled = new PlatformUpgradeStatusReader(
            Options.Create(new PlatformUpgradeOptions { Enabled = false }),
            environment,
            time,
            NullLogger<PlatformUpgradeStatusReader>.Instance).Read();
        var unavailable = new PlatformUpgradeStatusReader(
            Options.Create(new PlatformUpgradeOptions
            {
                Enabled = true,
                RequestSpoolDirectory = directory.Inbox,
                StatusDirectory = directory.Status,
            }),
            environment,
            time,
            NullLogger<PlatformUpgradeStatusReader>.Instance).Read();

        Assert.False(disabled.Response.Enabled);
        Assert.False(disabled.IsTrusted);
        Assert.Equal("platform_upgrade_disabled", disabled.Response.BlockingReason);
        Assert.True(unavailable.Response.Enabled);
        Assert.False(unavailable.IsTrusted);
        Assert.Equal("platform_upgrade_status_unavailable", unavailable.Response.BlockingReason);
    }

    [Fact]
    public async Task Stage_writes_the_exact_request_and_publishes_ready_only_after_audit_commit()
    {
        using var directory = new TemporaryDirectory();
        var audit = new RecordingAuditRepository();
        var unitOfWork = new RecordingUnitOfWork(() =>
            Assert.Empty(Directory.GetFiles(directory.Inbox, "*.ready")));
        var service = CreateService(directory, Status(), unitOfWork, audit);
        var upload = CreateUpload("1.2.4", Encoding.UTF8.GetBytes("verified platform bundle"));

        var context = CreateHttpContext();
        SetUploadForm(context, upload, "  production rollout  ");
        var response = await AdminEndpoints.StagePlatformUpgradeAsync(
            context.Request,
            context,
            service,
            new PlatformUpgradeUploadGate(),
            CancellationToken.None);
        Assert.Equal(StatusCodes.Status202Accepted, Assert.IsAssignableFrom<IStatusCodeHttpResult>(response).StatusCode);
        var result = Assert.IsType<PlatformUpgradeAcceptedV1>(
            Assert.IsAssignableFrom<IValueHttpResult>(response).Value);

        Assert.Equal("stage", result.Action);
        Assert.Equal("1.2.4", result.TargetVersion);
        Assert.Equal("queued", result.State);
        Assert.Equal(1, unitOfWork.Saves);
        Assert.True(File.Exists(Path.Combine(directory.Inbox, result.RequestId + ".ready")));
        Assert.Equal(result.RequestId, File.ReadAllText(Path.Combine(directory.Inbox, ".active-request")).Trim());

        var requestDirectory = Path.Combine(directory.Inbox, result.RequestId);
        var request = File.ReadAllText(Path.Combine(requestDirectory, "request.env"));
        Assert.Equal(
            "schema=1\n" +
            $"request_id={result.RequestId}\n" +
            "action=stage\n" +
            "version=1.2.4\n" +
            "expected_current=1.2.3\n" +
            "bundle_file=peeronq-server-1.2.4.run\n" +
            "checksum_file=peeronq-server-1.2.4.run.sha256\n" +
            "signature_file=peeronq-server-1.2.4.run.asc\n" +
            $"bundle_sha256={upload.Hash}\n" +
            $"bundle_size={upload.Bundle.Length}\n",
            request.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(
            ["peeronq-server-1.2.4.run", "peeronq-server-1.2.4.run.asc", "peeronq-server-1.2.4.run.sha256", "request.env"],
            Directory.GetFiles(requestDirectory).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray());
        Assert.False(File.Exists(Path.Combine(requestDirectory, ".ready.tmp")));
        AssertReadyMode(Path.Combine(directory.Inbox, result.RequestId + ".ready"));

        var auditEvent = Assert.Single(audit.Events);
        Assert.Equal("platform.upgrade.stage.authorize", auditEvent.Action);
        Assert.Equal($"1.2.4@{result.RequestId}", auditEvent.TargetId);
        Assert.Equal("production rollout", auditEvent.Reason);
    }

    [Fact]
    public async Task Stage_never_exposes_an_unaudited_request_when_database_commit_fails()
    {
        using var directory = new TemporaryDirectory();
        var audit = new RecordingAuditRepository();
        var unitOfWork = new RecordingUnitOfWork(
            () => Assert.Empty(Directory.GetFiles(directory.Inbox, "*.ready")),
            new InvalidOperationException("database unavailable"));
        var service = CreateService(directory, Status(), unitOfWork, audit);
        var upload = CreateUpload("1.2.4", Encoding.UTF8.GetBytes("verified platform bundle"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StageAsync(
            upload.Bundle,
            upload.Checksum,
            upload.Signature,
            "production rollout",
            CreateHttpContext(),
            CancellationToken.None));

        Assert.Equal("platform.upgrade.stage.authorize", Assert.Single(audit.Events).Action);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Inbox));
    }

    [Fact]
    public async Task Stage_rejects_integrity_mismatch_and_cleans_the_host_gate()
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(
            directory,
            Status(),
            new RecordingUnitOfWork(),
            new RecordingAuditRepository());
        var upload = CreateUpload(
            "1.2.4",
            Encoding.UTF8.GetBytes("platform bundle"),
            checksumHash: new string('0', 64));

        var exception = await Assert.ThrowsAsync<ApiProblemException>(() => service.StageAsync(
            upload.Bundle,
            upload.Checksum,
            upload.Signature,
            "production rollout",
            CreateHttpContext(),
            CancellationToken.None));

        Assert.Equal("platform_upgrade_bundle_integrity_mismatch", exception.ErrorCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Inbox));
    }

    [Fact]
    public async Task Stage_rejects_busy_and_replayed_requests_before_host_authorization()
    {
        using var directory = new TemporaryDirectory();
        var upload = CreateUpload("1.2.4", Encoding.UTF8.GetBytes("platform bundle"));
        File.WriteAllText(Path.Combine(directory.Inbox, ".active-request"), new string('a', 32) + "\n");
        var busyService = CreateService(
            directory,
            Status(),
            new RecordingUnitOfWork(),
            new RecordingAuditRepository());

        var busy = await Assert.ThrowsAsync<ApiProblemException>(() => busyService.StageAsync(
            upload.Bundle,
            upload.Checksum,
            upload.Signature,
            "production rollout",
            CreateHttpContext(),
            CancellationToken.None));
        Assert.Equal("platform_upgrade_busy", busy.ErrorCode);

        File.Delete(Path.Combine(directory.Inbox, ".active-request"));
        var replayService = CreateService(
            directory,
            Status(state: "failed", targetVersion: "1.2.4"),
            new RecordingUnitOfWork(),
            new RecordingAuditRepository());
        var replay = await Assert.ThrowsAsync<ApiProblemException>(() => replayService.StageAsync(
            upload.Bundle,
            upload.Checksum,
            upload.Signature,
            "production rollout",
            CreateHttpContext(),
            CancellationToken.None));

        Assert.Equal("platform_upgrade_replay", replay.ErrorCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Inbox));
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("rollback")]
    public async Task Owner_actions_return_the_exact_202_contract_and_write_a_minimal_request(string action)
    {
        using var directory = new TemporaryDirectory();
        var audit = new RecordingAuditRepository();
        var unitOfWork = new RecordingUnitOfWork(() =>
            Assert.Empty(Directory.GetFiles(directory.Inbox, "*.ready")));
        var status = action == "apply"
            ? Status(state: "ready", targetVersion: "1.2.4", canApply: true)
            : Status(state: "succeeded", currentVersion: "1.2.4", targetVersion: "1.2.4",
                rollbackVersion: "1.2.3", canRollback: true);
        var service = CreateService(directory, status, unitOfWork, audit);
        var request = action == "apply"
            ? new PlatformUpgradeActionRequestV1("1.2.4", "1.2.3", "apply production rollout")
            : new PlatformUpgradeActionRequestV1("1.2.3", "1.2.4", "rollback production rollout");
        var context = CreateHttpContext();

        var response = action == "apply"
            ? await AdminEndpoints.ApplyPlatformUpgradeAsync(request, context, service, CancellationToken.None)
            : await AdminEndpoints.RollbackPlatformUpgradeAsync(request, context, service, CancellationToken.None);

        Assert.Equal(StatusCodes.Status202Accepted, Assert.IsAssignableFrom<IStatusCodeHttpResult>(response).StatusCode);
        var accepted = Assert.IsType<PlatformUpgradeAcceptedV1>(
            Assert.IsAssignableFrom<IValueHttpResult>(response).Value);
        Assert.Equal(action, accepted.Action);
        Assert.Equal(request.TargetVersion, accepted.TargetVersion);
        Assert.Equal("queued", accepted.State);
        var serialized = JsonSerializer.SerializeToElement(accepted, WebJson);
        Assert.Equal(
            ["requestId", "action", "targetVersion", "state", "acceptedAtUtc"],
            serialized.EnumerateObject().Select(property => property.Name).ToArray());

        Assert.Equal(
            "schema=1\n" +
            $"request_id={accepted.RequestId}\n" +
            $"action={action}\n" +
            $"version={request.TargetVersion}\n" +
            $"expected_current={request.ExpectedCurrentVersion}\n",
            File.ReadAllText(Path.Combine(directory.Inbox, accepted.RequestId, "request.env"))
                .Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(directory.Inbox, accepted.RequestId + ".ready")));
        var auditEvent = Assert.Single(audit.Events);
        Assert.Equal($"platform.upgrade.{action}.authorize", auditEvent.Action);
        Assert.Equal($"{request.TargetVersion}@{accepted.RequestId}", auditEvent.TargetId);
    }

    [Fact]
    public async Task Routes_scope_upload_limits_and_owner_only_execution_policy()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
        });
        builder.Services.AddSingleton<PlatformUpgradeService>();
        builder.Services.AddSingleton<PlatformUpgradeUploadGate>();
        await using var app = builder.Build();
        AdminEndpoints.MapPlatformUpgradeEndpoints(app.MapGroup("/admin/v1"));
        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToDictionary(endpoint => endpoint.RoutePattern.RawText!, StringComparer.Ordinal);

        var status = routes["/admin/v1/platform-upgrades/status"];
        Assert.Contains(status.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == "admin.release");

        var stage = routes["/admin/v1/platform-upgrades/stage"];
        Assert.Equal(
            PlatformUpgradeOptions.MaximumRequestBodyBytes,
            ((IRequestSizeLimitMetadata)stage.Metadata.GetMetadata<RequestSizeLimitAttribute>()!).MaxRequestBodySize);
        Assert.Equal(
            PlatformUpgradeOptions.MaximumRequestBodyBytes,
            stage.Metadata.GetMetadata<RequestFormLimitsAttribute>()!.MultipartBodyLengthLimit);
        Assert.Contains(stage.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == "admin.release");

        foreach (var action in new[] { "apply", "rollback" })
        {
            var endpoint = routes[$"/admin/v1/platform-upgrades/{action}"];
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                metadata => metadata.Policy == "admin.platform-upgrade");
            Assert.Contains(HttpMethods.Post,
                endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        }
    }

    [Fact]
    public async Task Apply_rejects_a_stale_current_version_without_writing_to_the_spool()
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(
            directory,
            Status(state: "ready", targetVersion: "1.2.4", canApply: true),
            new RecordingUnitOfWork(),
            new RecordingAuditRepository());

        var exception = await Assert.ThrowsAsync<ApiProblemException>(() => service.ApplyAsync(
            new PlatformUpgradeActionRequestV1("1.2.4", "1.2.2", "apply production rollout"),
            CreateHttpContext(),
            CancellationToken.None));

        Assert.Equal("platform_upgrade_current_version_mismatch", exception.ErrorCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Inbox));
    }

    [Fact]
    public async Task Rollback_rejects_a_running_operation_even_when_rollback_is_available()
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(
            directory,
            Status(state: "queued", currentVersion: "1.2.4", targetVersion: "1.2.5",
                rollbackVersion: "1.2.3", canRollback: true),
            new RecordingUnitOfWork(),
            new RecordingAuditRepository());

        var exception = await Assert.ThrowsAsync<ApiProblemException>(() => service.RollbackAsync(
            new PlatformUpgradeActionRequestV1("1.2.3", "1.2.4", "rollback production rollout"),
            CreateHttpContext(),
            CancellationToken.None));

        Assert.Equal("platform_upgrade_busy", exception.ErrorCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Inbox));
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("ready")]
    [InlineData("failed")]
    [InlineData("rolled_back")]
    public async Task Rollback_requires_the_succeeded_terminal_state(string state)
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(
            directory,
            Status(state: state, currentVersion: "1.2.4", targetVersion: "1.2.4",
                rollbackVersion: "1.2.3", canRollback: true),
            new RecordingUnitOfWork(),
            new RecordingAuditRepository());

        var exception = await Assert.ThrowsAsync<ApiProblemException>(() => service.RollbackAsync(
            new PlatformUpgradeActionRequestV1("1.2.3", "1.2.4", "rollback production rollout"),
            CreateHttpContext(),
            CancellationToken.None));

        Assert.Equal("platform_upgrade_rollback_unavailable", exception.ErrorCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Inbox));
    }

    [Fact]
    public async Task Upload_contract_rejects_prerelease_bundle_names()
    {
        Assert.False(PlatformSemanticVersion.TryParse("1.2.3-rc.1", out _));
        Assert.False(PlatformSemanticVersion.TryParse("1.2.1000000000", out _));
        var spool = new PlatformUpgradeRequestSpool(Options.Create(new PlatformUpgradeOptions()));
        var upload = CreateUpload("1.2.3-rc.1", Encoding.UTF8.GetBytes("platform bundle"));

        var exception = await Assert.ThrowsAsync<ApiProblemException>(() => spool.ValidateUploadAsync(
            upload.Bundle,
            upload.Checksum,
            upload.Signature,
            CancellationToken.None));

        Assert.Equal("platform_upgrade_bundle_name_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Active_cleanup_does_not_follow_a_symbolic_link()
    {
        using var directory = new TemporaryDirectory();
        var requestId = new string('a', 32);
        var target = Path.Combine(directory.Path, "outside-active");
        var link = Path.Combine(directory.Inbox, ".active-request");
        File.WriteAllText(target, requestId + "\n");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException)
        {
            return;
        }

        PlatformUpgradeRequestSpool.DeleteActive(link, requestId);

        Assert.True(File.Exists(target));
        Assert.True(File.Exists(link));
    }

    [Fact]
    public async Task Ready_publication_never_overwrites_or_deletes_an_existing_marker()
    {
        using var directory = new TemporaryDirectory();
        var requestId = new string('a', 32);
        var requestDirectory = Path.Combine(directory.Inbox, requestId);
        var active = Path.Combine(directory.Inbox, ".active-request");
        var ready = Path.Combine(directory.Inbox, requestId + ".ready");
        Directory.CreateDirectory(requestDirectory);
        File.WriteAllText(active, requestId + "\n");
        File.WriteAllText(ready, "existing");

        await using (var prepared = new PreparedPlatformUpgradeRequest(
                         requestId,
                         "apply",
                         requestDirectory,
                         active))
        {
            Assert.Throws<IOException>(prepared.Commit);
        }

        Assert.Equal("existing", File.ReadAllText(ready));
    }

    private static PlatformUpgradeService CreateService(
        TemporaryDirectory directory,
        PlatformUpgradeStatusV1 status,
        RecordingUnitOfWork unitOfWork,
        RecordingAuditRepository audit) => new(
        new PlatformUpgradeRequestSpool(Options.Create(new PlatformUpgradeOptions
        {
            Enabled = true,
            RequestSpoolDirectory = directory.Inbox,
            StatusDirectory = directory.Status,
        })),
        new FixedStatusReader(status),
        unitOfWork,
        new AdminAuditWriter(audit),
        new AdminRequestContext(new TestPrivacyHasher()),
        new FixedTimeProvider(Now));

    private static PlatformUpgradeStatusV1 Status(
        string state = "idle",
        string currentVersion = "1.2.3",
        string? targetVersion = null,
        string? rollbackVersion = null,
        bool canApply = false,
        bool canRollback = false) => new(
        true,
        "Production",
        1,
        state,
        state == "idle" ? null : new string('a', 32),
        currentVersion,
        targetVersion,
        rollbackVersion,
        0,
        canApply,
        canRollback,
        null,
        "Platform upgrade status is available.",
        null,
        Now,
        []);

    private static byte[] StatusJson(string? currentVersion = "1.2.3") =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            state = "idle",
            operationId = (string?)null,
            currentVersion,
            targetVersion = (string?)null,
            rollbackVersion = (string?)null,
            progressPercent = 0,
            canApply = false,
            canRollback = false,
            blockingReason = (string?)null,
            message = "Platform upgrade status is available.",
            logReference = (string?)null,
            updatedAtUtc = Now,
            checks = new[]
            {
                new { code = "signature", label = "Signature", state = "passed", message = "Signature passed." },
            },
        }, WebJson);

    private static UploadFiles CreateUpload(string version, byte[] bytes, string? checksumHash = null)
    {
        var name = $"peeronq-server-{version}.run";
        var hash = checksumHash ?? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new UploadFiles(
            Form(bytes, "bundle", name),
            Form(Encoding.UTF8.GetBytes($"{hash}  {name}\n"), "checksum", name + ".sha256"),
            Form(Encoding.ASCII.GetBytes(
                "-----BEGIN PGP SIGNATURE-----\n\nAA==\n-----END PGP SIGNATURE-----\n"),
                "signature",
                name + ".asc"),
            hash);
    }

    private static IFormFile Form(byte[] bytes, string fieldName, string fileName) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, fieldName, fileName);

    private static DefaultHttpContext CreateHttpContext()
    {
        var userId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        return new DefaultHttpContext
        {
            TraceIdentifier = "platform-upgrade-test",
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString("N")),
            ], "test")),
        };
    }

    private static void SetUploadForm(DefaultHttpContext context, UploadFiles upload, string reason)
    {
        var files = new FormFileCollection { upload.Bundle, upload.Checksum, upload.Signature };
        var values = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["reason"] = reason,
        };
        context.Features.Set<IFormFeature>(new FormFeature(new FormCollection(values, files)));
        context.Request.ContentType = "multipart/form-data; boundary=platform-upgrade-test";
    }

    private static void AssertReadyMode(string path)
    {
        Assert.Equal(0, new FileInfo(path).Length);
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            File.GetUnixFileMode(path));
    }

    private sealed record UploadFiles(
        IFormFile Bundle,
        IFormFile Checksum,
        IFormFile Signature,
        string Hash);

    private sealed class FixedStatusReader(PlatformUpgradeStatusV1 status) : IPlatformUpgradeStatusReader
    {
        public PlatformUpgradeStatusSnapshot Read() => new(status, true);
    }

    private sealed class RecordingAuditRepository : IAuditRepository
    {
        public List<AuditEvent> Events { get; } = [];
        public void Add(AuditEvent auditEvent) => Events.Add(auditEvent);
    }

    private sealed class RecordingUnitOfWork(
        Action? onSave = null,
        Exception? failure = null) : ICloudUnitOfWork
    {
        public int Saves { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            onSave?.Invoke();
            return failure is null ? Task.FromResult(1) : Task.FromException<int>(failure);
        }
    }

    private sealed class TestPrivacyHasher : IPrivacyHasher
    {
        public byte[] ComputeHash(string purpose, string value) =>
            SHA256.HashData(Encoding.UTF8.GetBytes($"{purpose}:{value}"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"peeronq-platform-upgrade-tests-{Guid.NewGuid():N}");
            Inbox = System.IO.Path.Combine(Path, "inbox");
            Status = System.IO.Path.Combine(Path, "status");
            Directory.CreateDirectory(Inbox);
            Directory.CreateDirectory(Status);
        }

        public string Path { get; }
        public string Inbox { get; }
        public string Status { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
