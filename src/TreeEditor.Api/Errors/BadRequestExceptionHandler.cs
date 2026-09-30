using Microsoft.AspNetCore.Diagnostics;

namespace TreeEditor.Api.Errors;

/// <summary>
/// Turns request binding failures (malformed route, query or body values) into a 400 ProblemDetails
/// that says what could not be bound. Without it the exception handler would answer 500.
/// </summary>
internal sealed class BadRequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        httpContext.Response.StatusCode = badRequest.StatusCode;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = badRequest.StatusCode,
                Detail = badRequest.Message,
            },
        });
    }
}
