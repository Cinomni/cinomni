using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Cinomni.Identity.Api;

/// <summary>
/// Refuses a request whose body names a <see cref="JsonRequiredAttribute"/> field and sends it as
/// <c>null</c>. The attribute only demands that the field be present: <c>"password": null</c> satisfies
/// it, binds as null into a non-nullable string, and failed inside the authenticator as a 500 — the very
/// thing marking the fields required was meant to end.
/// </summary>
internal static class RequiredFields
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> RequiredReferenceProperties = new();

    public static async ValueTask<object?> RefuseNullsAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        foreach (var argument in context.Arguments)
        {
            if (argument is not null && HasNullRequiredField(argument))
            {
                return Results.Json(
                    new { error = "identity.invalid_request", message = "A required field is missing or empty." },
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        return await next(context);
    }

    private static bool HasNullRequiredField(object argument) =>
        RequiredReferenceProperties
            .GetOrAdd(argument.GetType(), type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => !p.PropertyType.IsValueType && p.IsDefined(typeof(JsonRequiredAttribute)))
                .ToArray())
            .Any(property => property.GetValue(argument) is null);
}
