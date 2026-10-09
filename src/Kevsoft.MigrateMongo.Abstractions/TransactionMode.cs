namespace Kevsoft.MigrateMongo;

public enum TransactionMode
{
    /// <summary>Use the project-level setting (which defaults to "transaction when the topology supports it").</summary>
    Auto = 0,

    /// <summary>Always use a transaction; fail if the topology doesn't support transactions.</summary>
    Always = 1,

    /// <summary>Never use a transaction (required for some DDL such as index builds on existing collections).</summary>
    Never = 2,
}
