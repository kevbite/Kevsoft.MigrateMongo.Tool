using Kevsoft.MigrateMongo.MongoDriver;
using MongoDB.Driver;
using MongoDB.Driver.Authentication.AWS;

namespace Kevsoft.MigrateMongo.Aws;

/// <summary>
/// Registers the MongoDB driver's AWS IAM authentication (<c>authMechanism=MONGODB-AWS</c>).
/// </summary>
/// <remarks>
/// Kevsoft.MigrateMongo calls <see cref="Register"/> itself before connecting with MONGODB-AWS, so referencing the
/// package is enough. Call it directly only to use MONGODB-AWS with your own clients before migrations run. Unlike
/// <c>MongoClientSettings.Extensions.AddAWSAuthentication()</c> it can be called more than once.
/// </remarks>
public static class AwsAuthentication
{
    private const string Mechanism = "MONGODB-AWS";
    private static readonly object Gate = new();

    /// <summary>Registers MONGODB-AWS with the driver, unless it already is.</summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (MongoAuthentication.IsRegistered(Mechanism))
            {
                return;
            }

            try
            {
                MongoClientSettings.Extensions.AddAWSAuthentication();
            }
            catch (ArgumentException)
            {
                // Registered by other code in the meantime.
            }
        }
    }
}
