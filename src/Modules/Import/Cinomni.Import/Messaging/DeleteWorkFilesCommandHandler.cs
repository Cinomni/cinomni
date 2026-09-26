using Cinomni.Import.Application;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Import.Messaging;

/// <summary>Deletes the library files of a removed work (⇠ Catalog's WorkRemoved with its files).</summary>
public sealed class DeleteWorkFilesCommandHandler(WorkFileRemoval removal) : ICommandHandler<DeleteWorkFilesCommand>
{
    public async Task<Result> HandleAsync(DeleteWorkFilesCommand command, CancellationToken cancellationToken = default)
    {
        await removal.RemoveAsync(command.WorkId, cancellationToken);
        return Result.Success();
    }
}
