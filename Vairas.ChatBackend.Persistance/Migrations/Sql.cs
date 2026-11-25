using System.Reflection;
namespace Vairas.ChatBackend.Persistance.Migrations;
public static class Sql
{
    public static string Read(string name)
    {
        var assembly = typeof(Sql).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(".Sql." + name, StringComparison.Ordinal)))!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
}
