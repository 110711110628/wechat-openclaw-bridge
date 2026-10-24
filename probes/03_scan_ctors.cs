using System.Reflection;

foreach (var f in Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
{
    try { Assembly.LoadFrom(f); } catch { }
}

var asms = AppDomain.CurrentDomain.GetAssemblies()
    .Where(a => {
        var n = a.GetName().Name ?? "";
        return n.StartsWith("WeChatAuto") || n.StartsWith("WeAuto");
    }).ToList();

foreach (var a in asms)
{
    Type[] ts;
    try { ts = a.GetTypes(); }
    catch (ReflectionTypeLoadException ex) { ts = ex.Types.Where(x => x != null).ToArray()!; }

    foreach (var t in ts)
    {
        var n = t.FullName ?? "";
        if (n == "WeChatAuto.Components.ChatContent" || n == "WeChatAuto.Components.MessageBubbleList")
        {
            Console.WriteLine("=== " + n);
            foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                Console.WriteLine("    [ctor] (" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
            foreach (var fl in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                Console.WriteLine($"    [field] {fl.FieldType.Name} {fl.Name}");
        }
        if (n == "WeChatAuto.Components.WeChatClient")
        {
            Console.WriteLine("=== WeChatClient fields");
            foreach (var fl in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                Console.WriteLine($"    [field] {fl.FieldType.Name} {fl.Name}");
        }
        if (n.EndsWith("ImageCacheItem"))
        {
            Console.WriteLine("=== " + n);
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                Console.WriteLine($"    [prop] {p.PropertyType.Name} {p.Name}");
            foreach (var fl in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                Console.WriteLine($"    [field] {fl.FieldType.Name} {fl.Name}");
        }
    }
}
