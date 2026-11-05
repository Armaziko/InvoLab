using System.Text.Json;

namespace InvoLab;

public static class Store
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static T Load<T>(string path, Func<T> fallback)
    {
        if (!File.Exists(path)) return fallback();
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(text, Opts) ?? fallback();
    }

    public static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Opts));
    }
}

public static class Options
{
    /// <summary>Parses "--key value" and bare "--flag" arguments.</summary>
    public static Dictionary<string, string> Parse(IEnumerable<string> args)
    {
        var list = args.ToList();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--")) continue;
            var key = list[i][2..];
            if (i + 1 < list.Count && !list[i + 1].StartsWith("--")) result[key] = list[++i];
            else result[key] = "true";
        }
        return result;
    }
}
