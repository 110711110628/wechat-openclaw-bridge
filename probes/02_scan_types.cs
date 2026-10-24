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

bool MentionsWeChatMessage(Type t) => t == typeof(string) ? false :
    (t.Name.Contains("WeChatMessage") || (t.IsGenericType && t.GetGenericArguments().Any(MentionsWeChatMessage)));

foreach (var a in asms)
{
    Type[] ts;
    try { ts = a.GetTypes(); }
    catch (ReflectionTypeLoadException ex) { ts = ex.Types.Where(x => x != null).ToArray()!; }

    foreach (var t in ts)
    {
        // 直接打印关键类型的全部成员
        if (t.FullName == "WeChatAuto.Components.ChatContent" ||
            t.FullName == "WeChatAuto.Components.MessageBubbleList" ||
            t.FullName == "WeChatAuto.Components.WeChatClient")
        {
            Console.WriteLine("=== " + t.FullName);
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                Console.WriteLine($"    [prop] {p.PropertyType.Name} {p.Name}");
            if (t.FullName != "WeChatAuto.Components.WeChatClient")
            {
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    if (!m.IsSpecialName)
                    {
                        var ps = string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name));
                        Console.WriteLine($"    [m] {m.Name}({ps}) -> {m.ReturnType.Name}");
                    }
            }
        }
    }

    // 全局搜 WeChatMessage 相关成员
    foreach (var t in ts)
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (m.IsSpecialName) continue;
            if (!(MentionsWeChatMessage(m.ReturnType) || m.GetParameters().Any(x => MentionsWeChatMessage(x.ParameterType)))) continue;
            var ps = string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name));
            Console.WriteLine($"WM {t.FullName} :: {m.Name}({ps}) -> {m.ReturnType.Name}");
        }
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (MentionsWeChatMessage(p.PropertyType))
                Console.WriteLine($"WM-PROP {t.FullName} :: {p.Name} : {p.PropertyType.Name}");
        }
    }
}
