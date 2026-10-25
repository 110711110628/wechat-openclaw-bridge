using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using WeChatAuto.Components;
using WeChatAuto.Options;
using WeChatAuto.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;

const uint SPI_SETSCREENREADER = 0x0047;
[DllImport("user32.dll", SetLastError = true)]
static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
[DllImport("user32.dll")]
static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")]
static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);
const uint MOUSEEVENTF_WHEEL = 0x0800;
const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
const uint MOUSEEVENTF_LEFTUP = 0x0004;
const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
const uint MOUSEEVENTF_RIGHTUP = 0x0010;
const uint KEYEVENTF_KEYUP = 0x0002;
[DllImport("user32.dll")]
static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
SystemParametersInfo(SPI_SETSCREENREADER, 1, IntPtr.Zero, 0x01 | 0x02);

// 微信的 UI 自动化全是**同步阻塞**调用（一次 GetChatHistory 就要 7 秒），
// 只要它占着一个线程池线程，Kestrel 就没线程接新请求 —— 表现是
// /api/inbound 明明只是读内存队列，却会 30 秒超时，然后守着它的客户端
// 以为服务挂了。把最小线程数抬上去，几个线程被 UIA 卡住也不影响接请求。
ThreadPool.GetMinThreads(out int _wMin, out int _iMin);
ThreadPool.SetMinThreads(Math.Max(_wMin, 64), Math.Max(_iMin, 64));

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://0.0.0.0:18800");
var app = builder.Build();

var TOKEN = Environment.GetEnvironmentVariable("WXAPI_TOKEN") ?? "wxapi-local";

object _lock = new();
WeChatClient _client = null;
string _self = null;

WeChatClient C()
{
    if (_client != null) return _client;
    lock (_lock)
    {
        if (_client != null) return _client;
        var sp = WeAutomation.Initialize(o =>
        {
            o.DebugMode = false;
            o.EnableOCR = true;
            o.IsForceOpenUITree = true;
            o.InitAdressBook = true;
        });
        var f = sp.GetRequiredService<WeChatClientFactory>();
        var names = f.GetWeChatClientNames();
        if (names.Count == 0) throw new Exception("未找到微信实例，UI Tree 可能未开，先跑 warm");
        _self = names.First();
        _client = f.GetWeChatClient(_self);
    }
    return _client;
}

object GetMB(WeChatClient c)
{
    var ct = c.ChatContent;
    var p = ct.GetType().GetProperty("MessageBubbleList",
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    return p!.GetValue(ct)!;
}

Array GetItems(object mb)
{
    var rp = mb.GetType().GetProperty("MessageRoot",
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    var box = rp!.GetValue(mb)!;
    var ip = box.GetType().GetProperty("Items");
    return (Array)ip!.GetValue(box)!;
}

// 把最后几条消息打包给桥做上下文。桥原来要自己再调一次 /api/history（7 秒级），
// 而这里刚读完全一样的数据，顺手带上就省掉一次全屏 UIA 操作。
List<object> RecentOf(System.Collections.IEnumerable hist, int n = 6)
{
    var all = new List<object>();
    foreach (var x in hist)
    {
        all.Add(new
        {
            who = (ReadMember(x, "Who") as string) ?? "",
            text = (ReadMember(x, "Message") as string) ?? "",
            time = (ReadMember(x, "SendDateTime") as string) ?? ""
        });
    }
    return all.Count > n ? all.GetRange(all.Count - n, n) : all;
}

object? ReadMember(object o, string name)
{
    var t = o.GetType();
    var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    if (p != null) { try { return p.GetValue(o); } catch { return null; } }
    var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    return f?.GetValue(o);
}

var _inbound = new ConcurrentQueue<Inbound>();
long _seq = 0;
string _epoch = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
var _seen = new HashSet<string>();
object _sentLock = new();
object _seenLock = new();
var _monCts = new CancellationTokenSource();
var _monWho = new List<string>();
var _seenMax = 20000;
int _busy = 0;
// UI 自动化是「单窗口」资源：谁把会话切过去，别人就读到别的聊天。
// 所有「打开会话 + 读气泡/截图/滚动」的段必须串行。
var _uiLock = new SemaphoreSlim(1, 1);
var _sent = new HashSet<string>();
var _sentQ = new Queue<string>();      // 只用来做 FIFO 淘汰，防止 _sent 无限增长
const int SENT_MAX = 2000;

// 记下「这条是我发的」，用来过滤自己的回显。超上限就丢掉最老的，不影响近期判重。
void MarkSent(string t)
{
    t = (t ?? "").Trim();
    if (t.Length == 0) return;
    lock (_sentLock)
    {
        if (_sent.Add(t)) _sentQ.Enqueue(t);
        while (_sentQ.Count > SENT_MAX)
        {
            var old = _sentQ.Dequeue();
            _sent.Remove(old);
        }
    }
}

// ---- 去重表落盘：重启后不再重复入队，配合启动补扫把停机期间的消息找回来 ----
var SEEN_FILE = @"C:\setup\wxapi\seen.json";
try
{
    if (File.Exists(SEEN_FILE))
    {
        var arr = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(SEEN_FILE));
        if (arr != null) foreach (var s in arr) _seen.Add(s);
        Console.WriteLine("[seen] loaded " + _seen.Count);
    }
}
catch (Exception ex) { Console.WriteLine("[seen] load error " + ex.Message); }

DateTime _lastSeenSave = DateTime.MinValue;
void SaveSeen(bool force = false)
{
    if (!force && (DateTime.Now - _lastSeenSave).TotalSeconds < 15) return;
    _lastSeenSave = DateTime.Now;
    try
    {
        List<string> l;
        lock (_seenLock) l = _seen.ToList();
        if (l.Count > _seenMax) l = l.Skip(l.Count - _seenMax).ToList();
        File.WriteAllText(SEEN_FILE, JsonSerializer.Serialize(l));
    }
    catch (Exception ex) { Console.WriteLine("[seen] save error " + ex.Message); }
}

async Task<bool> SubmitMonitor(string who)
{
    var c = C();
    _ = Task.Run(async () =>
    {
        Console.WriteLine("[poll] loop started for " + who);
        var firstSeen = new HashSet<string>();

        // 启动补扫：把这一次停机期间漏掉的消息补回来。首次运行（seen 为空）只做基线不补发。
        if (who == "*")
        {
            await _uiLock.WaitAsync();
            try
            {
                bool baseline = _seen.Count == 0;
                var vis0 = await c.GetVisibleConversations();
                Console.WriteLine("[catchup] start baseline=" + baseline + " convs=" + (vis0?.Count ?? 0));
                if (vis0 != null)
                {
                    // 每个会话都要 GetChatHistory（7 秒级），7 个会话就是 50 秒，
                    // 这段时间 HTTP 端点基本都在超时 → 给个预算，剩下的交给轮询
                    var cw = System.Diagnostics.Stopwatch.StartNew();
                    foreach (var v in vis0)
                    {
                        if (cw.Elapsed.TotalSeconds > 25)
                        {
                            Console.WriteLine("[catchup] 超出 25 秒预算，放弃剩余会话（未读会由轮询接手）");
                            break;
                        }
                        string w = v.ConversationTitle;
                        if (string.IsNullOrWhiteSpace(w)) continue;
                        try
                        {
                            var hist = await c.GetChatHistory(w, DateTime.Today);
                            if (hist == null || hist.Count == 0) continue;
                            foreach (var m in hist.Skip(Math.Max(0, hist.Count - 6)))
                            {
                                if (string.IsNullOrEmpty(m.UniqueString)) continue;
                                bool fresh;
                                lock (_seenLock) { fresh = _seen.Add(m.UniqueString); }
                                if (!fresh) continue;
                                if (baseline) continue;
                                string txt0 = (m.Message ?? "").Trim();
                                if (txt0.Length == 0) continue;
                                // 自己发的消息（含通过 /api/upload/send 发出去的文件）不能入队。
                                // _sent 只记文本，而文件消息的文本是「文件\nxxx.pdf\n150K」，
                                // 匹配不上 —— 重启补扫就会把自己发过的文件当成新消息，
                                // 让 agent 回一句「收到这个文件」。用发送者判断才可靠。
                                string snd0 = (ReadMember(m, "Who") as string) ?? "";
                                if (!string.IsNullOrEmpty(_self) && snd0 == _self) continue;
                                bool echo0;
                                lock (_seenLock) { echo0 = _sent.Contains(txt0); }
                                if (echo0) continue;
                                _inbound.Enqueue(new Inbound(Interlocked.Increment(ref _seq), w, m.Message ?? "", m.SendDateTime ?? "", m.UniqueString, (ReadMember(m, "Who") as string) ?? "", RecentOf(hist)));
                                Console.WriteLine("[catchup] QUEUED who=" + w + " text=" + txt0);
                            }
                            firstSeen.Add(w);
                        }
                        catch (Exception ex0) { Console.WriteLine("[catchup] skip " + w + ": " + ex0.Message); }
                        await Task.Delay(200);
                    }
                }
                SaveSeen(true);
                Console.WriteLine("[catchup] done seen=" + _seen.Count);
            }
            catch (Exception ex) { Console.WriteLine("[catchup] error: " + ex.Message); }
            finally { _uiLock.Release(); }
        }

        while (!_monCts.IsCancellationRequested)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
            {
                await _uiLock.WaitAsync();
                try
                {
                    var vis = await c.GetVisibleConversations();
                    if (vis != null)
                    {
                        var targets = vis.Where(x => x.NotReadNumbr > 0 && (who == "*" || x.ConversationTitle == who)).ToList();
                        foreach (var t in targets)
                        {
                            string w = t.ConversationTitle;
                            Console.WriteLine("[poll] unread " + w + " = " + t.NotReadNumbr);
                            var hist = await c.GetChatHistory(w, DateTime.Today);
                            if (hist == null || hist.Count == 0) continue;
                            bool first = !firstSeen.Contains(w);
                            int take = first ? 6 : 6;   // 统一取 6 条：@ 和正文常常分成两条发
                            var tail = hist.Skip(Math.Max(0, hist.Count - take)).ToList();
                            foreach (var m in tail)
                            {
                                if (string.IsNullOrEmpty(m.UniqueString)) continue;
                                bool fresh;
                                lock (_seenLock) { fresh = _seen.Add(m.UniqueString); }
                                if (!fresh) continue;
                                string txt = (m.Message ?? "").Trim();
                                string snd = (ReadMember(m, "Who") as string) ?? "";
                                // 同上：自己发的一律跳过，别让 agent 回自己的话/自己的文件
                                if (!string.IsNullOrEmpty(_self) && snd == _self) continue;
                                bool echo;
                                lock (_seenLock) { echo = txt.Length > 0 && _sent.Contains(txt); }
                                if (echo) continue;
                                _inbound.Enqueue(new Inbound(Interlocked.Increment(ref _seq), w, m.Message ?? "", m.SendDateTime ?? "", m.UniqueString, (ReadMember(m, "Who") as string) ?? "", RecentOf(hist)));
                                Console.WriteLine("[poll] QUEUED seq=" + _seq + " who=" + w + " text=" + (m.Message ?? ""));
                            }
                            firstSeen.Add(w);
                            while (_inbound.Count > 500)
                            {
                                if (_inbound.TryDequeue(out var lost))
                                    Console.WriteLine("[poll] DROP 队列溢出丢弃 seq=" + lost.seq + " who=" + lost.who);
                            }
                            SaveSeen();
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine("[poll] error: " + ex.Message); }
                finally { _uiLock.Release(); Interlocked.Exchange(ref _busy, 0); }
            }
            try { await Task.Delay(300, _monCts.Token); } catch { break; }
        }
    });
    return true;
}

var JSON = new JsonSerializerOptions
{
    ReferenceHandler = ReferenceHandler.IgnoreCycles,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    WriteIndented = false,
    MaxDepth = 16
};
IResult J(object o) => Results.Content(JsonSerializer.Serialize(o, JSON), "application/json; charset=utf-8");
IResult ERR(Exception ex) { Console.WriteLine("[ERR] " + ex); return J(new { ok = false, error = ex.GetType().Name + ": " + ex.Message }); }

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers["X-Token"].ToString() != TOKEN)
    {
        ctx.Response.StatusCode = 401;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("{\"ok\":false,\"error\":\"bad token\"}");
        return;
    }
    await next();
});

app.MapGet("/", () => J(new
{
    ok = true,
    service = "wxapi",
    port = 18800,
    endpoints = new[]
    {
        "GET  /api/status",
        "GET  /api/friends",
        "GET  /api/conversations",
        "GET  /api/visible",
        "GET  /api/history?who=&date=yyyy-MM-dd",
        "GET  /api/search?who=",
        "POST /api/send      {who,text}",
        "POST /api/sendfile  {who,files[]}",
        "POST /api/top       {who,on}",
        "POST /api/dnd       {who,on}",
        "POST /api/monitor/start {who}",
        "POST /api/monitor/stop",
        "GET  /api/monitor/status",
        "GET  /api/inbound?after=0",
        "POST /api/friend/accept",
        "GET  /api/group/members?name=",
        "POST /api/group/create {name,firstWho,members[]}",
        "POST /api/group/invite {name,members[],reason}",
        "POST /api/group/quit   {name,clearHistory}"
    }
}));

app.MapGet("/api/status", () =>
{
    try
    {
        var c = C();
        var o = c.GetOwerInfo();
        return J(new { ok = true, online = o != null, nick = o?.NickName, wxid = o?.WxId, instance = _self });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapGet("/api/friends", async () =>
{
    try
    {
        var c = C();
        var l = await c.GetAllFriends(false);
        return J(new { ok = true, count = l.Count, friends = l });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapGet("/api/conversations", async () =>
{
    try
    {
        var c = C();
        var l = await c.GetAllConversations();
        return J(new { ok = true, count = l.Count, items = l });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapGet("/api/visible", async () =>
{
    try
    {
        var c = C();
        var l = await c.GetVisibleConversations();
        return J(new { ok = true, count = l.Count, items = l });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapGet("/api/history", async (string who, string date) =>
{
    try
    {
        var c = C();
        var d = string.IsNullOrEmpty(date) ? DateTime.Today : DateTime.Parse(date);
        var l = string.IsNullOrEmpty(who) ? await c.GetChatHistory(d) : await c.GetChatHistory(who, d);
        return J(new { ok = true, count = l?.Count, items = l });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapGet("/api/search", async (string who) =>
{
    try
    {
        var c = C();
        var r = await c.SearchFriend(who);
        return J(new { ok = true, found = r });
    }
    catch (Exception ex) { return ERR(ex); }
});

// ---- 微信收到的文件其实已经落盘了，直接去磁盘上找，不用逆向 ----
string WxFileRoot()
{
    var env = Environment.GetEnvironmentVariable("WX_FILE_ROOT");
    if (!string.IsNullOrWhiteSpace(env)) return env;
    var doc = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    return Path.Combine(doc, "xwechat_files");
}

app.MapGet("/api/file/find", (string name, int? limit) =>
{
    try
    {
        var root = WxFileRoot();
        if (!Directory.Exists(root))
            return J(new { ok = false, error = "root not found", root });
        var key = (name ?? "").Trim();
        if (key.Length == 0) return J(new { ok = false, error = "name required" });

        int lim = Math.Clamp(limit ?? 10, 1, 50);
        var hits = new List<(string path, string name, long size, string mtime)>();
        // 只看最近 4 个月目录，避免全盘扫
        var months = Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .Where(d => d.Contains("\\msg\\file\\") || d.Contains("\\msg\\attach\\"))
            .OrderByDescending(d => d)
            .Take(8)
            .ToList();
        foreach (var d in months)
        {
            IEnumerable<string> fs;
            try { fs = Directory.GetFiles(d); } catch { continue; }
            foreach (var f in fs)
            {
                var fn = Path.GetFileName(f);
                if (fn.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var fi = new FileInfo(f);
                hits.Add((f, fn, fi.Length, fi.LastWriteTime.ToString("s")));
            }
        }
        var list = hits.OrderByDescending(x => x.mtime).Take(lim)
                       .Select(x => new { path = x.path, name = x.name, size = x.size, mtime = x.mtime })
                       .ToList();
        return J(new { ok = true, root, count = list.Count, items = list });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapGet("/api/file/get", (string path) =>
{
    try
    {
        var root = Path.GetFullPath(WxFileRoot());
        var full = Path.GetFullPath(path ?? "");
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return J(new { ok = false, error = "只允许读微信目录下的文件" });
        if (!File.Exists(full)) return J(new { ok = false, error = "not found" });
        var fi = new FileInfo(full);
        if (fi.Length > 30 * 1024 * 1024) return J(new { ok = false, error = "文件超过 30MB" });
        var b64 = Convert.ToBase64String(File.ReadAllBytes(full));
        return J(new { ok = true, name = fi.Name, size = fi.Length, b64 });
    }
    catch (Exception ex) { return ERR(ex); }
});

// ---- 右键气泡并截图，用来肉眼确认菜单里有什么 ----
// ---- 语音转文字 ----
// 微信气泡里的「转文字」是 mmui 自绘的，UIA 里查不到（子孙元素为 0），
// 只能按气泡坐标点：那一行里转文字按钮正好落在矩形横向 50% 的位置。
app.MapGet("/api/voice/text", async (string who, int? index, int? wait) =>
{
    await _uiLock.WaitAsync();
    try
    {
        var c = C();
        if (!string.IsNullOrWhiteSpace(who))
        {
            try { await c.GetChatHistory(who, DateTime.Today); } catch { }
            await Task.Delay(500);
        }
        try { await c.Focus(); await Task.Delay(300); } catch { }

        // 气泡名字列表：用「名字差集」判断转录结果，比下标可靠（点击后列表会位移）
        List<string> Names()
        {
            var its = GetItems(GetMB(c));
            var l = new List<string>();
            for (int i = 0; i < its.Length; i++)
                l.Add(((ReadMember(its.GetValue(i)!, "Name") as string) ?? "").Trim());
            return l;
        }

        var items = GetItems(GetMB(c));
        if (items.Length == 0) return J(new { ok = false, error = "no bubbles" });

        int idx = -1;
        if (index.HasValue)
        {
            idx = index.Value < 0 ? items.Length + index.Value : index.Value;
        }
        else
        {
            for (int i = items.Length - 1; i >= 0; i--)
            {
                var cl0 = (ReadMember(items.GetValue(i)!, "ClassName") as string) ?? "";
                if (cl0.Contains("Voice")) { idx = i; break; }
            }
        }
        if (idx < 0 || idx >= items.Length) return J(new { ok = false, error = "no voice bubble" });

        var rc = ReadMember(items.GetValue(idx)!, "BoundingRectangle");
        int x = Convert.ToInt32(ReadMember(rc, "X") ?? 0);
        int y = Convert.ToInt32(ReadMember(rc, "Y") ?? 0);
        int w = Convert.ToInt32(ReadMember(rc, "Width") ?? 0);
        int h = Convert.ToInt32(ReadMember(rc, "Height") ?? 0);
        if (w <= 0 || h <= 0) return J(new { ok = false, error = "bad rect" });

        var beforeNames = Names();
        string before = idx < beforeNames.Count ? beforeNames[idx] : "";
        int secs = Math.Clamp(wait ?? 8, 2, 20);
        string transcript = "";
        int usedX = 0;

        // 「秒」后面就是内容：语音2"秒哈喽哈喽，听得到吗？
        string TailOf(string nm)
        {
            var k = (nm ?? "").IndexOf("秒");
            if (k < 0) return "";
            var t = nm.Substring(k + 1).Trim();
            if (t.StartsWith("未播放")) t = t.Substring(3).Trim();
            return t;
        }

        transcript = TailOf(before);
        if (transcript.Length > 0)
            return J(new { ok = true, clicked = new { x = 0, y = 0 }, before, transcript, note = "这条已经转过了，直接读的名字" });

        // 悬停才显出「转文字」，位置大约在整行宽度的 50%~58%
        foreach (var f in new double[] { 0.52, 0.58, 0.46 })
        {
            int cx = x + (int)(w * f);
            int cy = y + h / 2;
            usedX = cx;
            SetCursorPos(cx, cy);
            await Task.Delay(400);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(60);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);

            for (int t = 0; t < secs * 2; t++)
            {
                await Task.Delay(500);
                List<string> now;
                try { now = Names(); } catch { continue; }
                foreach (var nm in now)
                {
                    if (string.IsNullOrWhiteSpace(nm)) continue;
                    if (beforeNames.Contains(nm)) continue;
                    var tl = TailOf(nm);
                    if (tl.Length > 0) { transcript = tl; break; }
                }
                if (transcript.Length > 0) break;
            }
            if (transcript.Length > 0) break;
        }

        return J(new
        {
            ok = transcript.Length > 0,
            clicked = new { x = usedX, y = y + h / 2 },
            before,
            transcript,
            note = transcript.Length > 0 ? "" : "点了没等到转录文本（可能是纯音乐、静音，或按钮位置变了）"
        });
    }
    catch (Exception ex) { return ERR(ex); }
    finally { _uiLock.Release(); }
});

// 反射枚举子孙元素：语音气泡旁边那个「转文字」到底是不是 UIA 元素，靠这个确认
app.MapGet("/api/desc", async (string who, int? index, int? depth) =>
{
    await _uiLock.WaitAsync();
    try
    {
        var c = C();
        if (!string.IsNullOrWhiteSpace(who))
        {
            try { await c.GetChatHistory(who, DateTime.Today); } catch { }
            await Task.Delay(500);
        }
        var items = GetItems(GetMB(c));
        if (items.Length == 0) return J(new { ok = false, error = "no bubbles" });
        int idx0 = index ?? -1;
        int idx = idx0 < 0 ? items.Length + idx0 : idx0;
        if (idx < 0 || idx >= items.Length) return J(new { ok = false, error = "index out of range" });

        var root = items.GetValue(idx)!;
        int maxDepth = Math.Clamp(depth ?? 2, 1, 4);
        var outp = new List<object>();
        var mFindAll = root.GetType().GetMethod("FindAllDescendants", Type.EmptyTypes);
        if (mFindAll == null) return J(new { ok = false, error = "no FindAllDescendants" });
        var kids = (System.Collections.IEnumerable?)mFindAll.Invoke(root, null);
        if (kids != null)
        {
            int n = 0;
            foreach (var k in kids)
            {
                if (k == null) continue;
                if (++n > 80) break;
                var rc = ReadMember(k, "BoundingRectangle");
                outp.Add(new
                {
                    cls = ReadMember(k, "ClassName") as string ?? "",
                    name = ((ReadMember(k, "Name") as string) ?? "").Trim(),
                    type = ReadMember(k, "ControlType")?.ToString() ?? "",
                    x = Convert.ToInt32(ReadMember(rc, "X") ?? 0),
                    y = Convert.ToInt32(ReadMember(rc, "Y") ?? 0),
                    w = Convert.ToInt32(ReadMember(rc, "Width") ?? 0),
                    h = Convert.ToInt32(ReadMember(rc, "Height") ?? 0)
                });
            }
        }
        var rct = ReadMember(root, "BoundingRectangle");
        return J(new
        {
            ok = true,
            owner = new
            {
                cls = ReadMember(root, "ClassName") as string ?? "",
                name = ((ReadMember(root, "Name") as string) ?? "").Trim(),
                x = Convert.ToInt32(ReadMember(rct, "X") ?? 0),
                y = Convert.ToInt32(ReadMember(rct, "Y") ?? 0),
                w = Convert.ToInt32(ReadMember(rct, "Width") ?? 0),
                h = Convert.ToInt32(ReadMember(rct, "Height") ?? 0)
            },
            count = outp.Count,
            kids = outp
        });
    }
    catch (Exception ex) { return ERR(ex); }
    finally { _uiLock.Release(); }
});

app.MapGet("/api/rightclick", async (string who, int? index, int? pad) =>
{
    await _uiLock.WaitAsync();
    try
    {
        var c = C();
        if (!string.IsNullOrWhiteSpace(who))
        {
            try { await c.GetChatHistory(who, DateTime.Today); } catch { }
            await Task.Delay(500);
        }
        try { await c.Focus(); await Task.Delay(500); } catch { }

        var items = GetItems(GetMB(c));
        if (items.Length == 0) return J(new { ok = false, error = "no bubbles" });
        int idx0 = index ?? -1;
        int idx = idx0 < 0 ? items.Length + idx0 : idx0;
        if (idx < 0 || idx >= items.Length) return J(new { ok = false, error = "index out of range" });

        var rect = ReadMember(items.GetValue(idx)!, "BoundingRectangle");
        int x = Convert.ToInt32(ReadMember(rect, "X") ?? 0);
        int y = Convert.ToInt32(ReadMember(rect, "Y") ?? 0);
        int w = Convert.ToInt32(ReadMember(rect, "Width") ?? 0);
        int h = Convert.ToInt32(ReadMember(rect, "Height") ?? 0);
        if (w <= 0 || h <= 0) return J(new { ok = false, error = "bad rect" });

        int cx = x + w / 2, cy = y + h / 2;
        SetCursorPos(cx, cy);
        await Task.Delay(150);
        mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(60);
        mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(700);

        int px = pad ?? 420;
        int sx = Math.Max(0, cx - px / 3);
        int sy = Math.Max(0, cy - px / 3);
        int sw = Math.Min(px, Math.Max(200, 1920 - sx));
        int sh = Math.Min(px, Math.Max(200, 1080 - sy));
        using var bmp = new System.Drawing.Bitmap(sw, sh, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(sx, sy, 0, 0, new System.Drawing.Size(sw, sh));
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return J(new { ok = true, x = cx, y = cy, sx, sy, sw, sh, png = Convert.ToBase64String(ms.ToArray()) });
    }
    catch (Exception ex) { return ERR(ex); }
    finally { _uiLock.Release(); }
});

app.MapGet("/api/key", (int code) =>
{
    try
    {
        keybd_event((byte)code, 0, 0, UIntPtr.Zero);
        keybd_event((byte)code, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        return J(new { ok = true, vk = code });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapPost("/api/send", async (M m) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(m.who) || string.IsNullOrEmpty(m.text))
            return J(new { ok = false, error = "who/text 不能为空" });
        var c = C();
        // ⚠️ SDK 的 SendMessage 在找不到会话时**既不报错也不发**，静默返回成功
        // （实测：发给「__不存在的会话__」，接口回 ok:true，微信里什么都没发生）。
        // 这里只留个痕迹方便排查，不用它拦截 —— visible 列表可能有分页，
        // 误拦掉一条正常消息的代价比漏记一条日志大得多。
        try
        {
            var vis = await c.GetVisibleConversations();
            if (vis != null && vis.Count > 0 &&
                !vis.Any(v => (v.ConversationTitle ?? "") == m.who))
                Console.WriteLine("[send] WARN 目标会话不在可见列表: " + m.who);
        }
        catch { }
        await c.SendMessage(m.who, m.text);
        MarkSent(m.text);
        return J(new { ok = true });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapPost("/api/sendfile", async (F f) =>
{
    try
    {
        var c = C();
        await c.SendFile(f.who, f.files);
        return J(new { ok = true });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapPost("/api/top", async (B b) =>
{
    try
    {
        var c = C();
        await c.SetTopMost(b.who, b.on);
        return J(new { ok = true });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapPost("/api/dnd", async (B b) =>
{
    try
    {
        var c = C();
        await c.SetDoNotDisturb(b.who, b.on);
        return J(new { ok = true });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapPost("/api/monitor/start", async (MQ q) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(q.who)) return J(new { ok = false, error = "who 不能为空" });
        var added = new List<string>();
        foreach (var w in q.who.Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (_monWho.Contains(w)) continue;
            var r = await SubmitMonitor(w);
            if (r) { _monWho.Add(w); added.Add(w); }
        }
        return J(new { ok = true, added, listening = _monWho });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapPost("/api/monitor/stop", () =>
{
    try
    {
        _monCts.Cancel();
        _monWho.Clear();
        return J(new { ok = true, note = "cancelled" });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapGet("/api/monitor/status", () =>
{
    return J(new { ok = true, listening = _monWho, seq = _seq, queued = _inbound.Count, seen = _seen.Count });
});

app.MapGet("/api/inbound", (long after) =>
{
    var items = _inbound.Where(x => x.seq > after).OrderBy(x => x.seq).Take(50).ToList();
    // epoch = 本进程的启动时间。客户端靠它判断"服务重启了、seq 归零了"，
    // 而不是靠 last：队列空的时候 last 也是 0，跟重启长得一模一样，
    // 客户端会被骗着反复把游标重置回 0，然后把整条队列重拉一遍。
    return J(new { ok = true, count = items.Count, last = _seq, epoch = _epoch, items });
});

app.MapPost("/api/friend/accept", async () =>
{
    try
    {
        var c = C();
        var opt = new FriendRequestAutoAcceptOptions();
        var passed = await c.PassedAllNewFriend(opt, CancellationToken.None);
        return J(new { ok = true, passedCount = passed?.Count ?? 0, passed });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapPost("/api/friend/listen", () =>
{
    try
    {
        var c = C();
        var fopt = new FriendRequestAutoAcceptOptions();
        _ = Task.Run(async () =>
        {
            try
            {
                await c.NewFriendMonitor.AddFriendRequestAutoAcceptListener(fopt, _monCts.Token, inv => { });
                Console.WriteLine("[friend] listener returned");
            }
            catch (Exception ex) { Console.WriteLine("[friend] listener error: " + ex.Message); }
        });
        return J(new { ok = true, note = "auto-accept listener submitted" });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapGet("/api/group/members", async (string name) =>
{
    try
    {
        var c = C();
        var l = await c.GetChatGroupMemberList(name);
        return J(new { ok = true, members = l });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapPost("/api/group/create", async (GC g) =>
{
    try
    {
        var c = C();
        var r = await c.CreateOwnerChatGroup(g.name, g.firstWho, g.members);
        return J(new { ok = true, result = r });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapPost("/api/group/invite", async (GI g) =>
{
    try
    {
        var c = C();
        var r = await c.InviteChatGroupMember(g.name, g.members.ToList(), g.reason);
        return J(new { ok = true, result = r });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapPost("/api/group/quit", async (GQ g) =>
{
    try
    {
        var c = C();
        await c.QuitChatGroup(g.name, g.clearHistory);
        return J(new { ok = true });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapGet("/api/bubbles", async (string who) =>
{
    await _uiLock.WaitAsync();
    try
    {
        var c = C();
        if (!string.IsNullOrWhiteSpace(who))
        {
            try { await c.GetChatHistory(who, DateTime.Today); } catch { }
            await Task.Delay(500);
        }
        var mb = GetMB(c);
        var items = GetItems(mb);
        var list = new List<object>();
        for (int i = 0; i < items.Length; i++)
        {
            var it = items.GetValue(i)!;
            string nm = ReadMember(it, "Name") as string ?? "";
            string cls = ReadMember(it, "ClassName") as string ?? "";
            string ct = "";
            try { ct = ReadMember(it, "ControlType")?.ToString() ?? ""; } catch { }
            if (nm.Length > 200) nm = nm.Substring(0, 200);
            var rc = ReadMember(it, "BoundingRectangle");
            int rx = Convert.ToInt32(ReadMember(rc, "X") ?? 0);
            int ry = Convert.ToInt32(ReadMember(rc, "Y") ?? 0);
            int rw = Convert.ToInt32(ReadMember(rc, "Width") ?? 0);
            int rh = Convert.ToInt32(ReadMember(rc, "Height") ?? 0);
            list.Add(new { i, type = ct, cls, name = nm, x = rx, y = ry, w = rw, h = rh });
        }
        return J(new { ok = true, count = items.Length, items = list });
    }
    catch (Exception ex) { return ERR(ex); }
    finally { _uiLock.Release(); }
});

app.MapGet("/api/image", async (string who, int? index) =>
{
    try
    {
        var c = C();
        if (!string.IsNullOrWhiteSpace(who))
        {
            try { await c.GetChatHistory(who, DateTime.Today); } catch { }
            await Task.Delay(500);
        }
        var mb = GetMB(c);
        var items = GetItems(mb);
        if (items.Length == 0) return J(new { ok = false, error = "no bubbles" });
        int idx0 = index ?? -1;
        int idx = idx0 < 0 ? items.Length + idx0 : idx0;
        if (idx < 0 || idx >= items.Length) return J(new { ok = false, error = "index out of range", count = items.Length });
        var it = items.GetValue(idx)!;
        var kids = new List<string>();
        try
        {
            var fm = it.GetType().GetMethod("FindAllChildren", Type.EmptyTypes);
            var arr = fm?.Invoke(it, null) as Array;
            if (arr != null)
                for (int k = 0; k < arr.Length; k++)
                {
                    var ck = arr.GetValue(k)!;
                    var kn = ReadMember(ck, "Name") as string ?? "";
                    if (kn.Length > 60) kn = kn.Substring(0, 60);
                    kids.Add((ReadMember(ck, "ClassName") as string ?? "?") + "|" + kn);
                }
        }
        catch { }
        try { await c.Focus(); await Task.Delay(500); } catch { }
        var fetch = mb.GetType().GetMethod("FetchImageAsync",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (fetch == null) return J(new { ok = false, error = "FetchImageAsync not found" });
        var task = (Task)fetch.Invoke(mb, new object?[] { it, CancellationToken.None })!;
        await task;
        var res = task.GetType().GetProperty("Result")!.GetValue(task);
        string fp = res?.GetType().GetProperty("FilePath")?.GetValue(res) as string ?? "";
        string b64 = res?.GetType().GetProperty("Base64Str")?.GetValue(res) as string ?? "";
        string er = res?.GetType().GetProperty("Error")?.GetValue(res) as string ?? "";
        long size = 0;
        if (!string.IsNullOrEmpty(fp) && File.Exists(fp)) size = new FileInfo(fp).Length;
        return J(new
        {
            ok = string.IsNullOrEmpty(er),
            count = items.Length,
            idx,
            bubbleName = (ReadMember(it, "Name") as string ?? ""),
            filePath = fp,
            fileSize = size,
            base64Len = b64.Length,
            error = er,
            kids = kids.ToArray()
        });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapGet("/api/shot", async (string who, int? index, int? pad) =>
{
    await _uiLock.WaitAsync();
    try
    {
        var c = C();
        if (!string.IsNullOrWhiteSpace(who))
        {
            try { await c.GetChatHistory(who, DateTime.Today); } catch { }
            await Task.Delay(500);
        }
        try { await c.Focus(); await Task.Delay(700); } catch { }

        var mb = GetMB(c);
        var items = GetItems(mb);
        if (items.Length == 0) return J(new { ok = false, error = "no bubbles" });
        int idx0 = index ?? -1;
        int idx = idx0 < 0 ? items.Length + idx0 : idx0;
        if (idx < 0 || idx >= items.Length)
            return J(new { ok = false, error = "index out of range", count = items.Length });

        var it = items.GetValue(idx)!;
        var rect = ReadMember(it, "BoundingRectangle");
        if (rect == null) return J(new { ok = false, error = "no BoundingRectangle" });

        int x = Convert.ToInt32(ReadMember(rect, "X") ?? 0);
        int y = Convert.ToInt32(ReadMember(rect, "Y") ?? 0);
        int w = Convert.ToInt32(ReadMember(rect, "Width") ?? 0);
        int h = Convert.ToInt32(ReadMember(rect, "Height") ?? 0);
        if (w <= 0 || h <= 0)
            return J(new { ok = false, error = "bad rect", x, y, w, h });

        int pad0 = pad ?? 4;
        int p = pad0 <= 0 ? 4 : Math.Min(pad0, 40);
        x = Math.Max(0, x - p);
        y = Math.Max(0, y - p);
        w += p * 2;
        h += p * 2;

        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h),
                             System.Drawing.CopyPixelOperation.SourceCopy);
        }
        var dir = Path.Combine(Path.GetTempPath(), "wxshot");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "shot_" + DateTime.Now.ToString("HHmmssfff") + ".png");
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);

        var raw = File.ReadAllBytes(path);
        var b64 = Convert.ToBase64String(raw);
        return J(new
        {
            ok = true,
            count = items.Length,
            idx,
            bubbleName = ReadMember(it, "Name") as string ?? "",
            bubbleCls = ReadMember(it, "ClassName") as string ?? "",
            x, y, w, h,
            filePath = path,
            fileSize = raw.Length,
            base64Len = b64.Length,
            base64 = b64
        });
    }
    catch (Exception ex) { return ERR(ex); }
    finally { _uiLock.Release(); }
});

app.MapGet("/api/history/deep", async (string who, int? pages, int? limit) =>
{
    await _uiLock.WaitAsync();
    try
    {
        var c = C();
        if (string.IsNullOrWhiteSpace(who))
            return J(new { ok = false, error = "who 不能为空" });

        await c.GetChatHistory(who, DateTime.Today);
        await Task.Delay(400);
        try { await c.Focus(); await Task.Delay(400); } catch { }

        int pg = Math.Clamp(pages ?? 3, 1, 12);
        int lim = Math.Clamp(limit ?? 60, 10, 200);

        var mb = GetMB(c);
        var buf = new List<(string cls, string name)>();     // 给外面看的（真实 cls/name）
        var sig = new List<(string cls, string name)>();     // 只用来判重叠
        int rounds = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int round = 0; round < pg; round++)
        {
            if (sw.Elapsed.TotalSeconds > 55) break;   // 硬上限，别把整个 API 拖住
            var items = GetItems(mb);
            if (items.Length == 0) break;

            var cur = new List<(string, string)>();
            var cursig = new List<(string, string)>();   // 判重叠用：不含时间分隔条
            for (int i = 0; i < items.Length; i++)
            {
                var it = items.GetValue(i)!;
                string scls = (ReadMember(it, "ClassName") as string) ?? "";
                string snm = (ReadMember(it, "Name") as string) ?? "";
                cur.Add((scls, snm));
                // 时间分隔条（mmui::ChatItemView）的 "19:39" 每屏都会重复出现，
                // 拿它判重叠必然错位 → 完全排除在签名之外，只在输出里保留
                bool isSep = scls.EndsWith("ChatItemView")
                             && !scls.Contains("ChatTextItemView")
                             && !scls.Contains("ChatBubble");
                if (!isSep) cursig.Add((scls, snm));
            }

            // 找「上一屏尾部」与「这一屏开头」的最大重叠（只数真实消息）
            int best = 0;
            for (int k = Math.Min(sig.Count, cursig.Count); k > 0; k--)
            {
                bool ok = true;
                for (int j = 0; j < k; j++)
                {
                    if (sig[sig.Count - k + j] != cursig[j]) { ok = false; break; }
                }
                if (ok) { best = k; break; }
            }

            // 把「重叠 best 条真实消息」换算成 cur 的下标，分隔条不计数但保留
            int skip = 0;
            if (best > 0)
            {
                int seenReal = 0;
                for (int i = 0; i < cur.Count; i++)
                {
                    string c0 = cur[i].Item1;
                    bool sep = c0.EndsWith("ChatItemView")
                               && !c0.Contains("ChatTextItemView")
                               && !c0.Contains("ChatBubble");
                    if (!sep) seenReal++;
                    if (seenReal >= best) { skip = i + 1; break; }
                }
                if (skip == 0) skip = cur.Count;
            }

            if (best == 0 && buf.Count > 0)
            {
                buf.InsertRange(0, cur);            // 无重叠：这一屏更早，插到前面
                sig.InsertRange(0, cursig);
            }
            else
            {
                buf.AddRange(cur.Skip(skip));       // 有重叠：只补后面新增的
                sig.AddRange(cursig.Skip(best));
            }
            rounds++;

            var rect = ReadMember(items.GetValue(0)!, "BoundingRectangle");
            int cx = Convert.ToInt32(ReadMember(rect, "X") ?? 0) + 40;
            int cy = Convert.ToInt32(ReadMember(rect, "Y") ?? 0) + 20;
            if (cx <= 0 || cy <= 0) break;
            SetCursorPos(cx, cy);
            for (int w = 0; w < 6; w++)
            {
                mouse_event(MOUSEEVENTF_WHEEL, 0, 0, 120, UIntPtr.Zero);
                await Task.Delay(90);
            }
            await Task.Delay(700);
        }

        var tail = buf.Count > lim ? buf.Skip(buf.Count - lim).ToList() : buf;
        return J(new
        {
            ok = true,
            rounds,
            took = Math.Round(sw.Elapsed.TotalSeconds, 1),
            count = tail.Count,
            items = tail.Select(x => new { type = x.cls, text = x.name }).ToList()
        });
    }
    catch (Exception ex) { return ERR(ex); }
    finally { _uiLock.Release(); }
});

// 每次发送用独立子目录：两个会话同时发「报告.pdf」时，共用一个文件名会互相覆盖，
// 先写的还没发出去就被后写的盖掉 —— 结果把 A 的文件发给了 B。
// 顺带把一小时前的旧目录清掉，否则 Temp 会一直涨。
string NewWxOutDir()
{
    var root = Path.Combine(Path.GetTempPath(), "wxout");
    Directory.CreateDirectory(root);
    try
    {
        var cutoff = DateTime.Now.AddHours(-1);
        foreach (var d in Directory.GetDirectories(root))
        {
            try { if (Directory.GetLastWriteTime(d) < cutoff) Directory.Delete(d, true); }
            catch { }
        }
    }
    catch { }
    var dir = Path.Combine(root, Guid.NewGuid().ToString("N").Substring(0, 12));
    Directory.CreateDirectory(dir);
    return dir;
}

app.MapPost("/api/upload", async (U u) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(u.name) || string.IsNullOrEmpty(u.data))
            return J(new { ok = false, error = "name/data 不能为空" });
        var dir = NewWxOutDir();
        var safe = string.Join("_", u.name.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(dir, safe);
        var bytes = Convert.FromBase64String(u.data);
        await File.WriteAllBytesAsync(path, bytes);
        return J(new { ok = true, path, size = bytes.Length });
    }
    catch (Exception ex) { return ERR(ex); }
});

app.MapPost("/api/upload/send", async (US u) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(u.who) || u.files == null || u.files.Length == 0)
            return J(new { ok = false, error = "who/files 不能为空" });
        var dir = NewWxOutDir();
        var saved = new List<string>();
        foreach (var f in u.files)
        {
            if (string.IsNullOrEmpty(f.name) || f.data == null) continue;
            var safe = string.Join("_", f.name.Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(dir, safe);
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(f.data));
            saved.Add(path);
        }
        if (saved.Count == 0) return J(new { ok = false, error = "没有有效文件" });
        var c = C();
        await c.SendFile(u.who, saved.ToArray());
        return J(new { ok = true, sent = saved });
    }
    catch (Exception ex) { return ERR(ex); }
});

_ = Task.Run(async () =>
{
    // 启动时 SDK 偶尔会因为枚举抖动抛错（例如同一实例名出现两次），
    // 之前一次失败就等于整轮没有监听，微信里的新消息再没人接。
    // 这里改成最多试 6 次，直到每个会话都注册成功为止。
    var autoWho = Environment.GetEnvironmentVariable("WXAPI_MONITOR_WHO");
    var wants = string.IsNullOrWhiteSpace(autoWho)
        ? new string[0]
        : autoWho.Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    for (int attempt = 1; attempt <= 6; attempt++)
    {
        try
        {
            await Task.Delay(attempt == 1 ? 2500 : 6000);
            C();
            Console.WriteLine("[warm] try " + attempt + " ready: instance=" + _self);
            var allOk = true;
            foreach (var w in wants)
            {
                if (_monWho.Contains(w)) continue;
                try
                {
                    var okm = await SubmitMonitor(w);
                    if (okm && !_monWho.Contains(w)) _monWho.Add(w);
                    Console.WriteLine("[warm] monitor " + w + " -> " + okm);
                    if (!okm) allOk = false;
                }
                catch (Exception ex)
                {
                    allOk = false;
                    Console.WriteLine("[warm] monitor failed for " + w + ": " + ex.Message);
                }
            }
            if (allOk && wants.Length > 0) break;
        }
        catch (Exception ex) { Console.WriteLine("[warm] init error (try " + attempt + "): " + ex.Message); }
    }
});

_ = Task.Run(async () =>
{
    try
    {
        await Task.Delay(8000);
        var fopt = new FriendRequestAutoAcceptOptions();
        await C().NewFriendMonitor.AddFriendRequestAutoAcceptListener(fopt, _monCts.Token, inv => { });
        Console.WriteLine("[friend] listener running");
    }
    catch (Exception ex) { Console.WriteLine("[friend] listener error: " + ex.Message); }
});

Console.WriteLine("[wxapi] listening http://0.0.0.0:18800");
app.Run();

record M(string who, string text);
record F(string who, string[] files);
record B(string who, bool on);
record MQ(string who);
record GC(string name, string firstWho, string[] members);
record GI(string name, string[] members, string reason);
record GQ(string name, bool clearHistory);
record Inbound(long seq, string who, string text, string time, string uniq, string sender, List<object> recent);
record U(string name, string data);
record UF(string name, string data);
record US(string who, UF[] files);
