namespace Demo;

/// <summary>What the dashboard shows for one account.</summary>
internal sealed record AccountState(
    string Key,
    string Lane,
    int Orders,
    int Products,
    int InitialJobs,
    int InitialCompleted,
    bool Loading);
