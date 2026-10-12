using System.Globalization;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace Kevsoft.MigrateMongo.Config;

/// <summary>
/// Loads <c>migrate-mongo.yaml</c>. Uses the YAML representation model (no reflection) so it is Native AOT safe.
/// </summary>
public static class ConfigLoader
{
    public const string DefaultFileName = "migrate-mongo.yaml";

    private static readonly string[] AlternativeFileNames = [DefaultFileName, "migrate-mongo.yml"];

    private static readonly HashSet<string> KnownKeys =
    [
        "project", "mongodb", "stateCollection", "lockTtl", "lockWaitTimeout", "onChecksumMismatch",
        "transaction", "transactionRetryTimeout", "allowOutOfOrder", "environments", "tunnel",
    ];

    private static readonly HashSet<string> TunnelKeys =
    [
        "host", "port", "user", "password", "privateKey", "privateKeyPath", "passphrase", "hostKeyFingerprint", "knownHostsPath", "mode",
    ];

    /// <summary>Finds the config file: an explicit path, or the default file names in <paramref name="directory"/>.</summary>
    public static string? Find(string? explicitPath, string directory)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath, directory);
            return File.Exists(full) ? full : throw new ConfigException($"Config file '{explicitPath}' was not found.");
        }

        return AlternativeFileNames.Select(n => Path.Combine(directory, n)).FirstOrDefault(File.Exists);
    }

    /// <param name="projectOnly">Only resolve <c>project</c>, so commands that don't connect (like <c>create</c>) work without connection variables.</param>
    public static ResolvedConfig LoadFile(string path, string? environment, Func<string, string?>? getEnvironmentVariable = null, bool projectOnly = false)
    {
        var resolved = Load(File.ReadAllText(path), environment, getEnvironmentVariable, projectOnly);
        var directory = Path.GetDirectoryName(path)!;
        if (resolved.Project is not null)
        {
            resolved.Project = Path.GetFullPath(resolved.Project, directory);
        }

        if (resolved.Tunnel is { } tunnel)
        {
            tunnel.PrivateKeyPath = ResolvePath(tunnel.PrivateKeyPath, directory);
            tunnel.KnownHostsPath = ResolvePath(tunnel.KnownHostsPath, directory);
        }

        return resolved;
    }

    /// <summary>Resolves <c>~/</c> to the home directory and relative paths against the config file's directory.</summary>
    private static string? ResolvePath(string? path, string directory)
    {
        if (path is null)
        {
            return null;
        }

        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
        }

        return Path.GetFullPath(path, directory);
    }

    public static ResolvedConfig Load(string yaml, string? environment, Func<string, string?>? getEnvironmentVariable = null, bool projectOnly = false)
    {
        getEnvironmentVariable ??= System.Environment.GetEnvironmentVariable;

        var root = Parse(yaml);
        ValidateKeys(root, "");

        if (!string.IsNullOrEmpty(environment))
        {
            if (root.GetValueOrDefault("environments") is not Dictionary<string, object?> environments ||
                environments.GetValueOrDefault(environment) is not Dictionary<string, object?> overlay)
            {
                throw new ConfigException($"Environment '{environment}' is not defined under 'environments' in the config file.");
            }

            ValidateKeys(overlay, $"environments.{environment}.");
            root = Merge(root, overlay);
        }

        root.Remove("environments");

        var config = new ResolvedConfig { Environment = string.IsNullOrEmpty(environment) ? null : environment };
        config.Project = OptionalString(root, "project", getEnvironmentVariable);
        if (projectOnly)
        {
            return config;
        }

        if (root.GetValueOrDefault("mongodb") is Dictionary<string, object?> mongo)
        {
            config.MongoUrl = OptionalString(mongo, "url", getEnvironmentVariable) ?? "";
            config.DatabaseName = OptionalString(mongo, "databaseName", getEnvironmentVariable) ?? "";
        }

        config.StateCollection = OptionalString(root, "stateCollection", getEnvironmentVariable) ?? config.StateCollection;
        config.LockTtlSeconds = OptionalInt(root, "lockTtl", getEnvironmentVariable) ?? config.LockTtlSeconds;
        config.LockWaitTimeoutSeconds = OptionalInt(root, "lockWaitTimeout", getEnvironmentVariable) ?? config.LockWaitTimeoutSeconds;
        config.OnChecksumMismatch = OneOf(root, "onChecksumMismatch", ["fail", "warn", "ignore"], getEnvironmentVariable) ?? config.OnChecksumMismatch;
        config.Transaction = OneOf(root, "transaction", ["auto", "always", "never"], getEnvironmentVariable) ?? config.Transaction;
        config.TransactionRetryTimeoutSeconds = OptionalInt(root, "transactionRetryTimeout", getEnvironmentVariable) ?? config.TransactionRetryTimeoutSeconds;
        config.AllowOutOfOrder = OptionalBool(root, "allowOutOfOrder", getEnvironmentVariable) ?? false;
        config.Tunnel = root.GetValueOrDefault("tunnel") switch
        {
            null => null,
            Dictionary<string, object?> tunnel => LoadTunnel(tunnel, getEnvironmentVariable),
            _ => throw new ConfigException("Config setting 'tunnel' must be a mapping."),
        };

        return config;
    }

    private static TunnelConfig LoadTunnel(Dictionary<string, object?> map, Func<string, string?> env)
    {
        var tunnel = new TunnelConfig
        {
            Host = OptionalString(map, "host", env) ?? "",
            Port = OptionalInt(map, "port", env) ?? 22,
            User = OptionalString(map, "user", env) ?? "",
            Password = NullIfEmpty(OptionalString(map, "password", env)),
            PrivateKey = NullIfEmpty(OptionalString(map, "privateKey", env)),
            PrivateKeyPath = NullIfEmpty(OptionalString(map, "privateKeyPath", env)),
            Passphrase = NullIfEmpty(OptionalString(map, "passphrase", env)),
            HostKeyFingerprint = NullIfEmpty(OptionalString(map, "hostKeyFingerprint", env)),
            KnownHostsPath = NullIfEmpty(OptionalString(map, "knownHostsPath", env)),
            Mode = OneOf(map, "mode", [TunnelConfig.SocksMode, TunnelConfig.LocalMode], env) ?? TunnelConfig.SocksMode,
        };

        if (string.IsNullOrWhiteSpace(tunnel.Host))
        {
            throw new ConfigException("'tunnel.host' is not set in the config file.");
        }

        if (string.IsNullOrWhiteSpace(tunnel.User))
        {
            throw new ConfigException("'tunnel.user' is not set in the config file.");
        }

        if (tunnel.Port is < 1 or > 65535)
        {
            throw new ConfigException("'tunnel.port' must be between 1 and 65535.");
        }

        if (tunnel.Password is null && tunnel.PrivateKey is null && tunnel.PrivateKeyPath is null)
        {
            throw new ConfigException("'tunnel' needs one of 'password', 'privateKey' or 'privateKeyPath'.");
        }

        if (tunnel.PrivateKey is not null && tunnel.PrivateKeyPath is not null)
        {
            throw new ConfigException("'tunnel' can't have both 'privateKey' and 'privateKeyPath'.");
        }

        if (tunnel.HostKeyFingerprint is { } fingerprint && !fingerprint.StartsWith("SHA256:", StringComparison.Ordinal))
        {
            throw new ConfigException("'tunnel.hostKeyFingerprint' must be a SHA256 fingerprint as printed by 'ssh-keygen -lf', e.g. 'SHA256:abc...'.");
        }

        return tunnel;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Checks the settings needed to connect are present.</summary>
    public static void EnsureConnectable(ResolvedConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.MongoUrl))
        {
            throw new ConfigException("'mongodb.url' is not set in the config file.");
        }

        if (string.IsNullOrWhiteSpace(config.DatabaseName))
        {
            throw new ConfigException("'mongodb.databaseName' is not set in the config file.");
        }

        if (config.LockTtlSeconds < 3)
        {
            throw new ConfigException("'lockTtl' must be at least 3 seconds.");
        }

        if (config.LockWaitTimeoutSeconds < 0)
        {
            throw new ConfigException("'lockWaitTimeout' must not be negative.");
        }

        if (config.TransactionRetryTimeoutSeconds < 0)
        {
            throw new ConfigException("'transactionRetryTimeout' must not be negative.");
        }
    }

    /// <summary>Replaces <c>${NAME}</c> and <c>${NAME:-default}</c> with environment variable values.</summary>
    public static string Interpolate(string value, Func<string, string?> getEnvironmentVariable)
    {
        var builder = new StringBuilder();
        var index = 0;
        while (index < value.Length)
        {
            var start = value.IndexOf("${", index, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(value, index, value.Length - index);
                break;
            }

            var end = value.IndexOf('}', start + 2);
            if (end < 0)
            {
                throw new ConfigException($"Unterminated '${{' in config value '{value}'.");
            }

            builder.Append(value, index, start - index);
            var expression = value.Substring(start + 2, end - start - 2);
            var defaultIndex = expression.IndexOf(":-", StringComparison.Ordinal);
            var name = defaultIndex >= 0 ? expression[..defaultIndex] : expression;
            var fallback = defaultIndex >= 0 ? expression[(defaultIndex + 2)..] : null;
            var resolved = getEnvironmentVariable(name);
            if (string.IsNullOrEmpty(resolved))
            {
                resolved = fallback ?? throw new ConfigException($"Environment variable '{name}' referenced in the config file is not set.");
            }

            builder.Append(resolved);
            index = end + 1;
        }

        return builder.ToString();
    }

    private static Dictionary<string, object?> Parse(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlDotNet.Core.YamlException e)
        {
            throw new ConfigException($"Invalid YAML in config file: {e.Message}");
        }

        if (stream.Documents.Count == 0)
        {
            return new Dictionary<string, object?>();
        }

        return Convert(stream.Documents[0].RootNode) as Dictionary<string, object?>
               ?? throw new ConfigException("The config file must contain a YAML mapping at the top level.");
    }

    private static object? Convert(YamlNode node) => node switch
    {
        YamlMappingNode mapping => mapping.Children.ToDictionary(
            kv => ((YamlScalarNode)kv.Key).Value ?? "",
            kv => Convert(kv.Value),
            StringComparer.Ordinal),
        YamlSequenceNode sequence => sequence.Children.Select(Convert).ToList(),
        YamlScalarNode scalar => scalar.Value is null or "~" or "null" && scalar.Style == YamlDotNet.Core.ScalarStyle.Plain ? null : scalar.Value,
        _ => null,
    };

    private static Dictionary<string, object?> Merge(Dictionary<string, object?> baseMap, Dictionary<string, object?> overlay)
    {
        var result = new Dictionary<string, object?>(baseMap, StringComparer.Ordinal);
        foreach (var (key, value) in overlay)
        {
            result[key] = result.GetValueOrDefault(key) is Dictionary<string, object?> existing && value is Dictionary<string, object?> nested
                ? Merge(existing, nested)
                : value;
        }

        return result;
    }

    private static void ValidateKeys(Dictionary<string, object?> map, string prefix)
    {
        foreach (var key in map.Keys)
        {
            if (!KnownKeys.Contains(key) || (prefix.Length > 0 && key == "environments"))
            {
                throw new ConfigException($"Unknown config setting '{prefix}{key}'.");
            }
        }

        if (map.GetValueOrDefault("mongodb") is Dictionary<string, object?> mongo)
        {
            foreach (var key in mongo.Keys.Where(k => k is not ("url" or "databaseName")))
            {
                throw new ConfigException($"Unknown config setting '{prefix}mongodb.{key}'.");
            }
        }

        if (map.GetValueOrDefault("tunnel") is Dictionary<string, object?> tunnel)
        {
            foreach (var key in tunnel.Keys.Where(k => !TunnelKeys.Contains(k)))
            {
                throw new ConfigException($"Unknown config setting '{prefix}tunnel.{key}'.");
            }
        }
    }

    private static string? OptionalString(Dictionary<string, object?> map, string key, Func<string, string?> env) =>
        map.GetValueOrDefault(key) switch
        {
            null => null,
            string s => Interpolate(s, env),
            _ => throw new ConfigException($"Config setting '{key}' must be a single value."),
        };

    private static int? OptionalInt(Dictionary<string, object?> map, string key, Func<string, string?> env)
    {
        var value = OptionalString(map, key, env);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new ConfigException($"Config setting '{key}' must be a whole number but was '{value}'.");
    }

    private static bool? OptionalBool(Dictionary<string, object?> map, string key, Func<string, string?> env)
    {
        var value = OptionalString(map, key, env);
        return value switch
        {
            null => null,
            "true" or "True" or "yes" => true,
            "false" or "False" or "no" => false,
            _ => throw new ConfigException($"Config setting '{key}' must be true or false but was '{value}'."),
        };
    }

    private static string? OneOf(Dictionary<string, object?> map, string key, string[] allowed, Func<string, string?> env)
    {
        var value = OptionalString(map, key, env);
        if (value is null)
        {
            return null;
        }

        return allowed.Contains(value, StringComparer.Ordinal)
            ? value
            : throw new ConfigException($"Config setting '{key}' must be one of {string.Join(", ", allowed)} but was '{value}'.");
    }
}
