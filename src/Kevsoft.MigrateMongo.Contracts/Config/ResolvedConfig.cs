namespace Kevsoft.MigrateMongo.Config;

/// <summary>
/// Fully resolved settings for a single run (environment overlay and <c>${ENV}</c> interpolation applied).
/// </summary>
public sealed class ResolvedConfig
{
    public string? Project { get; set; }

    /// <summary>File <c>create</c> renders new migrations from instead of the built-in template. Only the tool uses it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? MigrationTemplate { get; set; }

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

    /// <summary>Seconds a transactional migration keeps retrying transient transaction errors (0 disables retries).</summary>
    public int TransactionRetryTimeoutSeconds { get; set; } = 120;

    public string? Environment { get; set; }

    /// <summary>Record the filters, updates and commands run through <c>context.Track(...)</c> (<c>results.captureStatements</c>).</summary>
    public bool ResultsCaptureStatements { get; set; } = true;

    /// <summary>The largest a migration's named results may be, in BSON bytes (<c>results.maxBytes</c>).</summary>
    public int ResultsMaxBytes { get; set; } = 64 * 1024;

    /// <summary>What happens to indexes a collection definition doesn't declare: <c>report</c>, <c>hide</c> or <c>drop</c> (<c>indexes.prune</c>).</summary>
    public string IndexesPrune { get; set; } = "report";

    /// <summary>Whether an index may be dropped and created again: <c>allow</c> or <c>fail</c> (<c>indexes.onRebuild</c>).</summary>
    public string IndexesOnRebuild { get; set; } = "allow";

    /// <summary>With <c>prune: hide</c>, seconds an undeclared index stays hidden before it's dropped (<c>indexes.dropHiddenAfter</c>).</summary>
    public int IndexesDropHiddenAfterSeconds { get; set; } = 7 * 24 * 60 * 60;

    /// <summary>
    /// Seconds after a collection definition is replaced during which a run bringing back the previous one is skipped as an
    /// older build (<c>indexes.supersededWindow</c>; 0 turns it off).
    /// </summary>
    public int IndexesSupersededWindowSeconds { get; set; } = 60 * 60;

    /// <summary>What happens to Atlas search indexes a collection doesn't declare: <c>report</c> or <c>drop</c> (<c>searchIndexes.prune</c>).</summary>
    public string SearchIndexesPrune { get; set; } = "report";

    /// <summary>Whether a run waits for search index changes to take effect (<c>searchIndexes.waitForReady</c>).</summary>
    public bool SearchIndexesWaitForReady { get; set; }

    /// <summary>When waiting for search indexes, seconds before giving up (<c>searchIndexes.waitTimeout</c>).</summary>
    public int SearchIndexesWaitTimeoutSeconds { get; set; } = 300;

    /// <summary>Whether an existing collection may be converted to capped: <c>fail</c> or <c>allow</c> (<c>collections.onConvertToCapped</c>).</summary>
    public string CollectionsOnConvertToCapped { get; set; } = "fail";

    /// <summary>What happens to an option MongoDB can't change on an existing collection: <c>fail</c> or <c>skip</c> (<c>collections.onUnchangeable</c>).</summary>
    public string CollectionsOnUnchangeable { get; set; } = "fail";

    /// <summary>What happens to a validator on the server that isn't declared: <c>report</c> or <c>remove</c> (<c>validators.prune</c>).</summary>
    public string ValidatorsPrune { get; set; } = "report";

    /// <summary>What happens when existing documents fail a new or changed validator: <c>warn</c> or <c>fail</c> (<c>validators.onInvalidDocuments</c>).</summary>
    public string ValidatorsOnInvalidDocuments { get; set; } = "warn";

    /// <summary>Whether existing documents are counted against a new or changed validator (<c>validators.countInvalidDocuments</c>).</summary>
    public bool ValidatorsCountInvalidDocuments { get; set; } = true;

    /// <summary>
    /// SSH tunnel to open before connecting. Never serialised: the tool opens the tunnel and only passes the rewritten
    /// <see cref="MongoUrl"/> to the runner, so SSH credentials never leave the tool process. A standalone runner or bundle
    /// opens it with its <see cref="IMongoTunnelProvider"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TunnelConfig? Tunnel { get; set; }
}

/// <summary>The <c>tunnel</c> section of the config file.</summary>
public sealed class TunnelConfig
{
    public const string SocksMode = "socks";
    public const string LocalMode = "local";

    public string Host { get; set; } = "";

    public int Port { get; set; } = 22;

    public string User { get; set; } = "";

    public string? Password { get; set; }

    /// <summary>The private key itself (PEM/OpenSSH text), e.g. from a CI secret.</summary>
    public string? PrivateKey { get; set; }

    public string? PrivateKeyPath { get; set; }

    public string? Passphrase { get; set; }

    /// <summary>Expected server host key fingerprint, as printed by <c>ssh-keygen -lf</c> (<c>SHA256:...</c>).</summary>
    public string? HostKeyFingerprint { get; set; }

    public string? KnownHostsPath { get; set; }

    /// <summary>
    /// <c>socks</c> (default): a SOCKS5 proxy the driver connects through, so replica sets and SRV work (needs
    /// MongoDB.Driver 3.12+). <c>local</c>: forwards a local port to the first host with <c>directConnection=true</c>.
    /// </summary>
    public string Mode { get; set; } = SocksMode;
}
