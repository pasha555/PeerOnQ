using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using PeerOnQ.Cloud.Application;

namespace PeerOnQ.Observability;

public sealed class ApiProblemException(
    int statusCode,
    string errorCode,
    string title,
    string? safeDetail = null) : Exception(title)
{
    public int StatusCode { get; } = statusCode;
    public string ErrorCode { get; } = errorCode;
    public string Title { get; } = title;
    public string? SafeDetail { get; } = safeDetail;
}

public sealed class PeerOnQExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<PeerOnQExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var errorId = Guid.NewGuid().ToString("N");
        var (status, code, title, detail) = Map(exception);

        logger.Log(
            status >= StatusCodes.Status500InternalServerError ? LogLevel.Error : LogLevel.Warning,
            "API request failed with {EventName}, {ErrorId}, {ExceptionType}, {ErrorCode}",
            "api.request.failed",
            errorId,
            exception.GetType().Name,
            code);

        Activity.Current?.SetStatus(ActivityStatusCode.Error, code);
        Activity.Current?.SetTag("error.type", exception.GetType().Name);
        Activity.Current?.SetTag("peeronq.error_id", errorId);

        context.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
                Type = $"https://docs.peeronq.com/problems/{code}",
                Extensions =
                {
                    ["code"] = code,
                    ["errorId"] = errorId,
                    ["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
                },
            },
        });
    }

    private static (int Status, string Code, string Title, string? Detail) Map(Exception exception) => exception switch
    {
        ApiProblemException problem => (problem.StatusCode, problem.ErrorCode, problem.Title, problem.SafeDetail),
        CloudServiceException cloud => MapCloud(cloud),
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
            (StatusCodes.Status413PayloadTooLarge, "payload_too_large", "Request payload is too large.", null),
        BadHttpRequestException =>
            (StatusCodes.Status400BadRequest, "invalid_request", "The request is invalid.", null),
        OperationCanceledException =>
            (StatusCodes.Status408RequestTimeout, "request_cancelled", "The request did not complete.", null),
        UnauthorizedAccessException =>
            (StatusCodes.Status403Forbidden, "forbidden", "Access is denied.", null),
        KeyNotFoundException =>
            (StatusCodes.Status404NotFound, "not_found", "The requested resource was not found.", null),
        ArgumentException =>
            (StatusCodes.Status400BadRequest, "invalid_request", "The request is invalid.", null),
        _ =>
            (StatusCodes.Status500InternalServerError, "internal_error", "The service could not complete the request.", null),
    };

    private static (int Status, string Code, string Title, string? Detail) MapCloud(CloudServiceException exception)
    {
        if (!exception.IsPermanent)
        {
            return (StatusCodes.Status503ServiceUnavailable, "service_unavailable", "A required service is temporarily unavailable.", null);
        }

        var status = exception.Code switch
        {
            CloudErrorCodes.InvalidRequest => StatusCodes.Status400BadRequest,
            CloudErrorCodes.UnsupportedProtocol or CloudErrorCodes.UnsupportedVersion => StatusCodes.Status426UpgradeRequired,
            CloudErrorCodes.InstallationNotFound or CloudErrorCodes.SessionNotFound or CloudErrorCodes.DownloadNotFound or CloudErrorCodes.DiagnosticNotFound => StatusCodes.Status404NotFound,
            CloudErrorCodes.InstallationBlocked or CloudErrorCodes.DeviceRevoked => StatusCodes.Status403Forbidden,
            CloudErrorCodes.ChallengeInvalidOrExpired or CloudErrorCodes.IdentityProofInvalid => StatusCodes.Status401Unauthorized,
            CloudErrorCodes.InstallationIdentityConflict or CloudErrorCodes.IdentityFingerprintMismatch => StatusCodes.Status409Conflict,
            CloudErrorCodes.DiagnosticConsentRequired or CloudErrorCodes.DiagnosticUploadRejected => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status400BadRequest,
        };

        return (status, exception.Code.ToLowerInvariant(), "The request was rejected.", null);
    }
}
