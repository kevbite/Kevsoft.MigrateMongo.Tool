namespace Kevsoft.MigrateMongo.Scripting;

/// <summary>A script couldn't be loaded or failed while it ran. The message says which file and where.</summary>
public sealed class ScriptException : Exception
{
    public ScriptException()
    {
    }

    public ScriptException(string message) : base(message)
    {
    }

    public ScriptException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
