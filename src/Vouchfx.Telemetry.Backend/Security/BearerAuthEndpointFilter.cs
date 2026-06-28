namespace Vouchfx.Telemetry.Backend.Security;

/// <summary>
/// Minimal-API endpoint filter that enforces Bearer token authentication.
/// Returns 401 with a <c>WWW-Authenticate: Bearer</c> header and an empty body
/// when the Authorization header is absent or invalid.
/// </summary>
public sealed class BearerAuthEndpointFilter : IEndpointFilter
{
    private readonly BearerTokenValidator _validator;

    /// <summary>Initialises the filter with the configured <paramref name="validator"/>.</summary>
    public BearerAuthEndpointFilter(BearerTokenValidator validator) => _validator = validator;

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var authHeader = context.HttpContext.Request.Headers.Authorization.ToString();
        if (!_validator.IsValid(authHeader))
        {
            context.HttpContext.Response.Headers["WWW-Authenticate"] = "Bearer";
            return new UnauthorizedEmptyResult();
        }

        return await next(context);
    }
}

/// <summary>
/// Returns HTTP 401 with no response body (avoids leaking information via a
/// ProblemDetails body that the default <see cref="Results.Unauthorized"/> would write).
/// </summary>
internal sealed class UnauthorizedEmptyResult : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
