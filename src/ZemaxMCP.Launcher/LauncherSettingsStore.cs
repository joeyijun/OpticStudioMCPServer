using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ZemaxMCP.Launcher;

internal static class LauncherSettingsStore
{
    internal static JObject Load(string path)
    {
        if (!File.Exists(path))
            return File.Exists(path + ".bak") ? JObject.Parse(File.ReadAllText(path + ".bak")) : new JObject();
        try { return JObject.Parse(File.ReadAllText(path)); }
        catch when (File.Exists(path + ".bak")) { return JObject.Parse(File.ReadAllText(path + ".bak")); }
    }

    internal static void Save(string path, JObject settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, settings.ToString());
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
