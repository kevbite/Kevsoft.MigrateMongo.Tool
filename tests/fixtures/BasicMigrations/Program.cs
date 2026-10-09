using BasicMigrations;
using Kevsoft.MigrateMongo;
using Microsoft.Extensions.DependencyInjection;

return await MigrateMongoRunner.RunAsync(args, services => services.AddSingleton(new SeedOptions("admin@example.com")));
