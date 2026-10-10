namespace Librus;

/// <summary>Stable, machine-readable error codes surfaced by every librus-net component.</summary>
public static class LibrusErrorCodes
{
    public const string AuthenticationFailed = "AUTHENTICATION_FAILED";
    public const string AuthRefreshFailed = "AUTH_REFRESH_FAILED";
    public const string PortalLoginPageInvalid = "PORTAL_LOGIN_PAGE_INVALID";
    public const string ApiRequestFailed = "API_REQUEST_FAILED";
    public const string AccessDenied = "ACCESS_DENIED";
    public const string NotFound = "NOT_FOUND";
    public const string RateLimited = "RATE_LIMITED";
    public const string ServiceMaintenance = "SERVICE_MAINTENANCE";
    public const string NetworkError = "NETWORK_ERROR";
    public const string NetworkTimeout = "NETWORK_TIMEOUT";
    public const string ResponseValidationFailed = "RESPONSE_VALIDATION_FAILED";
    public const string ConfigurationError = "CONFIGURATION_ERROR";
    public const string ChildNotFound = "CHILD_NOT_FOUND";
    public const string AmbiguousChild = "AMBIGUOUS_CHILD";
    public const string ChildRequired = "CHILD_REQUIRED";
    public const string MessagesUnavailable = "MESSAGES_UNAVAILABLE";
    public const string SendDisabled = "SEND_DISABLED";
    public const string SendNotImplemented = "SEND_NOT_IMPLEMENTED";
    public const string ConfirmationRequired = "CONFIRMATION_REQUIRED";
    public const string InvalidArgument = "INVALID_ARGUMENT";
}

/// <summary>
/// The single exception type thrown by librus-net. Messages and properties are secret-safe:
/// they never contain passwords, bearer tokens, cookie values or response bodies.
/// </summary>
public class LibrusException : Exception
{
    public LibrusException(string code, string message, string? endpoint = null, int? status = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Endpoint = endpoint;
        Status = status;
    }

    /// <summary>One of <see cref="LibrusErrorCodes"/>.</summary>
    public string Code { get; }

    /// <summary>Request URL (already redacted where it would carry a secret).</summary>
    public string? Endpoint { get; }

    /// <summary>HTTP status code if the failure came from an HTTP response.</summary>
    public int? Status { get; }

    public bool IsUnauthorized => Status == 401;

    public static LibrusException FromStatus(string endpoint, int status, string what = "Librus API request failed")
    {
        var code = status switch
        {
            401 => LibrusErrorCodes.AuthenticationFailed,
            403 => LibrusErrorCodes.AccessDenied,
            404 => LibrusErrorCodes.NotFound,
            429 => LibrusErrorCodes.RateLimited,
            503 => LibrusErrorCodes.ServiceMaintenance,
            _ => LibrusErrorCodes.ApiRequestFailed,
        };
        return new LibrusException(code, $"{what} (HTTP {status}).", endpoint, status);
    }
}
