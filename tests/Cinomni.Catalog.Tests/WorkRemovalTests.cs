using System.Text.Json;
using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// Removing a work, against a real PostgreSQL instance: the removal and its <c>WorkRemoved</c> commit
/// together, no catalog read sees the work afterwards, the row itself is kept, and the title can be added
/// again as a new work rather than resolving to the removed one.
/// </summary>
public sealed class WorkRemovalTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await CatalogTestHost.CreateAsync("cinomni_test_catalog_removal", _ => { });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Removing_a_work_hides_it_and_publishes_the_removal_with_the_file_choice()
    {
        // Arrange
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        var query = scope.ServiceProvider.GetRequiredService<ICatalogQuery>();
        var added = await commands.AddMovieAsync("World War Z", 2013, [new ExternalId(MetadataProvider.Tmdb, "72190")]);

        // Act
        var removed = await commands.RemoveWorkAsync(added.Value, deleteFiles: true);

        // Assert — invisible to every read, but kept as a row.
        Assert.True(removed.IsSuccess);
        Assert.Null(await query.GetByIdAsync(added.Value));
        Assert.Empty(await query.ListAsync());
        Assert.Null(await query.FindByExternalIdAsync(MetadataProvider.Tmdb, "72190", WorkKind.Movie));

        await using var check = _provider.CreateAsyncScope();
        var catalogDb = check.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var row = await catalogDb.Works.IgnoreQueryFilters().SingleAsync(w => w.Id == added.Value.Value);
        Assert.NotNull(row.RemovedAt);

        var operationsDb = check.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var message = await operationsDb.Outbox.SingleAsync(m => m.EventType == CatalogEventNames.WorkRemoved);
        var payload = JsonSerializer.Deserialize<WorkRemoved>(
            message.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(payload!.DeleteFiles);
        Assert.Equal(added.Value.Value, payload.WorkId);
    }

    [Fact]
    public async Task A_removed_title_can_be_added_again_as_a_new_work()
    {
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        var first = await commands.AddMovieAsync("Sintel", 2010, [new ExternalId(MetadataProvider.Tmdb, "45745")]);
        await commands.RemoveWorkAsync(first.Value, deleteFiles: false);

        var again = await commands.AddMovieAsync("Sintel", 2010, [new ExternalId(MetadataProvider.Tmdb, "45745")]);

        Assert.True(again.IsSuccess);
        Assert.NotEqual(first.Value, again.Value);
    }

    [Fact]
    public async Task Removing_an_unknown_or_already_removed_work_fails_and_publishes_nothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        var added = await commands.AddMovieAsync("Tears of Steel", 2012, []);
        await commands.RemoveWorkAsync(added.Value, deleteFiles: false);

        var twice = await commands.RemoveWorkAsync(added.Value, deleteFiles: false);
        var unknown = await commands.RemoveWorkAsync(new WorkId(Guid.NewGuid()), deleteFiles: false);

        Assert.Equal("catalog.work_not_found", twice.Error.Code);
        Assert.Equal("catalog.work_not_found", unknown.Error.Code);
        await using var check = _provider.CreateAsyncScope();
        var operationsDb = check.ServiceProvider.GetRequiredService<OperationsDbContext>();
        Assert.Equal(1, await operationsDb.Outbox.CountAsync(m => m.EventType == CatalogEventNames.WorkRemoved));
    }
}
