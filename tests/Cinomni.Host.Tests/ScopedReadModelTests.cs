using Cinomni.Catalog.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// The structural half of content access. Two read models exist for the same data: an unscoped one that
/// answers for the system (event and command handlers, which have no person to answer for) and a scoped
/// twin that answers for a viewer. Nothing stops a future endpoint from binding the wrong one and quietly
/// serving everything to everyone — except this, which reads the handler signatures the Host actually maps.
/// <para>
/// The alternative, a <c>Viewer.System</c> value, would put a typed administrator bypass within reach of
/// every call site, guarded by nothing but grep. This is the guard instead.
/// </para>
/// </summary>
public sealed class ScopedReadModelTests
{
    /// <summary>Read models with no viewer. An endpoint always has one, so it must bind the scoped twin.</summary>
    private static readonly string[] Unscoped = ["ICatalogQuery", "ILibraryQuery", "ISubtitleQuery"];

    [Fact]
    public void No_endpoint_binds_an_unscoped_read_model()
    {
        var offenders = ApiAuthorizationTests.Surface()
            .Where(route => route.Handler?.GetParameters()
                .Any(parameter => Unscoped.Contains(parameter.ParameterType.Name)) == true)
            .Select(route => route.Key)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These endpoints bind a read model that answers for nobody:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    [Fact]
    public void The_composed_host_can_resolve_the_content_access_authority()
    {
        // Registered by Catalog and nowhere else, with no permissive fallback: a Host that forgot to
        // compose Catalog must fail here rather than serve every work to every account.
        var (services, _) = ApiAuthorizationTests.Compose();

        using var scope = services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IContentAccess>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICatalogBrowse>());
    }
}
