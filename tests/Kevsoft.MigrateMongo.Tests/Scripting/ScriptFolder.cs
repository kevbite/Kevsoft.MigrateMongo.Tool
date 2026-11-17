namespace Kevsoft.MigrateMongo.Tests.Scripting;

/// <summary>A temporary scripts folder for a test, deleted afterwards.</summary>
public sealed class ScriptFolder : IDisposable
{
    public ScriptFolder() => Directory.CreateDirectory(Root);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "mm-scripts-" + Guid.NewGuid().ToString("N"));

    public ScriptFolder Write(string file, string content)
    {
        var path = Path.Combine(Root, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return this;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
