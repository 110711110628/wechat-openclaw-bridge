using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using WeChatAuto.Components;
using WeChatAuto.Services;

const uint SPI_SETSCREENREADER = 0x0047;
[DllImport("user32.dll", SetLastError = true)]
static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
SystemParametersInfo(SPI_SETSCREENREADER, 1, IntPtr.Zero, 0x01 | 0x02);

var sp = WeAutomation.Initialize(o =>
{
    o.DebugMode = false;
    o.EnableOCR = false;
    o.IsForceOpenUITree = false;
    o.InitAdressBook = true;
});
var factory = sp.GetRequiredService<WeChatClientFactory>();
var names = factory.GetWeChatClientNames();
Console.WriteLine("instances: " + string.Join(",", names));
var client = factory.GetWeChatClient(names.First());

string who = args.Length > 0 ? args[0] : "";
if (string.IsNullOrEmpty(who))
{
    Console.WriteLine("usage: probe <会话名>   —— 传入要检查的聊天会话标题");
    return;
}
Console.WriteLine("locate: " + who);
try { await client.LocateConversation(who); } catch (Exception ex) { Console.WriteLine("locate err: " + ex.Message); }
await Task.Delay(1500);

var content = client.ChatContent;
var mbProp = content.GetType().GetProperty("MessageBubbleList",
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
var mb = mbProp?.GetValue(content);
if (mb == null) { Console.WriteLine("mb NULL"); return; }
Console.WriteLine("mb type: " + mb.GetType().FullName);

var rootProp = mb.GetType().GetProperty("MessageRoot",
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
var box = rootProp?.GetValue(mb);
Console.WriteLine("box type: " + box?.GetType().FullName);

var itemsProp = box!.GetType().GetProperty("Items");
var items = (Array)itemsProp!.GetValue(box)!;
Console.WriteLine("items: " + items.Length);

for (int i = 0; i < items.Length; i++)
{
    var it = items.GetValue(i)!;
    var t = it.GetType();
    string nm = t.GetProperty("Name")?.GetValue(it) as string ?? "";
    string cls = t.GetProperty("ClassName")?.GetValue(it) as string ?? "";
    string ct = "";
    try { ct = t.GetProperty("ControlType")?.GetValue(it)?.ToString() ?? ""; } catch { }
    if (nm.Length > 90) nm = nm.Substring(0, 90);
    nm = nm.Replace("\r", " ").Replace("\n", " | ");
    Console.WriteLine($"[{i}] {ct} :: {cls} :: {nm}");
}

var fetch = mb.GetType().GetMethod("FetchImageAsync",
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
Console.WriteLine("fetch method: " + (fetch == null ? "NULL" : fetch.ToString()));

if (fetch != null && items.Length > 0)
{
    for (int k = items.Length - 1; k >= Math.Max(0, items.Length - 4); k--)
    {
        try
        {
            var it = items.GetValue(k)!;
            var task = (Task)fetch.Invoke(mb, new object?[] { it, CancellationToken.None })!;
            await task;
            var res = task.GetType().GetProperty("Result")?.GetValue(task);
            var fp = res?.GetType().GetProperty("FilePath")?.GetValue(res) as string;
            var b64 = res?.GetType().GetProperty("Base64Str")?.GetValue(res) as string;
            var er = res?.GetType().GetProperty("Error")?.GetValue(res) as string;
            long sz = (!string.IsNullOrEmpty(fp) && File.Exists(fp)) ? new FileInfo(fp).Length : 0;
            Console.WriteLine($"IMG[{k}] path={fp} size={sz} b64={b64?.Length ?? 0} err={er}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"IMG[{k}] EX {ex.GetType().Name}: {ex.Message}");
        }
    }
}
Console.WriteLine("DONE");
