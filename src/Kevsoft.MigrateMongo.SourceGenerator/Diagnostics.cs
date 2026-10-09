using Microsoft.CodeAnalysis;

namespace Kevsoft.MigrateMongo.SourceGenerator;

internal static class Diagnostics
{
    private const string Category = "Kevsoft.MigrateMongo";

    public static readonly DiagnosticDescriptor DuplicateId = new(
        "MM0001",
        "Duplicate migration id",
        "Migration id '{0}' is used by more than one migration: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidId = new(
        "MM0002",
        "Invalid migration id",
        "Migration id '{0}' on '{1}' must be a 14 digit timestamp (yyyyMMddHHmmss)",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MustImplementInterface = new(
        "MM0003",
        "Migration must implement IMigration",
        "'{0}' is marked with [Migration] but does not implement Kevsoft.MigrateMongo.IMigration",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotConstructible = new(
        "MM0004",
        "Migration cannot be constructed",
        "'{0}' must be a non-abstract public or internal class with a public or internal constructor",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingName = new(
        "MM0005",
        "Migration name missing",
        "'{0}' must have a non-empty migration name",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
