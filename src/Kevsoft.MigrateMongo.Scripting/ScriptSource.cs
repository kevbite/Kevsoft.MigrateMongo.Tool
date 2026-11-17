using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Acornima;
using Acornima.Ast;

namespace Kevsoft.MigrateMongo.Scripting;

/// <summary>Reads script files: checksums and module kinds.</summary>
internal static class ScriptSource
{
    /// <summary>
    /// SHA-256 over the file's tokens joined by single spaces, like C# migrations' checksums, so whitespace and comment
    /// changes don't alter it but any code change does.
    /// </summary>
    public static string Checksum(string source, string file)
    {
        var tokens = new List<string>();
        var parser = new Parser(new ParserOptions
        {
            OnToken = (in Token token) =>
            {
                if (token.Kind != TokenKind.EOF)
                {
                    tokens.Add(source[token.Start..token.End]);
                }
            },
        });
        Parse(parser, source, file);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(" ", tokens)));
        var builder = new StringBuilder("sha256:", 7 + (hash.Length * 2));
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether a file is CommonJS (<c>module.exports = { up, down }</c>, as migrate-mongo for Node.js writes them) rather
    /// than an ES module: it has no <c>import</c> or <c>export</c> declarations and assigns to <c>exports</c>.
    /// </summary>
    public static bool IsCommonJs(string source, string file)
    {
        var module = Parse(new Parser(), source, file);
        return !module.Body.Any(s => s is ImportDeclaration or ExportNamedDeclaration or ExportDefaultDeclaration or ExportAllDeclaration)
               && (source.Contains("module.exports", StringComparison.Ordinal) || source.Contains("exports.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Wraps a CommonJS file as an ES module whose default export is <c>module.exports</c>. The prelude is on the first line
    /// so line numbers in errors still match the file. <c>require</c> only loads <c>@kevsoft/migrate-mongo</c>.
    /// </summary>
    public static string WrapCommonJs(string source) =>
        $"import * as __migrateMongo from '{ScriptModule.Name}'; const module = {{ exports: {{}} }}; let exports = module.exports; " +
        $"const require = specifier => {{ if (specifier === '{ScriptModule.Name}') return __migrateMongo; " +
        $"throw new Error(`require('${{specifier}}') isn't supported: CommonJS migrations can only require('{ScriptModule.Name}'). Use an ES module (import) to share code between scripts.`); }}; " +
        source + "\nexport default module.exports;\n";

    private static Module Parse(Parser parser, string source, string file)
    {
        try
        {
            return parser.ParseModule(source, file);
        }
        catch (ParseErrorException e)
        {
            throw new ScriptException($"{file}:{e.LineNumber}:{e.Column}: {e.Description}", e);
        }
    }
}
