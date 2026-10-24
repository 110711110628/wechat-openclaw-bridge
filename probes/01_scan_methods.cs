using System.Reflection;

var KW = new[] { "FetchImage", "GetImageCache", "ImageFilePath", "SendFile", "SendEmoji",
                 "CaptureImage", "FetchFile", "FetchAttachment", "SendVoice", "MessageMonitor",
                 "Download" };

foreach (var f in Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
{
    try { Assembly.LoadFrom(f); } catch { }
}

var asms = AppDomain.CurrentDomain.GetAssemblies()
    .Where(a => {
        var n = a.GetName().Name ?? "";
        return n.StartsWith("WeChatAuto") || n.StartsWith("WeAuto");
    })
    .ToList();

Console.WriteLine("ASMS: " + string.Join(", ", asms.Select(a => a.GetName().Name)));

foreach (var a in asms)
{
    Type[] ts;
    try { ts = a.GetTypes(); }
    catch (ReflectionTypeLoadException ex) { ts = ex.Types.Where(t => t != null).ToArray()!; }

    foreach (var t in ts)
    {
        MethodInfo[] ms;
        try
        {
            ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                              BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        }
        catch { continue; }

        foreach (var m in ms)
        {
            if (!KW.Any(k => m.Name.Contains(k))) continue;
            var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
            Console.WriteLine($"{t.FullName} :: {m.Name}({ps}) -> {m.ReturnType.Name}");
        }

        PropertyInfo[] props;
        try
        {
            props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance |
                                    BindingFlags.Static | BindingFlags.DeclaredOnly);
        }
        catch { continue; }

        foreach (var p in props)
        {
            if (!KW.Any(k => p.Name.Contains(k))) continue;
            Console.WriteLine($"{t.FullName} :: [prop] {p.Name} : {p.PropertyType.Name}");
        }
    }
}
