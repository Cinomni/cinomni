using Cinomni.Catalog.Application;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Catalog.Messaging;

/// <summary>Runs <see cref="RefreshTrendingListCommand"/> through <see cref="TrendingListRefresh"/>.</summary>
public sealed class RefreshTrendingListCommandHandler(TrendingListRefresh refresh)
    : ICommandHandler<RefreshTrendingListCommand>
{
    public async Task<Result> HandleAsync(RefreshTrendingListCommand command, CancellationToken cancellationToken = default)
    {
        await refresh.RunAsync(cancellationToken);
        return Result.Success();
    }
}
