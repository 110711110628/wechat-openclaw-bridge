# 架构与实现细节

## 为什么是「三层」

```
Windows VM  ──  wxapi   :18800   只做「和微信打交道」这一件事
Linux host  ──  wxbridge          只做「消息调度 + agent 调用 + 文件回传」
OpenClaw    ──  agents            只负责「想」和「做」
```

拆成三层的直接原因：**微信必须在 Windows 上跑，而 agent 生态（Node、文件系统、工具链）在 Linux 上更顺手**。
中间的 HTTP 层让两边解耦 —— Windows 那台随时可以重装、重启、换 IP，桥只需要改一个环境变量。

## 收消息：为什么不用 MessageMonitor

SDK 里有个 `MessageMonitor`，看起来正是为此设计的。实际情况：

- 该类**只有构造函数和 `Dispose`**，没有任何公开的启动入口
- 底层走 `SystemMonitorChannel`，类型是 `Channel<(SystemMonitorOption, List<string>)>`，
  往里写数据**回调也不会触发** —— 消费循环根本没启动

于是退回到**未读轮询**：

```csharp
var vis = await c.GetVisibleConversations();          // ~50ms
var targets = vis.Where(x => x.NotReadNumbr > 0);
foreach (var t in targets)
{
    var hist = await c.GetChatHistory(t.ConversationTitle, DateTime.Today);  // ~7s
    // 取最后 6 条，UniqueString 去重
}
```

`GetVisibleConversations()` 只要 50ms，`GetChatHistory()` 要 7 秒，所以**绝不能读全部会话**，只能读有未读的。

### ⛔ 这里有个必须避开的陷阱

**启动时不能先读一遍历史做基线。** 读历史 = 打开会话 = 把未读清零，
之后 `NotReadNumbr` 永远是 0，那条消息就再也检测不到了。

正确做法是把「见过哪些消息」持久化（`seen.json`），靠 `UniqueString` 判断新旧，
而不是靠未读状态。

### 启动补扫（catch-up）

wxapi 重启期间到达的消息，未读可能被别的东西（人、脚本、调试）读掉。所以启动时：

1. 载入 `seen.json`，如果为空 → 这是首次运行，**只建基线不补发**，避免把一屏旧消息全回一遍
2. 否则把所有可见会话各读 6 条，凡是不在 `seen` 里的，入队补发
3. 补扫耗时约 `会话数 × 7s`，7 个会话大概 50 秒，这段时间不处理新消息（只在启动时一次）

## 发消息

文本走 `SendText(who, text)`；文件走 `SendFile(who, paths[])`。
注意 `SendFile` 要的是 **Windows 本地路径**，所以远端（Linux 上的桥）发文件必须：

```
Linux 文件 → base64 → POST /api/upload/send → VM 落盘到 %TEMP%\wxout → SendFile → 归档发送记录
```

## 文件回传（agent → 微信）

agent 不能直接调 wxapi（那样要给它网络工具和 token）。走**目录约定**：

```
workspace/out        主人收到的文件
workspace-chat/out   访客收到的文件
workspace-group/out  群文件（当前禁用写入）
```

桥在每轮 `ask()` 前后记时间戳 `t0`，回答发完后扫描 `out/` 里 `mtime >= t0 - 1` 的文件，
base64 上传发送，成功后挪到 `out/sent/`。

agent 不会自己想到用 `out/`，**必须写进 `IDENTITY.md` / `USER.md`** 才会照做：

```md
要做成品时：用 write 工具写进 out/ 目录，写完后自然会发到微信上。
文件名简洁带扩展名，正文用 .md 或 .txt，表格用 .csv，要能直接打印用 .html。
做完只说一句"文件发你了"，不要把正文整段贴在聊天里。
```

## 看图：三条路，只能走第三条

| 方案 | 结论 |
|---|---|
| 直接读 `.dat` | ❌ V2 加密，文件头 `07 08 56 32 08 07`，密钥在微信进程内存 → 必须注入，违背前提 |
| `FetchImageAsync(bubble)` | ❌ 内部走「右键 → 复制 → 剪贴板」，加 `Focus()` 后仍报「右键菜单没有弹出」 |
| 截屏 | ✅ 读气泡 `BoundingRectangle` + `CopyFromScreen` |

截屏的取法：

```csharp
var rect = ReadMember(item, "BoundingRectangle");   // 反射，类型是 WPF Rect
int x = rect.X - pad, y = rect.Y - pad;
using var bmp = new Bitmap(w, h);
using var g = Graphics.FromImage(bmp);
g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
```

限制：滚到可视区**顶部**的历史图片，`y` 会变成 0（被标题栏遮住），裁出来是残缺的；
刚收到、还在底部的图片没问题。

### 另一个坑：模型"看不见图"

如果 agent 说看不到图片，先别怀疑模型。检查 `openclaw.json`：

```json
"models": [{ "id": "deepseek-flash", "input": ["text"] }]   // ← 罪魁祸首
```

声明里没有 `"image"`，OpenClaw 就不会把图片送给模型。改成 `["text","image"]` 立刻正常。
（实测同一网关下 `deepseek-flash`、`qwen3.8-max` 能吃图，`glm-5.3` 直接 400，`kimi-k3` 返回空。）

## 长上下文：滚动 + 去重合并

`/api/history` 只有一屏 6~8 条。要更多，只能**模拟鼠标滚动**：

```csharp
SetCursorPos(x, y);                                     // 光标移到气泡上
for (int i = 0; i < 6; i++)
    mouse_event(MOUSEEVENTF_WHEEL, 0, 0, 120, UIntPtr.Zero);
await Task.Delay(700);
```

滚完再读一遍气泡，跟上一轮做**尾部重叠比对**，只追加新增部分。

### 重叠比对的坑

时间分隔条（`mmui::ChatItemView`，内容形如 `21:25`）**会重复出现**。
如果拿它参与比对，会误判「整屏都是重叠」，把新内容全丢掉（实测 24 条降到 8 条）。

解法：判重时把分隔条换成**带轮次编号的唯一标记**，输出时再用真实类型。

## 自愈

UI 自动化偶发死锁。一旦某个端点卡住，wxapi 内部状态被占住，**后续所有请求全部超时**，
但它不会自己恢复 —— 只能重启进程。

三层防护：

1. **调用方短超时 + 冷却**：`/api/history/deep` 客户端 90s，失败后 10 分钟不再用它
2. **看门狗**：桥连续 4 次拉不到 `inbound` → 调 `recover-wxapi.sh`
3. **VM 内重启脚本**：`netstat` 找 PID → `taskkill /F` → `schtasks /end` → `schtasks /run`

注意 `tasklist` 里**没有 `wxapi.exe`**，进程名是 `dotnet.exe`，所以只能按端口找 PID。

## seq 游标

wxapi 的 `_seq` 是内存自增，重启归零；桥的 `cursor` 停在旧的大数值，
于是 `after=cursor` 永远查不到东西，**而且日志一条错误都不报**（只是安静地不干活）。

修法：每次拉取都返回 `last`，桥发现 `last < cursor` 就重置游标：

```python
if last < cursor:
    log("seq reset detected: last=%d cursor=%d -> reset to 0" % (last, cursor))
    cursor = 0
```
