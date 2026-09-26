using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Host;

/// <summary>
/// What the API answers when a request never reaches an endpoint's own error handling: a body it cannot
/// read, or an exception nobody caught. Both used to come back as a bare status — a 500 with an empty
/// body for a JSON object missing a field — which a client cannot tell apart from a crash and which
/// broke the <c>{ error, message }</c> envelope every endpoint promises.
/// </summary>
internal static class ApiErrorHandling
{
    internal const string InvalidRequest = "request.invalid";

    internal const string ServerError = "server.error";

    /// <summary>
    /// Binding failures — a body that is not JSON, or a value of the wrong type — are thrown rather than
    /// answered with a bare 400, so the handler below can answer them in the envelope.
    /// <para>
    /// Required fields are not enforced here, globally: many request types have optional members a client
    /// leaves out on purpose, and a blanket rule would refuse those. Each module names its own required
    /// fields (Identity marks its credentials <c>[JsonRequired]</c>).
    /// </para>
    /// </summary>
    public static IServiceCollection AddCinomniApiErrors(this IServiceCollection services) =>
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

    /// <summary>
    /// The envelope for both cases. The message is fixed text: the exception's own message names types,
    /// paths and positions in the body, none of which the caller needs and some of which it should not see.
    /// </summary>
    public static IApplicationBuilder UseCinomniApiErrors(this IApplicationBuilder app) =>
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandler = async context =>
            {
                var failure = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                var (status, error, message) = failure is BadHttpRequestException bad
                    ? (bad.StatusCode, InvalidRequest, BadRequestMessage(bad.StatusCode))
                    : (StatusCodes.Status500InternalServerError, ServerError, "Something went wrong on the server.");

                context.Response.StatusCode = status;
                await context.Response.WriteAsJsonAsync(new { error, message });
            },
            // A body the caller got wrong is the caller's mistake, not a fault in the server. Logged as
            // an unhandled exception with its stack trace, it let anyone write an error-level entry per
            // request to an anonymous route, ahead of the rate limiter.
            SuppressDiagnosticsCallback = context => context.Exception is BadHttpRequestException,
        });

    private static string BadRequestMessage(int status) => status switch
    {
        StatusCodes.Status413PayloadTooLarge => "The request body is too large.",
        StatusCodes.Status415UnsupportedMediaType => "The request body must be JSON.",
        _ => "The request could not be read: a field is missing or has the wrong type.",
    };
}
