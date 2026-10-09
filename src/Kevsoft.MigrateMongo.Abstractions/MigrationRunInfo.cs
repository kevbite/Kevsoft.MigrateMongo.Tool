namespace Kevsoft.MigrateMongo;

public enum MigrationDirection
{
    Up,
    Down,
}

public sealed record MigrationRunInfo(
    string RunId,
    int Batch,
    MigrationDirection Direction,
    bool Transactional,
    string? Environment);
