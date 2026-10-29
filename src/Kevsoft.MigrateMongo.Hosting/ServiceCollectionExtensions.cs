using System.Reflection;
using Kevsoft.MigrateMongo.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kevsoft.MigrateMongo.Hosting
{
    /// <summary>Configures where migrations come from and whether they run on startup.</summary>
    public sealed class MongoMigrationsBuilder
    {
        internal MongoMigrationsBuilder(Microsoft.Extensions.DependencyInjection.IServiceCollection services, MongoMigrationRegistration registration)
        {
            Services = services;
            Registration = registration;
        }

        public Microsoft.Extensions.DependencyInjection.IServiceCollection Services { get; }

        internal MongoMigrationRegistration Registration { get; }

        /// <summary>Runs the source-generated migrations in <paramref name="assembly"/>.</summary>
        public MongoMigrationsBuilder FromAssembly(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);
            if (!Registration.Assemblies.Contains(assembly))
            {
                Registration.Assemblies.Add(assembly);
            }

            return this;
        }

        /// <summary>Runs the source-generated migrations in the assembly that contains <typeparamref name="T"/>.</summary>
        public MongoMigrationsBuilder FromAssemblyContaining<T>() => FromAssembly(typeof(T).Assembly);

        /// <summary>Adds a custom migration source.</summary>
        public MongoMigrationsBuilder AddSource(IMigrationSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            Registration.Sources.Add(source);
            return this;
        }

        /// <summary>
        /// Applies pending migrations, then brings collections in line with their definitions, when the host starts: before any
        /// other hosted service (including the ASP.NET Core server) starts. If a migration or a collection change fails, or
        /// the lock can't be acquired in time, the host fails to start.
        /// </summary>
        /// <param name="configure">
        /// Set <see cref="MongoMigrationStartupOptions.Collections"/> to false to leave collection definitions to a deploy step,
        /// so long index builds and blocked changes don't hold up or stop startup.
        /// </param>
        public MongoMigrationsBuilder RunOnStartup(Action<MongoMigrationStartupOptions>? configure = null)
        {
            configure?.Invoke(Registration.Startup);
            Services.TryAddEnumerable(Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<IHostedService, MongoMigrationStartupService>());
            return this;
        }
    }

    /// <summary>What <see cref="MongoMigrationsBuilder.RunOnStartup"/> applies.</summary>
    public sealed class MongoMigrationStartupOptions
    {
        /// <summary>Also bring collections in line with their <c>[ManagedCollection]</c> definitions (default true).</summary>
        public bool Collections { get; set; } = true;
    }

    internal sealed class MongoMigrationRegistration
    {
        public List<Action<IServiceProvider, MongoMigrationOptions>> Configure { get; } = [];

        public List<Assembly> Assemblies { get; } = [];

        public List<IMigrationSource> Sources { get; } = [];

        public MongoMigrationStartupOptions Startup { get; } = new();

        public MongoMigrationOptions CreateOptions(IServiceProvider services)
        {
            var options = new MongoMigrationOptions();
            foreach (var configure in Configure)
            {
                configure(services, options);
            }

            options.Validate();
            return options;
        }

        public (List<MigrationDescriptor> Migrations, List<CollectionDescriptor> Collections) Discover(ILogger logger)
        {
            if (Assemblies.Count == 0 && Sources.Count == 0)
            {
                throw new InvalidOperationException(
                    "No migrations have been configured. Call FromAssemblyContaining<T>() (or FromAssembly/AddSource) after AddMongoMigrations(...).");
            }

            var sources = new List<IMigrationSource>();
            foreach (var assembly in Assemblies)
            {
                if (MigrationRegistry.ForAssembly(assembly) is { } source)
                {
                    sources.Add(source);
                }
                else
                {
                    Log.AssemblyHasNoMigrations(logger, assembly.GetName().Name);
                }
            }

            sources.AddRange(Sources);
            var distinct = sources.Distinct().ToList();
            return (distinct.SelectMany(s => s.GetMigrations()).ToList(), distinct.SelectMany(s => s.GetCollections()).ToList());
        }
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    public static class MongoMigrationsServiceCollectionExtensions
    {
        /// <summary>
        /// Registers <see cref="IMongoMigrationRunner"/>. Chain <c>FromAssemblyContaining&lt;T&gt;()</c> to choose the migrations
        /// and <c>RunOnStartup()</c> to apply them when the host starts.
        /// </summary>
        public static MongoMigrationsBuilder AddMongoMigrations(this IServiceCollection services, Action<MongoMigrationOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            return services.AddMongoMigrations((_, options) => configure(options));
        }

        /// <inheritdoc cref="AddMongoMigrations(IServiceCollection, Action{MongoMigrationOptions})"/>
        public static MongoMigrationsBuilder AddMongoMigrations(this IServiceCollection services, Action<IServiceProvider, MongoMigrationOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            var registration = services.FirstOrDefault(d => d.ServiceType == typeof(MongoMigrationRegistration))?.ImplementationInstance as MongoMigrationRegistration;
            if (registration is null)
            {
                registration = new MongoMigrationRegistration();
                services.AddSingleton(registration);
                services.TryAddSingleton<IMongoMigrationRunner, MongoMigrationRunner>();
            }

            registration.Configure.Add(configure);
            return new MongoMigrationsBuilder(services, registration);
        }
    }
}
