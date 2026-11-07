namespace Cinomni.Downloads.Usenet;

/// <summary>
/// The server could not be reached, did not answer in time, or dropped the session. Unlike a refusal
/// (a missing article, a rejected sign-in) another try can succeed, so a download waits it out.
/// </summary>
internal sealed class NntpUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
