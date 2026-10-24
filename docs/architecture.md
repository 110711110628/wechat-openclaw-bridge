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

解法：判重时**彻底排除分隔条**，只比真实消息；重叠条数按真实消息算完，再换算回
「带分隔条的下标」，输出照旧保留分隔条。

第一版其实写错了 —— 当时把分隔条换成「带轮次编号的唯一标记」，编号每轮都不同，
跨轮比对永远匹配不上，结果整屏被重复追加一次（现象是上下文里同一段话出现两遍）。

```csharp
bool IsSep(string cls) => cls.EndsWith("ChatItemView")
                          && !cls.Contains("ChatTextItemView")
                          && !cls.Contains("ChatBubble");
```

顺带说一句：这个接口现在已经**不在主链路上**。对带 `、` 的群名，
`GetChatHistory(name)` 会打开错误的会话，deep 于是读到别人的聊天记录（见 `docs/pitfalls.md` 31）。
聊天上下文一律用 `/api/history`（带 `Who`、1~7 秒），deep 只留作人工排查。

## 延时到底花在哪（实测分解）

一段 5 秒的等待，拆开是这样：

| 环节 | 实测 | 能不能压 |
|---|---|---|
| wxapi 发现未读（轮询 300ms 一轮） | 0~0.3s | 压不动，再快就是空转 UI |
| `GetChatHistory` 解析这一屏 | 0.3~7s | 会话越大越慢，没法绕 |
| 桥拉队列（`POLL_INTERVAL`） | 0~0.2s | 已从 0.5s 压到 0.2s |
| **`openclaw agent` 进程启动** | **~1.4s** | ⚠️ 每条消息都要重开一个 node |
| 模型往返（`--thinking off`） | 1.3~1.9s | 已压到接近底线 |
| 发回微信 | 0.1~0.5s | 压不动 |

两个结论：

1. **那 1.4 秒是固定开销，跟消息内容无关**。日志显示模型自己只跑 `durationMs: 1334`，
   而端到端 3 秒 —— 差的就是一个 node 进程的启动时间。想再省只能自己写一个常驻的
   gateway 客户端（复用连接、不再 spawn），收益 1.4s、成本是一整层协议实现，不划算。
2. **带工具的回合压不动**。读文件、转格式、跑命令，一分钟以上很正常。这部分只能靠
   「处理中」提示解决体感，不能靠优化 —— 所以桥在 8 秒没结果时会先回一句。

## 会话忙的时候，怎么还能回话

一次 agent 回合要几秒到几分钟。这期间用户很容易再发一条（「顺便问一下…」）。
朴素做法是把它排进同一队列 —— 于是它就等在长任务后面，等于没反应。

现在的分流（`dispatch()`）：

```
同会话 inflight == 0  →  进 worker 队列，正常处理
同会话 inflight >= 1  →  SIDE 线程 + agent:chat:<who>-side 独立会话，立刻回一句
同会话积压 >= 3       →  DROP，只记日志（宁可丢，也不能把 worker 占满让主人一起等）
```

两个关键点：

- **插话必须用独立 session**。复用主会话的 `session-key` 会让两个 agent turn
  同时写同一份历史，上下文互相污染（见 `pitfalls.md` 65）
- **插话只处理一条**。`_side_busy` 保证同一会话同时最多一条插话；
  再多就说明对方在刷屏，这时候回什么都没意义

插话的提示词里不带「你在处理什么细节」，只带一句 `_busy_hint`（那条消息的前 40 字），
让它知道「对方在等什么」就够了。带细节反而会让它开始复述工作过程。

## 短消息分诊

≤16 字、非文件的消息，先花 1~2 秒问一个分诊模型：

| 路由 | 走哪 | 典型输入 |
|---|---|---|
| `skip` | 直接用分诊模型给的一句话回掉，**不起 agent** | 在吗 / 谢谢 / 好的 / 收到 |
| `chat` | 起 `chat` agent，思考关 | 普通问答、要一份文档 |
| `deep` | 起 `chat` agent，保留思考 | 查资料、多步处理、读文件内容 |

- 分诊失败（超时 / 返回非 JSON / 模型抽风）→ **返回 `None`，走原流程**
- 分诊只对「短消息」开，长消息直接进 agent —— 长消息本来就是真事，不该多一次往返
- `WX_ROUTER=0` 可整条关掉。关掉只影响寒暄的快慢，不影响正确性

实测（deepseek-flash）：skip 1.05~1.21s、chat 1.22~1.78s、deep 1.93s。
分诊本身的 1 秒是不赚的，赚的是 **skip 省下的整个 agent 回合（3~5 秒）**。

## 访客怎么「出文件」而不碰文件系统

访客 agent 的 `read/write/edit/ls/glob/grep` 全部 deny（见 `pitfalls.md` 64），
它没法往磁盘写任何东西。要交文件就靠文本协议：

```
[[FILE: 周报模板.docx]]
# 周报
## 一、本周完成
- ...
[[/FILE]]
文件发你了
```

桥在回复发出去之前做三件事（`take_files()`）：

1. **剥掉文件块**，剩下的文字才是要发到微信的内容（全剥空了就补一句「文件发你了」）
2. **清洗文件名**：自己去 `\` 和 `/` 切、过滤 `<>:"|?*` 和控制字符、
   扩展名不在白名单（`.md .txt .csv .html .htm .json .docx`）就改成 `.md`
3. **落盘**：`.docx` 转成真 Word（标题 / 列表 / 表格 / 正文四类）；
   `python-docx` 缺失时降级成 `.md`，**不给出一个坏文件**

路径、扩展名、大小（20 万字符）全部卡在桥这边。所以「给不给访客出文档」和
「给不给访客服务器」是两个独立决定，前者只受这个白名单约束。

## 自愈

UI 自动化偶发死锁。一旦某个端点卡住，wxapi 内部状态被占住，**后续所有请求全部超时**，
但它不会自己恢复 —— 只能重启进程。

三层防护：

1. **调用方短超时 + 冷却**：`/api/history/deep` 客户端 90s，失败后 10 分钟不再用它
2. **看门狗**：桥连续 6 次拉不到 `inbound` → 调 `recover-wxapi.sh`（4 次太容易在冷启动时误判，
   wxapi 刚起那 30~60 秒本来就应答不了）
3. **VM 内重启脚本**：`netstat` 找 PID → `taskkill /F` → `schtasks /end` → `schtasks /run`
4. **宿主 systemd timer**：每 2 分钟探一次 `/api/status`，连探 3 次都不应答才动手。
   这一层防的是「桥自己也停了」—— 前两层都需要桥活着才能触发。

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
