using Kevsoft.MigrateMongo.Aws;
using Kevsoft.MigrateMongo.Config;
using Kevsoft.MigrateMongo.MongoDriver;
using MongoDB.Driver;
using MongoDB.Driver.Authentication.AWS;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.MongoDriver;

public class MongoAuthenticationTests
{
    [Fact]
    public void Mechanisms_the_driver_has_built_in_need_nothing()
    {
        Should.NotThrow(() => MongoAuthentication.EnsureSupported(MongoClientSettings.FromConnectionString("mongodb://user:pass@localhost/")));
        Should.NotThrow(() => MongoAuthentication.EnsureSupported(MongoClientSettings.FromConnectionString("mongodb://localhost/")));
    }

    [Fact]
    public void An_unregistered_mechanism_is_reported_as_such()
    {
        MongoAuthentication.IsRegistered("KEVSOFT-NEVER-REGISTERED").ShouldBeFalse();
    }

    [Fact]
    public void A_missing_extension_package_explains_what_to_add()
    {
        var extensions = Extensions("KEVSOFT-MISSING", new("Kevsoft.MigrateMongo.NotInstalled", "Kevsoft.MigrateMongo.NotInstalled.Auth"));

        var exception = Should.Throw<ConfigException>(() => MongoAuthentication.EnsureSupported("KEVSOFT-MISSING", extensions));

        exception.Message.ShouldBe(
            "The MongoDB url uses authMechanism=KEVSOFT-MISSING, which needs the Kevsoft.MigrateMongo.NotInstalled package. " +
            "Add it to the project that runs the migrations: dotnet add package Kevsoft.MigrateMongo.NotInstalled");
    }

    [Fact]
    public void A_referenced_extension_package_is_registered_once()
    {
        var extensions = Extensions(FakeAuthentication.Mechanism, new(typeof(FakeAuthentication).Assembly.GetName().Name!, typeof(FakeAuthentication).FullName!));

        MongoAuthentication.EnsureSupported(FakeAuthentication.Mechanism, extensions);
        MongoAuthentication.EnsureSupported(FakeAuthentication.Mechanism, extensions);

        FakeAuthentication.Registrations.ShouldBe(1);
        MongoAuthentication.IsRegistered(FakeAuthentication.Mechanism).ShouldBeTrue();
    }

    [Fact]
    public void Aws_urls_register_the_aws_package()
    {
        var settings = MongoClientSettings.FromConnectionString("mongodb+srv://cluster0.example.mongodb.net/?authSource=%24external&authMechanism=MONGODB-AWS");

        MongoAuthentication.EnsureSupported(settings);

        MongoAuthentication.IsRegistered("MONGODB-AWS").ShouldBeTrue();
    }

    [Fact]
    public void Aws_registration_tolerates_being_registered_already()
    {
        // e.g. the app called the driver's own registration before migrations ran.
        try
        {
            MongoClientSettings.Extensions.AddAWSAuthentication();
        }
        catch (ArgumentException)
        {
            // Another test registered it first.
        }

        Should.NotThrow(AwsAuthentication.Register);
        Should.NotThrow(AwsAuthentication.Register);
        MongoAuthentication.IsRegistered("MONGODB-AWS").ShouldBeTrue();
    }

    private static Dictionary<string, MongoAuthentication.AuthenticationExtension> Extensions(string mechanism, MongoAuthentication.AuthenticationExtension extension) =>
        new() { [mechanism] = extension };
}

public static class FakeAuthentication
{
    public const string Mechanism = "KEVSOFT-FAKE";

    public static int Registrations { get; private set; }

    public static void Register()
    {
        Registrations++;
        MongoClientSettings.Extensions.SaslMechanisms.Register(Mechanism, _ => throw new NotSupportedException());
    }
}
