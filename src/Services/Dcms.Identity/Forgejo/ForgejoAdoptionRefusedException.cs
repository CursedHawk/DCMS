namespace Dcms.Identity.Forgejo;

/// <summary>
/// A Forgejo account already exists for this email and the DCMS identity has not proven the
/// address, so the sync refused to take it over. Permanent: nothing about a retry changes the
/// answer, so the callers drop the work rather than queueing it.
/// </summary>
public sealed class ForgejoAdoptionRefusedException(string message) : InvalidOperationException(message);
