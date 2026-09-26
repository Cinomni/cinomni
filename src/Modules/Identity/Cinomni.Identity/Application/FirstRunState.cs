namespace Cinomni.Identity.Application;

/// <summary>
/// Whether this installation has ever had an account, remembered for the life of the process.
/// <para>
/// The fact is monotonic: an installation that has an account never goes back to having none. That is
/// what makes it safe to latch, and latching is what keeps the anonymous, unthrottled
/// <c>setup-required</c> route from spending a pooled database connection on every load of the sign-in
/// screen — a connection the acquisition spine is competing for.
/// </para>
/// <para>
/// A singleton rather than a static field, so it belongs to one composed installation. A static would
/// be shared by every service provider in a process, which is wrong the moment two of them speak to
/// different databases.
/// </para>
/// <para>
/// Public only because <c>UserProvisioning</c> is: a public constructor cannot take an
/// internal parameter. Nothing outside this module has a reason to resolve it.
/// </para>
/// </summary>
public sealed class FirstRunState
{
    private volatile bool _anyUserHasExisted;

    public bool AnyUserHasExisted => _anyUserHasExisted;

    public void RecordUserExists() => _anyUserHasExisted = true;
}
