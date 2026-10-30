namespace SyncLane.Http;

/// <summary>Which credential a request needs: the job's owner, and the name in the recipe's <c>auth.credential</c>.</summary>
/// <param name="Owner">The owner key of the job, as in <see cref="Job.Owner"/>.</param>
/// <param name="Name">The credential name the recipe gives, such as <c>example-shop</c>.</param>
public readonly record struct CredentialKey(string Owner, string Name);
