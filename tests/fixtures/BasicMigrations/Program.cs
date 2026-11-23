using BasicMigrations;
using Kevsoft.MigrateMongo;
using Microsoft.Extensions.DependencyInjection;

return await MigrateMongoRunner.RunAsync(args, services => services.AddScoped(_ => new SeedOptions("admin@example.com")));
