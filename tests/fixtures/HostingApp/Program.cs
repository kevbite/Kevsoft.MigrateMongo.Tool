using BasicMigrations;
using BasicMigrations.Migrations;
using HostingApp;
using Kevsoft.MigrateMongo.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

var builder = Host.CreateApplicationBuilder(args);

// The app's own client and services; migrations use both.
builder.Services.AddSingleton<IMongoClient>(new MongoClient(Environment.GetEnvironmentVariable("MONGODB_URL")));
builder.Services.AddSingleton(new SeedOptions("app-admin@example.com"));

builder.Services
    .AddMongoMigrations(o => o.DatabaseName = Environment.GetEnvironmentVariable("DATABASE_NAME")!)
    .FromAssemblyContaining<CreateUsers>()
    .RunOnStartup();

builder.Services.AddHostedService<AppStartedService>();

try
{
    await builder.Build().RunAsync();
    return 0;
}
catch (MongoMigrationException e)
{
    Console.Error.WriteLine($"Migrations failed: {e.Message}");
    return 1;
}
