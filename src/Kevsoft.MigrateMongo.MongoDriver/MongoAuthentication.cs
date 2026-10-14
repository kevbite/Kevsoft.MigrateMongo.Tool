using System.Reflection;
using System.Runtime.ExceptionServices;
using Kevsoft.MigrateMongo.Config;
using MongoDB.Driver;
using MongoDB.Driver.Authentication;

namespace Kevsoft.MigrateMongo.MongoDriver;

/// <summary>
/// Makes sure the driver can use the connection's authentication mechanism. Some mechanisms (e.g. MONGODB-AWS) live in
/// separate driver packages that have to be registered first; when the matching Kevsoft.MigrateMongo package is
/// referenced it's registered automatically, otherwise the run fails with instructions instead of an obscure error.
/// </summary>
public static class MongoAuthentication
{
    /// <summary>Mechanisms the driver only supports through an extension, and the package that registers them.</summary>
    internal static readonly IReadOnlyDictionary<string, AuthenticationExtension> Extensions =
        new Dictionary<string, AuthenticationExtension>(StringComparer.OrdinalIgnoreCase)
        {
            ["MONGODB-AWS"] = new("Kevsoft.MigrateMongo.Aws", "Kevsoft.MigrateMongo.Aws.AwsAuthentication"),
        };

    /// <summary>Whether the driver has an implementation registered for <paramref name="mechanism"/>.</summary>
    /// <param name="mechanism">The SASL mechanism name, e.g. <c>MONGODB-AWS</c>.</param>
    public static bool IsRegistered(string mechanism)
    {
        var context = new SaslContext
        {
            Mechanism = mechanism,
            Identity = new MongoExternalIdentity("probe"),
            IdentityEvidence = new ExternalEvidence(),
            MechanismProperties = [],
        };

        try
        {
            return MongoClientSettings.Extensions.SaslMechanisms.TryCreate(context, out _);
        }
        catch (Exception)
        {
            // The registry only calls a mechanism's factory when one is registered, so a factory complaining about the
            // probe still means it's there.
            return true;
        }
    }

    /// <summary>
    /// Registers the extension for the settings' authentication mechanism if it needs one and isn't registered yet.
    /// </summary>
    /// <param name="settings">The client settings that are about to be used.</param>
    /// <exception cref="ConfigException">The mechanism needs a package that isn't referenced.</exception>
    public static void EnsureSupported(MongoClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureSupported(settings.Credential?.Mechanism, Extensions);
    }

    internal static void EnsureSupported(string? mechanism, IReadOnlyDictionary<string, AuthenticationExtension> extensions)
    {
        if (mechanism is null || !extensions.TryGetValue(mechanism, out var extension) || IsRegistered(mechanism))
        {
            return;
        }

        var register = Type.GetType($"{extension.TypeName}, {extension.Package}", throwOnError: false)
            ?.GetMethod("Register", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
        if (register is null)
        {
            throw new ConfigException(
                $"The MongoDB url uses authMechanism={mechanism}, which needs the {extension.Package} package. " +
                $"Add it to the project that runs the migrations: dotnet add package {extension.Package}");
        }

        try
        {
            register.Invoke(null, null);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
        }
    }

    internal sealed record AuthenticationExtension(string Package, string TypeName);
}
