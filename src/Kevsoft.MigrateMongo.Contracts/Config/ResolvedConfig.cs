namespace Kevsoft.MigrateMongo.Config;

/// <summary>
/// Fully resolved settings for a single run (environment overlay and <c>${ENV}</c> interpolation applied).
/// </summary>
public sealed class ResolvedConfig
{
    public string? Project { get; set; }

    public string MongoUrl { get; set; } = "";

    public string DatabaseName { get; set; } = "";

    public string StateCollection { get; set; } = "_migrations";

    public int LockTtlSeconds { get; set; } = 300;

    public int LockWaitTimeoutSeconds { get; set; } = 60;

    /// <summary><c>fail</c>, <c>warn</c> or <c>ignore</c>.</summary>
    public string OnChecksumMismatch { get; set; } = "fail";

    /// <summary><c>auto</c>, <c>always</c> or <c>never</c>.</summary>
    public string Transaction { get; set; } = "auto";

    public bool AllowOutOfOrder { get; set; }

    public string? Environment { get; set; }
}
