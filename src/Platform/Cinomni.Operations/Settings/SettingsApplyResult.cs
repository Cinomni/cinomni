namespace Cinomni.Operations.Settings;

/// <summary>
/// The outcome of <see cref="ISettingsAdministration.ApplyAsync"/>. Not the shared <c>Result</c>/<c>Result&lt;T&gt;</c>
/// types (<c>Cinomni.Kernel.Results</c>): a rejected batch can fail for several independent reasons at
/// once (an unknown key, a malformed value, a stale version), each attributed to its own key(s), and a
/// single <c>Error</c> cannot carry more than one.
/// </summary>
public sealed class SettingsApplyResult
{
    private SettingsApplyResult(bool isSuccess, IReadOnlyList<SettingError> errors)
    {
        IsSuccess = isSuccess;
        Errors = errors;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    /// <summary>Empty when <see cref="IsSuccess"/> is true.</summary>
    public IReadOnlyList<SettingError> Errors { get; }

    public static readonly SettingsApplyResult Success = new(true, []);

    public static SettingsApplyResult Failure(IReadOnlyList<SettingError> errors)
    {
        if (errors.Count == 0)
        {
            throw new ArgumentException("A failure must carry at least one error.", nameof(errors));
        }

        return new SettingsApplyResult(false, errors);
    }
}
