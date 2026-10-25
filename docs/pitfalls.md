# 踩坑清单

按「坑 → 现象 → 原因 → 解法」记。都是真实撞过的，踩一次记一条。

## SDK / UI Automation

### 1. `ChatContent` 是 public **字段**，不是属性

```csharp
c.ChatContent                       // ✔
typeof(WeChatClient).GetProperties() // ✘ 看不到它
typeof(WeChatClient).GetFields()     // ✔ 在这里
```

### 2. `MessageBubbleList` 是 **internal 属性**，必须反射

直接写 `c.ChatContent.MessageBubbleList` 编译报 `CS1061: 未包含该定义`。
正确姿势：

```csharp
var p = ct.GetType().GetProperty("MessageBubbleList",
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
var mb = p.GetValue(ct);
```

`MessageBubbleList.MessageRoot` 是 FlaUI 的 `ListBox`，`.Items` 就是气泡数组。

### 3. `LocateConversation(who)` 是**假方法**

调用后不切换会话，读出来的气泡仍是上一个会话的。两个不同的人返回一模一样的内容，
排查半天才发现是它没生效。

**正解**：用 `GetChatHistory(who, DateTime.Today)` 定位，之后 `Task.Delay(500)` 再读。

### 4. 气泡 `ClassName` 才是可靠的消息类型

`Name` 里的「图片」「动画表情」只是占位文本，且：

- `mmui::ChatTextItemView` — 文本
- `mmui::ChatBubbleReferItemView` — **图片和表情包共用同一个类名**
- `mmui::ChatItemView` — 时间分隔条

区分图片和表情包只能看 `Name`：

| Name | 实际 |
|---|---|
| `图片` | 图片 |
| `动画表情 [大吉大利]` | 表情包（**表情名可读**，这点意外地有用） |

### 5. 精确匹配 `Name == "图片"` 永远失败

UIA 给的字符串不是纯净的，`nm in ("图片","[图片]","【图片】")` 一次都没命中过。
改成 `"图片" in nm` + 排除「表情 / 动画」才对。

### 6. `FetchImageAsync` 在多数环境不可用

加 `await c.Focus()` 能进 `_CheckImageStyle`（对时间条会报「参数为非图片消息」），
但对真实图片气泡**始终报「右键菜单没有弹出」**。别在这上面耗时间，直接截屏。

### 7. 滚到顶部时截屏会被标题栏切

图片气泡的 `BoundingRectangle` 是整个消息组容器。滚到可视区顶部时 `y=0`，意味着被微信
标题栏遮住，截出来是残缺的。刚收到、还在底部的消息没问题。

### 8. `SendFile` 只认 Windows 本地路径

远端调不通。必须先把文件 base64 过去、落盘、再 `SendFile`。

## 监听 / 消息

### 9. ⛔ 启动时读历史做基线 = 永久丢消息

读历史会打开会话，**把未读清零**。之后 `NotReadNumbr` 恒为 0，那条消息再也检测不到。
日志里一条错误都不会有，只是安静地不干活。

**正解**：靠持久化的 `UniqueString` 水位判断新旧，不要靠未读。

### 10. @ 和正文常常是两条消息

```
 @OpenClaw
 @OpenClaw⁤在吗
```

如果每次只取最后 1 条，就会拿到「在吗」而丢掉「@OpenClaw」，判定为「没被 @」。
所以统一取 6 条再逐条判断。

### 11. @ 后面跟的不是普通空格

是 `U+2005`（四分之一空格），字符串比较直接失效：

```python
t = t.replace(chr(0x2005), " ").replace(chr(0xa0), " ")
```

### 12. wxapi 重启后 seq 归零，桥的游标卡死

`after=<旧的大数值>` 永远查不到东西，**且日志无异常**。
每次响应都带 `last`，发现 `last < cursor` 就重置。

### 13. 群消息分不出发言人

`ChatSimpleMessage` 里没有发送者字段。群里只能拿到文本内容，多人群聊要谨慎设计。

### 14. 自己的回复会回流成新消息

必须在发出去时记下文本，收到时比对过滤（`_sent`），否则 AI 会跟自己的话对话。

## OpenClaw

### 15. `tools.allow: []` 会让 OpenClaw 拒绝运行

想给「零工具」不要用空 allow，用 `profile` + `deny` 清单。

### 16. 多 agent 必须显式声明

缺 `agents.ownership: "explicit"` 和 `agents.defaults.systemAgent.agentId` 会校验失败。

### 17. `read` 一定要留在 deny 里

给访客 agent 放开 `write` 是为了回传文件，但**不能顺手放开 `read`** ——
否则访客可以诱导它读服务器上的任意文件。这个组合（能写不能读）是对的。

### 18. 模型说「看不到图」先查 input 声明

```json
"input": ["text"]        // ← 图片根本不会送给模型
"input": ["text","image"]  // ✔
```

不是模型不行。实测 `deepseek-flash` 能正确读出图里的验证码。

### 19. 配置改完用对应用户验证

```bash
openclaw config validate          # ✘ 用 root 跑会报 Config file not found（HOME 指向 /root）
su - <user> -c 'openclaw config validate'   # ✔
```

### 20. token stale 会让整条渠道静默 pause

症状：对方发消息毫无反应。去看 `/tmp/openclaw/openclaw-YYYY-MM-DD.log`。

## 运维

### 21. 卡死的进程叫 `dotnet.exe`，不是 `wxapi.exe`

`taskkill /IM wxapi.exe` 什么都没杀到。只能按端口反查：

```bat
for /f "tokens=5" %%p in ('netstat -ano ^| findstr LISTENING ^| findstr :18800') do taskkill /PID %%p /F
```

### 22. 计划任务 `IgnoreNew` 不先 `/end` 就静默不启动

必须 `schtasks /end` → 等几秒 → `schtasks /run`。

### 23. 重编译前必须先停服务

dll 被占用，`dotnet build` 会失败。顺序：停 → build → 启。

### 24. `sshpass ssh` 会抢走 heredoc 的 stdin

内层 ssh 必须加 `-n`，否则脚本里后续的命令读不到输入，行为诡异。

### 25. 写 `.cmd` / `.ps1` 的编码坑

- `.cmd` 必须**纯 ASCII + CRLF**，中文注释会变乱码
- `.ps1` 要 UTF-8 BOM；`"$var:"` 会被当成作用域变量，必须写 `${var}:`

## Web / 面板类

### 26. 短 UA 一律 404，不代表服务挂了

某些面板（如宝塔）里 `ua_len < 24` 直接判定爬虫返回 404，页面还仿成 nginx 的
`<center>404 Not Found</center>`。用 `curl` 测永远是 404，必须带浏览器 UA。

### 27. 同一个端口号在两台机器上含义可能相反

见过一边是 `nginx / AI 站点`、另一边是 `sshd`。跨机排查先确认端口归属。

## 文档 / 脱敏

### 28. `.docx` 本身是 zip

敏感词扫描只扫外部文本会漏，必须解开内部的 xml 再扫。

### 29. 分享源码前，脱敏清单要包含「非密钥」的东西

昵称、内部 IP、域名、目录里的用户名、甚至探针脚本的默认参数（`args[0] ?? "某个真实昵称"`）
都算个人信息。逐文件 grep 不如写一份规则表批量替换，再逐字节复核。

## 上下文 / 回复质量

### 30. 群聊想分清「谁在说话」，别指望气泡，要看 `GetChatHistory()`

`ChatSimpleMessage`（气泡）里**没有发送者字段**，怎么反射都拿不到。
但 `GetChatHistory(who, date)` 返回的 `Message` 有 **`Who`** 字段，就是发言人昵称。
所以群聊上下文走 history，气泡只用来做「第几条是图片」这类定位。

```csharp
// 入站消息带上发言人，桥接层才能在提示里写「群里 张三 @ 了你」
var m = hist[^1];
_inbound.Enqueue(new Inbound(seq, who, m.Message, m.SendDateTime, m.UniqueString, m.Who));
```

顺带一个反直觉的点：**`Who == 机器人自己的昵称` 就说明这条是自己发的**，
比拿文本比对 echo 更可靠，能挡掉「自己 @ 自己」的死循环。

### 31. `/api/history/deep` 会打开错误的会话，上下文里别用它

现象：`who=「张三、李四」`（群名带 `、`），deep 返回的却是「张三」单聊的内容。
原因：`GetChatHistory(name, date)` 内部是搜索 + 打开，遇到带标点的群名会命中错的那条，
而 deep 之后读的是「当前打开会话」的气泡板 —— 于是读到别人的聊天记录。
**这个 bug 比卡死更严重：它会把别人的私聊内容塞进群的上下文。**

结论：聊天上下文一律用 `GetChatHistory()`（带 `Who`、1-7 秒），
deep 只保留给「人工排查某一屏到底有什么控件」。

### 32. 滚动合并：时间分隔条绝对不能参与判重

现象：`pages=2` 拿到的条数比 `pages=1` 还少，或者整屏内容重复两遍。
原因：`mmui::ChatItemView` 时间分隔条的内容（如 `19:39`）**每一屏都会重新出现**，
拿它去做「上一屏尾部 vs 这一屏头部」的最大重叠比对，会得到完全错位的匹配。

解法：判重用的签名里**整个排除分隔条**，但输出里保留；重叠条数按「真实消息」计，
再换算成带分隔条的下标：

```csharp
int skip = 0;
if (best > 0) {
    int seenReal = 0;
    for (int i = 0; i < cur.Count; i++) {
        if (!IsSeparator(cur[i])) seenReal++;
        if (seenReal >= best) { skip = i + 1; break; }
    }
    if (skip == 0) skip = cur.Count;
}
buf.AddRange(cur.Skip(skip));      // buf 里有分隔条，sig 里没有
```

### 33. 长回复会被微信吞掉

一条消息几千字时，粘贴进去再回车，微信可能只发出一部分甚至不发。
桥接层按句号/问号/感叹号切成 ≤500 字的几段，段间 sleep 1.2 秒，
最多 4 段（剩下的截断），比发一条巨长的稳得多。

同时要把 markdown 符号洗掉：微信不渲染 `**`、`###`、``` 代码块，
不洗的话用户看到的是满屏星号。

注意列表符只删**行首**的（正则 `^\s*[-*+•]\s+`）——整串替换会把 `10 - 3 = 7` 变成 `10 3 = 7`。

## 判定 / 并发

### 34. 探针失败绝不能缓存，否则群会变成有问必答

判定「这是群还是单聊」靠 `/api/group/members` 探针。最初的写法是无条件
`GROUP_CACHE[who] = g`，于是探针超时一次就把这个群**永久记成私聊**。

后果不是掉一条消息，而是：私聊分支不要求 @ → 群里从此**每句话它都接**，直接刷屏。

现在两道保险：

1. 先用最可靠的信号：**发言人 != 会话名 → 一定是群**（单聊里 `Who` 永远等于会话名），
   这一条命中率极高，根本不用打探针
2. 探针只在兜底时用，**只有成功结果才写缓存**，失败就下次再问

```python
if snd and snd != who:
    GROUP_CACHE[who] = True          # 直接定案
    return True
try:
    ...探针...
    GROUP_CACHE[who] = g             # 只有成功才缓存
except Exception:
    return False                     # 失败：本次当私聊，但不留痕
```

### 35. 多个会话共用一个 `out/` 目录，回传文件会被抢

同一个 agent 的所有会话共享 `workspace-chat/out`。用「mtime > 本轮开始时间」收集产物，
两个会话几乎同时结束时会**各自收走同一批文件** —— 同一份文件发两遍，甚至发给错的人。

解法：`os.rename` 原子领取，改名成功的才算自己的：

```python
box = os.path.join(d, ".claimed", token)      # token = 本轮 seq
os.makedirs(box, exist_ok=True)
os.replace(p, os.path.join(box, name))        # 抢到才有；OSError 说明被别人领走
```

归档时把文件挪进 `<out>/sent/`，顺手 `rmdir` 掉空的 `.claimed/<token>`。

### 36. 别在提示里让 agent 用一个它被 deny 的工具

群聊 agent 故意 deny 了 `view_image`（防止别人的图被当指令执行），
但桥的图片分支仍然写「先用 view_image 看一下这个文件」—— 模型只能干瞪眼，
然后编一个「看到了」出来。

按 agent 分流：群聊里直接回「你只收到占位文字，看不到画面内容」，
连截屏都省了（还少占一次 UI 锁）。

### 37. 启动补扫要设时间预算

`GetChatHistory()` 每个会话 7 秒级，7 个会话 = 50 秒。这段时间里 HTTP 端点基本全超时，
桥那边看就是「服务挂了」。加个 25 秒预算，超了就放弃剩余会话（未读的会由轮询接手）：

```csharp
var cw = Stopwatch.StartNew();
foreach (var v in vis0) {
    if (cw.Elapsed.TotalSeconds > 25) { Console.WriteLine("[catchup] 超预算，交给轮询"); break; }
    ...
}
```

## 附件：文件 / 语音

### 38. 微信收到的文件**已经在磁盘上**，根本不用逆向

一开始以为文件消息拿不到内容。其实微信 PC 收到附件就存好了：

```
C:\Users\<user>\Documents\xwechat_files\<wxid>_xxxx\msg\file\<yyyy-MM>\<原文件名>
```

所以「收文件」= 按文件名在最近几个月目录里找 + 读出来。文件消息的文本本身就是索引：

```
文件
项目四 历史·政治跨学科探究_20260928093202.pdf
150.2K
```

第二行就是文件名，拿它去 `/api/file/find` 一搜一个准。
读完别忘了解析：PDF 用 `pdftotext -layout`，docx/pptx/xlsx 其实就是 zip + xml，
`zipfile` + 一段正则把 `w:t` 抽出来就够了，不装任何库。

顺带提醒：`/api/file/get` 一定要把路径限制在 `xwechat_files` 里面，
否则它就是个任意文件读取接口。

### 39. 语音的转录文本，就藏在气泡的 UIA `Name` 里

`mmui::ChatVoiceItemView` 的 `Name` 有两种形态：

```
语音2"秒未播放              ← 没转过
语音2"秒哈喽哈喽，听得到吗？   ← 转过了
```

**「秒」后面那段就是转录结果**。所以能读就直接读，读不到才去点。

而那个「转文字」按钮是 **mmui 自绘的，不是 UIA 元素** —— 枚举气泡的所有子孙元素，
`count = 0`，一个都查不到。只能按坐标点，位置大约在整行矩形的横向 52%：

```csharp
int cx = x + (int)(w * 0.52);   // 0.52 附近是「转文字」，0.25 左右会点到气泡本体开始播放
```

两个细节：

- 鼠标得先**悬停 400ms** 按钮才显形，直接瞬移+点击容易点空
- 点完之后**不要按下标取结果**：气泡列表会位移，`items[idx]` 可能已经变成下一条消息。
  正确做法是记下点击前的名字集合，点完求差集，出现的新名字才是转录文本

### 40. 后台轮询会「偷走」当前打开的会话

所有「先 `GetChatHistory(who)` 再读气泡」的接口，中间有 1~2 秒空档。
而监听线程每 300ms 就在为别的会话调 `GetChatHistory` —— 它会把微信窗口切走，
于是你读到的是**别人的聊天**。

症状：截屏截到别的聊天、语音接口报「no voice bubble」、上下文串会话。
这类 bug 偶尔出现、极难复现，因为取决于时序。

解法：进程内一把 `SemaphoreSlim(1,1)`，把「打开会话 + 读气泡/截图/滚动」整段括起来，
轮询、补扫和所有相关端点都走这把锁。串行换正确性，值。

### 41. 延时到底花在哪

一条文本消息的真实耗时：

| 环节 | 耗时 | 能不能省 |
|---|---|---|
| 微信切会话 + `GetChatHistory` | ~7s | 省不掉，SDK 内部固定等待 |
| 桥轮询拉取 | ≤0.5s | 已从 1s 调到 0.5s |
| agent 推理 | 10~40s | 模型侧 |
| 发消息 | 1~2s | — |

真正能省的是**重复的 UI 操作**，实测砍掉两处：

1. 群聊上下文原来要再读一次历史（7s，卡的时候 34s）→ 让 wxapi 随消息把最近 6 条
   （`recent`）一起带过来，桥直接用
2. 判定群/私聊原来要打一次 `/api/group/members` 探针（首聊 13s）→ 先用
   `sender != 会话名` 和「recent 里是否只有本人说话」判断，探针只做兜底



---

## 第二轮自查（22:50~22:56）

### 42. 自己发出的消息会把自己的私聊「判成群聊」

「发言人 ≠ 会话名 → 一定是群」这条规则很准，**但有一个例外**：机器人自己发出去的回复，
`sender` 就是 bot 的名字，自然 ≠ 会话名。

于是首次回复成功后，这个私聊就被**永久**记成群聊，从此用户不 @ 机器人就再也收不到回复。
日志里长这样，看着毫无异常：

```
probe 主人 -> direct                                          # 用户发第一条，正常
probe 主人 -> group (by sender=OpenClaw)                      # 机器人回完，自己踩自己
SKIP  主人 group-not-mention '又发了一条消息'                    # 从此不 @ 就不理
```

修复要两层：

1. `sender == BOT_NAME` 时**这个信号没有判别力**，直接清空，别拿它判群
2. self-echo 的过滤必须放在**判群之前**。放在判群之后，探针已经跑完、错判已经写进缓存了，
   后面再 `return` 也救不回来
3. 顺带把 bot 从 `recent` 的发言人集合里剔掉，否则「只有本人在说话」这条快速判定永远命中不了

### 43. 去重表放在内存里，一重启就重复回复

wxapi 重启后 `_seen` 集合归零，监听的启动补扫会把「未读」重新解析一遍再入队。
桥这边如果只有内存游标 `cursor`，重启后 `cursor=0` → 拉到整条队列 → **已经回过的消息再回一遍**。

实测就是这么发生的：用户在同一个群里连问两次，第二次被回复了两遍。

修复：把消息唯一串（`UniqueString`）落盘到 `/opt/wxbridge/state/seen.txt`，
`os.replace` 原子替换，FIFO 保留最近 800 条。

还有个顺序陷阱 —— 去重标记必须打在**入队时**，不能等处理完：

```python
# 错：一批 items 里若有同一条的重复，两条都进 handle 之前谁都没被记下
queue.put(it);  handle(it);  mark_seen(uniq)

# 对：入队即占位，后面那条立刻被挡住；处理失败再撤回来，保证还能重试
if seen_before(uniq): return
mark_seen(uniq)
queue.put(it)
# worker 里 catch 到异常时 unmark_seen(uniq)
```

### 44. 同步 UI 调用会把线程池吃光，轻量接口跟着超时

微信的 UI 自动化全是**同步阻塞**调用（一次 `GetChatHistory` 要 7 秒）。
它占着一个线程池线程不放，Kestrel 就没线程接新请求 —— 于是
`/api/inbound` 明明只是读一下内存队列，却会 **30 秒超时**。

症状极具误导性：客户端（桥 / 看门狗）以为服务挂了，于是重启 wxapi，
重启又清空去重表、重放历史消息 —— 一条完整的雪崩链。

修复一行，在 `Program.cs` 最前面：

```csharp
ThreadPool.GetMinThreads(out int w, out int i);
ThreadPool.SetMinThreads(Math.Max(w, 64), Math.Max(i, 64));
```

实测效果（一个重 UI 请求正在跑时打轻量接口）：

| | 改前 | 改后 |
|---|---|---|
| `/api/inbound` | 30s 超时 | 1~9ms |

配套两处防御：

- 桥的拉取超时 `30s → 10s`（7 秒的 UI 操作都做完了，不值得白等半分钟），失败按 1→5 秒退避
- 自愈加冷却：4 分钟内不重复重启 wxapi。它冷启动本来就要 30~60 秒，
  把「正在启动」误判成「挂了」会形成重启抖动

### 45. `last` 骗人：队列空和刚重启长得一模一样

`/api/inbound` 原来返回 `last = _seq`（队列里最后一条的序号）。客户端拿它判断游标要不要退回：

```python
if last < cursor:      # 「服务重启了，seq 归零了」
    cursor = 0
```

问题是**队列空的时候 `_seq` 也是 0**，跟"刚重启"完全无法区分。于是：

1. 桥拉到一条消息，`cursor` 推到 3
2. 队列被消费空，`last` 回到 0 → `0 < 3` 成立 → 游标重置为 0
3. `after=0` 又把队列里剩下的旧消息全拉一遍 → 去重挡下 → 但**去重挡下的消息不会推进游标**
4. 回到第 2 步，每 0.5 秒循环一次

症状就是日志被 `SKIP ... dup(after-restart)` 刷满，3 分钟刷了 352 行。

两个修复：

- 服务端多给一个 `epoch`（进程启动时间戳），客户端**只认它**
- 客户端把游标推进挪到 `dispatch()` 里 —— 包括被去重挡下的消息也要推，
  否则游标就永远停在原地

```csharp
string _epoch = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
return J(new { ok = true, count = items.Count, last = _seq, epoch = _epoch, items });
```

```python
if ep is not None and epoch is not None and ep != epoch:
    cursor = 0            # 只有换了进程才归零
epoch = ep
for it in items:
    dispatch(it)          # dispatch 内部推进 cursor，不管这条要不要处理
```

### 46. 桥把内容送到嘴边了，提示词还在说「我收不到」

文件、语音这两件事，桥这边早就翻译好了：文件抽成正文、语音转成文字，直接拼进 prompt。
但第一次实测 agent 还是回「我这边确实拿不到附件内容」——

问题不在代码，在**提示词**。agent 的 `USER.md` 里一个字都没提这些格式，
模型只能靠猜；它之前被教过「看不到就是看不到，别编」，于是老老实实说自己看不到。
更糟的是 `chat-USER.md` 里还留着一条 `遇到图片占位提示时，如实说自己看不到画面` ——
那是图片还没做截屏方案时写的，现在图片是能看的，这条会让它**明明看得到也说看不到**。

所以这套东西有一条隐含契约：**桥产出的文本格式，必须在提示词里被解释一遍**。

| 桥送过来的格式 | 提示词里要教它做什么 |
|---|---|
| `（对方发来一张图片，已保存到 /path）` | 用 view_image 打开看，别猜 |
| `（对方发来一个文件：x.pdf，约 150KB，已保存到 /path）` + `文件内容如下：…` | 直接读正文，**不要再去 read 那个文件**（大文件撑爆上下文） |
| `（对方发来一条语音，已经转成文字了）语音里说：…` | 当普通文字处理 |
| `（对方发来…，我收不到，请换成文字）` | 这个才是真拿不到，如实说 |

还要显式写一条否定约束：**「不要说你收不到附件」**，除非提示里明写了抽不出文字。
光说「该怎么做」不够，模型的旧习惯会把新能力盖掉。

另一个纯 bug：`group-IDENTITY.md` 里写着「我是「某个人名」」—— 从私聊那份复制过来忘了改。
提示词这种地方只要复制过一次，就一定会带出这种错误。

---

## 第三轮自查：失败路径（23:10~23:15）

前两轮盯的是「消息有没有被正确处理」，这一轮专门盯**失败的那条路**。
正常运行谁都看得见，出问题时没人看得见 —— 这七条全是「明明失败了，日志却是绿的」。

### 47. HTTP 200 不等于发成功，要看 body 里的 `ok`

wxapi 内部抛异常时，异常会被顶层 handler 吃掉并转成
`{"ok": false, "error": "..."}` —— **HTTP 状态码仍然是 200**。

```python
r = api("/api/send", {...})
log("SENT", who)                     # ✗ 只要没抛异常就记成功
```

后果是用户什么都没收到，日志一片正常，等你发现时已经过去几小时。

```python
if r.get("ok"):
    return True
log("send rejected", who, str(r)[:160])
return False                          # ✓ 拿 ok 当唯一判据
```

同理，`/api/inbound`、`/api/file/get` 都要判 `ok`，不能只看能不能连通。
这个项目的**所有**接口都统一返回 `{ok, ...}`，就是为了让调用方只有一条判断路径。

### 48. 超时不要重试

发送超时的时候，请求**可能已经到达服务端并且发出去了**。
重试的结果是给用户发两遍同样的话 —— 比丢一条更难受，因为丢一条用户会再问一遍。

```python
except Exception as e:
    if isinstance(e, socket.timeout) or "timed out" in str(e).lower():
        log("send timeout（可能已发出，不重试）", who)
        return False                  # 不重试，只记一笔
    if i + 1 < tries:                 # 连接失败 / DNS 失败才是真没送到
        time.sleep(2 + i * 2)
```

「重试」只在**确定对方没收到**时才有意义。判断不了的时候，宁可不重试。

### 49. SDK 的 `SendMessage` 找不到会话时静默成功

实测：发给一个不存在的会话名，接口回 `{"ok": true}`，微信里什么都没发生，
**既不抛异常也不返回失败**。所以「ok:true」在发送这一侧最多只能证明「没崩」。

只能在发之前自己核一遍会话是否存在，留个痕迹方便排查：

```csharp
var vis = await c.GetVisibleConversations();
if (vis != null && vis.Count > 0 &&
    !vis.Any(v => (v.ConversationTitle ?? "") == m.who))
    Console.WriteLine("[send] WARN 目标会话不在可见列表: " + m.who);
```

**只用它告警，不用它拦截**。`GetVisibleConversations()` 可能有分页，
真拦下去就是「偶发丢消息」；而漏记一条日志的代价几乎为零。方向要选错得起的那个。

### 50. 发送失败的文件，别扣在 `.claimed` 里

收成品的流程是「扫描 → 改名进 `.claimed` → 发送 → 归档」。
发送失败时如果只是 `return`，文件就留在了 `.claimed` ——
而扫描只看 `out/`，那个目录**再也没人扫**，等于永久丢失，用户还以为发出去了。

```python
if send_files(who, files):
    archive(files, outdir)
else:
    for p in files:
        dst = os.path.join(outdir, os.path.basename(p))
        os.replace(p, dst)
        os.utime(dst, None)     # 必须刷 mtime，否则 since 判断会跳过它
        back += 1
    log("OUTBOX putback", who, back)
```

`.claimed` 这种「中间态目录」只要存在，就必须有一个**把它捞回来**的分支，
否则它就是个单向垃圾桶。

### 51. 临时目录按次隔离：否则 A 的文件会发给 B

原来是所有请求共用 `<Temp>/wxout`，文件名就是原始文件名。
两个会话同时发「报告.pdf」时，后写的直接把先写的盖掉 ——
**结果把 A 的文件发给了 B**。这种 bug 出一次就不敢再用。

```csharp
string NewWxOutDir()
{
    var root = Path.Combine(Path.GetTempPath(), "wxout");
    Directory.CreateDirectory(root);
    var cutoff = DateTime.Now.AddHours(-1);          // 顺手清理，否则只涨不降
    foreach (var d in Directory.GetDirectories(root))
        if (Directory.GetLastWriteTime(d) < cutoff) Directory.Delete(d, true);
    var dir = Path.Combine(root, Guid.NewGuid().ToString("N").Substring(0, 12));
    Directory.CreateDirectory(dir);
    return dir;
}
```

凡是「落盘再发送」的链路，落盘路径都必须带请求唯一性。
只靠文件名区分是不够的 —— 用户起同名文件太正常了。

### 52. 一个人能占满所有 worker

worker 只有 3 个，队列无上限。一个陌生人连发 20 条，3 个 worker 全在等他，
主人发来的消息排在后面一起卡 —— 而这台机器是给主人用的。

```python
MAX_PER_CONV = 3                      # 同一会话最多「排队 + 处理中」3 条
with _inflight_lock:
    n = _inflight.get(who, 0)
    if n >= MAX_PER_CONV:
        log("DROP", who, "同一会话已积压 %d 条，丢弃这条" % n)
        return
    _inflight[who] = n + 1
```

两个细节：
- 计数在**入队时**加、在 worker 的 `finally` 里减，不能只在 `handle()` 里加减
- 被丢弃的消息**游标也要推进**，否则每 0.5 秒重拉同一批，日志刷屏

公平性要靠限流实现，不能靠「反正有队列」——队列没有公平性。

### 53. 读取没有上限，就是给自己埋 OOM

`read()` 一个 200MB 的文本文件，进程直接吃掉 200MB（解码后可能是 400MB）。
陌生人发什么你都读，等于把内存交给了对方。

```python
MAX_PARSE_BYTES = 40 * 1024 * 1024    # 先看大小，超了不解析
MAX_TEXT_READ = 200000                # 再看，就算大小合格也只读这么多字符
MAX_DOC_CHARS = 8000                  # 真正塞进 prompt 的只有这么多
```

三层上限各管一件事：**磁盘（不搬）、内存（不读）、上下文（不塞）**。
少任何一层，另外两层都会被绕过。

顺带一个只增不减的问题：`inbox/` 里的截图和附件从来没人删。
加个 `prune_inbox()`，每存一个新文件顺手清 7 天前的 —— 目录里文件不多，`listdir` 很便宜。

### 54. 文件名只精确到秒，同一秒两张就互相覆盖

`img_%H%M%S.png` 看着够了，实际上一秒内连截两张（用户连发两张图）就会盖掉一张。
加上毫秒尾巴，代价是零：

```python
p = os.path.join(d, "img_%s_%03d.png"
                 % (time.strftime("%H%M%S"), int(time.time() * 1000) % 1000))
```

判断标准很简单：**这个文件名会不会被两个人（或两条消息）在同一时刻算出来？**
会，就得加唯一性。时间戳精确到秒从来都不够。

### 55. `ask()` 失败时，用户那边是彻底的安静

agent 进程崩了、超时了、返回空 —— 桥这边 `reply` 是空字符串，
然后 `if reply:` 判断为假，什么都不发。用户看到的是「对方已读不回」。

```python
reply = ask(agent, session, prompt)
if not reply:                          # 冷启动偶尔会空手而归，先给一次机会
    time.sleep(2)
    reply = ask(agent, session, prompt)
if reply:
    send_long(who, clean_reply(reply)[:MAX_REPLY])
else:
    send_text(who, "我这边刚才卡了一下，没生成出回复。你再说一遍？")
```

**失败也必须有一条出路**。机器人沉默和机器人卡住，在用户眼里是同一件事，
但后者至少能让他知道该重发。

### 56. 杂事别塞在主循环里：一个卡住的「自动通过好友」能闷住所有消息

收消息的主循环里除了拉取，还顺手做了「每 30 秒自动通过好友申请」。
这个调用走的是 UI 自动化，超时给到 200 秒 —— 一旦它卡住：

- 收消息整体停摆最多 **200 秒**（消息不会丢，但谁问都不理）
- 而且它**不抛异常**，`fails` 不涨，看门狗判定「一切正常」，不会来救

用户看到的现象是「三分多钟突然不理人」，日志里干干净净。

```python
threading.Thread(target=accepter, daemon=True).start()   # 杂事单独一条线
```

判断标准：**主循环里只能放「不做就收不到消息」的事**。
自动通过好友、上报心跳、清理缓存这类，全部挪到独立线程 ——
它们的失败不应该让主链路陪葬。

---

## 第四轮：延迟（23:25~23:40）

用户原话：「怎么现在的延时比以前还高？」「工具调用时间长要提示一下，不要让人觉得没反应」。

### 57. 别凭感觉优化延迟，先把时间分段量出来

第一反应是「哪一步变慢了」，但日志里只有 `RECV`→`SENT` 一个总时长，看不出成分。
真拆开测才发现大头根本不在模型：

```
总 3040ms  模型 1607ms
总 3608ms  模型 2118ms
总 2999ms  模型 1553ms
```

**每条消息有 1.4 秒花在启动一个 node 进程上**，跟消息内容一点关系都没有。
而 `openclaw agent` 本来就是「via the Gateway」（网关常驻），这 1.4 秒纯粹是
CLI 自己加载模块的开销。

想省这 1.4 秒，只有两条路：自己写一个常驻 gateway 客户端（省 1.4s，成本是一整层协议），
或者接受它。权衡之后接受 —— 但**必须知道这 1.4 秒存在**，否则会把优化力气全花错地方。

顺带量出来的另一件事：这个链路里 `GetChatHistory` 才是最大的变数（0.3~7 秒，看会话大小），
它比模型慢得多，而且完全不受桥控制。

### 58. `--thinking` 档位不是随便填的，填错等于消息丢失

`--thinking off` 实测能把模型耗时从 2.5s 压到 1.9s，尾部（3.4s→1.9s）收得更明显 ——
用户感受到的是尾部，不是中位数。

但档位是**按模型能力**的。这台机器接的是自定义 API 的 deepseek，只认 `off`：

```
Thinking level "low" is not supported for custom-ai-api-.../deepseek-...
```

填了不支持的档位，CLI 直接返回 `ok:false`，`ask()` 拿到空 → 用户收到「我这边卡了一下」。
**看起来像模型抽风，其实是参数填错**。改档位之前必须先手测一次。

### 59. 超过 8 秒没动静，用户就认为你死了

延迟里有很大一块是**压不动的**：带工具的回合要读文件、转格式、跑命令，
实测一次「把那份文件转成Word」用了 **89 秒**。这种时候优化毫无意义 ——
聊天窗口里一个字都没有，用户已经在怀疑消息没发出去。

解法不是更快，是**别让他干等**：

```python
ack = {"done": False}
def _ack():
    with ack_lock:
        if ack["done"]: return
        ack["done"] = True
    send_text(who, "收到，我这边在处理，稍等一下。")

timer = threading.Timer(8.0, _ack)
timer.start()
try:
    reply = ask(...)
finally:
    with ack_lock: ack["done"] = True
    timer.cancel()
```

三个细节：
- **8 秒**是个经验值：中位数 5 秒，正常回合不该看到这句；真慢的才该看到
- **群里不发** —— 那里面有别人，插一句「稍等」很吵
- 撤回要和 `done` 标志配合。只在 `cancel()` 里撤不够：timer 恰好触发时，
  可能在 `cancel` 之后才执行 `_ack`，就变成「回复完了又冒出一句稍等」

### 60. 超时之后重试，是在让用户再等一遍

上一轮加了「失败重试一次」，当时想的是「网关抖一下，重试就好了」。
但没区分失败类型：如果是**跑满 180 秒超时**才失败，重试就是再等 180 秒 ——
用户等了三分钟，等来的是又三分钟。

```python
took = time.time() - t0
if took < 25:          # 快速失败：值得重试
    time.sleep(2)
    reply = ask(...)
else:                  # 已经跑满超时：重试只是加倍痛苦
    log("no reply after %.0fs, too slow to retry" % took, who)
```

原则：**重试的前提是「代价比不重试小」**。对已经等了很久的请求，尽快给个
「没成」比再赌一次更有用 —— 至少要让他能自己决定要不要重发。

---

## 第二轮：给访客「真人感」

### 61. `max_tokens` 给小了，reasoning 模型会「看起来不支持」

分诊器一开始 `max_tokens=40`，结果是：**寒暄全部成功，正经问题全部失败**。

原因不是分类逻辑写错，而是这个模型是 reasoning 模型：40 个 token 全花在
`reasoning_content` 上就被截断了，`content` 是空字符串、`finish_reason=length`，
下游 `json.loads("")` 直接抛。表面症状很像「模型对这个问题没话说」。

```python
# 错：寒暄能过，其他全空
body = json.dumps({..., "max_tokens": 40, ...})
# 对：延迟只差 0.02 秒（实测 1.08 vs 1.10）
body = json.dumps({..., "max_tokens": 200, ...})
```

排查动作固定成一条：**只要模型返回空内容，先打 `finish_reason`**。
`length` = 预算不够，`stop` + 空 = 真的没话说，两者处理方式完全不同。

### 62. `txt[find("{"):rfind("}")+1]` 不是「提取 JSON」

同一个分诊器还有第二层坑：模型偶尔把同一行 JSON 重复输出两三遍

```
{"route":"chat","reply":"..."}{"route":"chat","reply":"..."}{"route":"chat","reply":"..."}
```

首尾一框就成了 `{...}{...}{...}`，非法 JSON。而寒暄那种短回复只输出一遍，
于是又出现「寒暄成功、其他失败」这个极具误导性的现象。

正确做法是**逐个候选对象尝试解析，取第一个合法的**：

```python
for m in re.finditer(r"\{[^{}]*\}", txt):
    try:
        obj = json.loads(m.group(0))
    except ValueError:
        continue
    if isinstance(obj, dict) and obj.get("route"):
        return obj
```

经验：**模型输出永远是「不可信输入」**，不要用字符串切片去假装它是结构化数据。

### 63. 非特权 user namespace 被禁，`bwrap` 沙箱直接不可用

想让访客 agent 有文件能力但关在沙箱里，`bwrap` 以 root 跑没问题，
以普通用户跑报：

```
bwrap: setting up uid map: Permission denied
```

Ubuntu 24.04 默认限制非特权 user namespace（`kernel.unprivileged_userns_clone=0`
一类的加固）。要么改内核参数（削弱整机安全，不划算），要么换方案。

**最终选的是「不给文件权限 + 桥代写」**：访客 agent 零文件系统权限，
要交付文件就走 `[[FILE: 名字]]…[[/FILE]]` 文本协议，由桥侧受控落盘。
路径、扩展名、大小全部卡在桥上，比沙箱更容易审计，也不用跟内核较劲。

### 64. 只 deny 了 `read`，`write` / `edit` 是开着的

审计时发现的真实漏洞：访客 agent 的 `tools.deny` 里有 `read`，看起来「读不了文件」，
但 `write`、`edit`、`apply_patch` 全没 deny。

后果不是「能看文件」，是**能改配置给自己提权**：

- 改 `openclaw.json` 把自己的 `exec` 打开
- 覆盖 `~/.ssh/authorized_keys` 直接拿到 SSH

原则：**文件类工具要按「写比读危险」的方向全量 deny**，
不要只填了最直觉的那一个。`ls` / `glob` / `grep` 也算读，一样要关。

### 65. 插话必须用独立 session，否则把主任务的历史搞乱

主任务在跑时用户又发一条，如果复用同一个 `session-key` 去回答，
两个 agent turn 会同时写同一份会话历史 —— 结果是主任务读到半截插话的记录，
上下文互相污染，表现为「答非所问」。

```python
sess = "agent:%s:wx-%s-side" % (CHAT_AGENT, who)   # 后缀 -side，独立会话
```

插话会话是一次性的，不需要长期记忆；它只需要知道「对方在等什么」——
这个信息直接用参数传进去，比让它去读历史更可靠。

### 66. 分诊模型只在「省下来的时间 > 花掉的时间」时才值得加

分诊器一次要 1.0~1.9 秒，而一次普通回合是 4~6 秒。所以：

- **短消息且是寒暄** → 省下 3~5 秒，用户能感觉到
- **正经问题** → 净赚 0，只是多了一次网络往返
- **分诊自己失败** → 必须降级成「走原流程」，绝不能变成单点故障

因此接入条件写得很窄：`len(raw) <= 16`、非文件消息、**且只对访客生效**，
失败返回 `None` 就当没这回事。**优化项永远不能有「失败就答不了」的路径。**

主人为什么排除在外？两个理由：

- 分诊的 `skip` 回复是**无状态**的，不进会话历史。主人回头问「刚才说什么」，
  上下文里没有那两句，会对不上；访客本来就没有跨轮上下文，无所谓
- 主人那句「重启下 nginx」只有 7 个字，但它是真指令，不该因为这个长度判断
  被降级成「不思考」

### 67. 给访客发文件，等于把「文件名」也交给了模型

模型给的文件名是**不可信输入**。`../../etc/passwd`、`c:xxx`、`a.exe` 都能出现。

```python
n = (raw or "").strip().replace("\\", "/").rsplit("/", 1)[-1]   # 自己切，别信 basename
n = re.sub(r'[<>:"|?*\x00-\x1f]', "_", n).strip(" .")
# 扩展名白名单之外一律改成 .md
```

`os.path.basename` 在这里是坑：Windows 上把 `c:xxx` 当盘符拆掉，Linux 上不拆，
同一份代码两种行为，测试和线上对不上。**跨平台确定性比「用标准库」更重要。**

### 68. `.docx` 依赖装在 venv 里，service 却用系统 Python

`pip install python-docx` 被 PEP 668 拒绝（`externally-managed-environment`），
于是装进 venv。但 systemd 的 `ExecStart` 还是 `/usr/bin/python3` ——
`import docx` 失败，代码里 `except ImportError: return False`，于是
**`.docx` 静默降级成 `.md`，不报错、不告警，用户只是拿不到 word。**

```ini
ExecStart=/opt/wxbridge/.venv/bin/python /opt/wxbridge/wxbridge.py
```

凡是「降级路径」都要在部署后**主动验一次**（生成一个真实 docx 看字节数），
否则你永远不知道走的是主路还是降级路。

### 69. 让 agent「像真人」，靠的是禁止清单，不是形容词

在 IDENTITY 里写「你是一个自然、亲切的助手」没有任何用 ——
模型照样会「很高兴为您服务」。真正有效的是把动作一条条禁掉：

- 不说「我是AI」「作为助手」「我可以帮你」
- 不报自己的名字
- 不用「很高兴为您服务」「还有什么可以帮您」
- 不每句都加表情

**并且要顺手禁掉它的替代行为**：被问「你是不是机器人」时，
它会正经回答「我是个程序」—— 这比自我介绍还生硬。
所以要多写一句「随口带过，比如『算吧，不过有事说事』」。

### 70. 不确定的细节，模型一定会编

实测：插话时用户问「明天天气怎么样」，模型的回复是
「合同那份马上弄完，完了接着看天气。你是要问**北京**明天……对吧？」
—— 「北京」是凭空来的，对话里从没出现过。

这类幻觉出现在**过渡性回复**里特别多，因为它的目标只是「说点什么」，
不是「说对什么」。所以插话提示词里必须明写：

> 不知道的细节（城市、日期、数字、人名）绝对不要猜，要么反问一句，要么说稍后确认。

### 71. 微信 4.x PC 端没有「换头像」入口，别再花时间找

想给 bot 那个微信号换个头像，把电脑版能点的都点了一遍，结论是**这个版本做不到**：

| 尝试的路径 | 结果 |
|---|---|
| 左上角头像 → 个人信息卡片 → 再点头像 | 卡片直接关掉，**不出文件选择框** |
| 卡片里的头像 hover | 没有相机图标、没有边框变化 |
| 卡片里的头像右键 | 无菜单（截图字节级相同） |
| 卡片里的昵称 / 微信号 | 都不是可点区域 |
| 双击头像、卡片上按回车 | 无反应 |
| 设置 → 账号与安全 → 头像 | hover 无「编辑」，点了也不动 |
| 设置左侧导航 | 只有 账号与安全/通用/快捷键/通知/插件/关于微信，**没有「个人信息」** |
| 微信ClawBot 会话 | 是个聊天窗口，不是管理面板 |

网上 2026 年的教程说「点头像 → 小窗口 → 再点头像就能选图」，那是 3.x 或更新构建的
情形，别拿它当准。**这类「这个版本到底有没有这个入口」的问题，最快的判定方式是
截图 + 逐层点击，不是查教程。**

顺带记下远程驱动 UI 的可用姿势（`tools/vm-ui-driver.ps1`）：

- ssh 进 Windows VM 的默认 shell 是 `cmd`。**别拼 `cmd /c`，也别在命令行里塞
  一长串 powershell 参数** —— 引号经过三层 ssh 必然被吃掉。脚本落地成 `.ps1`，
  用 `schtasks /create /tn <名字> /tr "... -File C:\...\ui.ps1" /sc once /st 23:59 /it`
  建一个**交互式**任务（`/it` 必须有，否则拿不到桌面和微信窗口），
  之后每次只改一个 `cmd.txt` 再 `schtasks /run`
- **Qt 弹窗在焦点变化时会自己关。**「打开菜单」和「点菜单项」必须在
  **同一次脚本执行内**完成；分两次 run 一定失败 —— 中间 schtasks 起进程会把焦点抢走
- 坐标不能靠肉眼估：`shot` 存 1280×800 原图，用 PIL 按已知 crop 原点换算，
  每步点击后再截一张对比，否则你根本不知道点中了什么
- 挪窗口前先确认它在屏幕内：这台机器上微信主窗口在 `y=-48`，
  顶部那条（正好是账号头像所在）整条在屏幕外，怎么点都点不到

---

## 第五轮：微信自动升级后退登（2026-10-25）

一次「发微信没反应」的完整复盘。整条链路每一层都没报错，但整条链路是死的 ——
因为微信半夜自动升级，升级完把账号登出了。

### 72. 先看窗口类名，别猜

UIA 顶层元素的 `ClassName` 是唯一可靠的判据：

| ClassName | 含义 |
| --- | --- |
| `mmui::MainWindow` | 正常，已登录 |
| `mmui::LoginWindow` | **已退登，只剩登录页** |

坑在于 Win32 侧两者都是 `Qt51514QWindowIcon`、标题都是 `微信`，
所以 `visible=True / iconic=False` 看着一切正常，SDK 却一直报
`WechatNotOpenedException: 检查到系统中未打开微信窗口 或者 微信窗口处于隐藏状态`。

**那句报错的字面意思（窗口隐藏/最小化）会把人带偏**，真实原因是没有主窗口。
排查第一步永远是：`RootElement.FindAll(Children)` 把那几个顶层元素的
`ClassName` 打出来看一眼。窗户到底存不存在，比任何日志都直接。

顺带一个取证姿势（无 GUI 也行）：

```bash
virsh screenshot <vm-name> /tmp/shot.png   # 宿主机上直接抓 VM 真实画面
```

### 73. `IsForceOpenUITree` 必须开，默认是 false

微信 4.1.9.xx 以后是否公开 UI Tree 由腾讯内部算法决定。SDK 有开关：

```csharp
WeAutomation.Initialize(o => {
    o.IsForceOpenUITree = true;   // 社区版 2.1.8 起提供
    o.EnableOCR = true;
});
```

不开的话初始化能过、`GetWeChatClientNames()` 能返回客户端名，
但一旦真去读控件就抛
`WechatNotSupportUITreeException: 腾讯没有公开 processID=xxx 的微信的 UI Tree`。

开了之后日志里会出现：

```
UI Tree Activator started (check_only=False, pid=...)
Target detail: pid=... source=scanner rva=0x........
ACTIVATION_WRITTEN
[UI Tree 观察] 共采样 21 次，最终值=1
```

原理是按 RVA 找到 `Weixin.dll` 里的门控字节写 1。**微信一升级 RVA 就变**，
那个「scanner」是猜着找的，升级后第一次启动偶尔会失败，重启一次通常就好。

### 74. Qt 自绘按钮，`InvokePattern.Invoke()` 返回成功但什么都没发生

登录页的「登录」是 `mmui::XOutlineButton`。走 UIA：

```csharp
btn.GetCurrentPattern(InvokePattern.Pattern).Invoke();   // 不抛异常
```

日志一切正常，界面纹丝不动。Qt 的自绘控件没有实现 Invoke provider，
`Invoke()` 等于空实现。

**正解**：拿 `BoundingRectangle` 算中心点，真点一次。

```csharp
SetCursorPos(cx, cy);
mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);   // down
mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);   // up
```

`BoundingRectangle` 是物理像素，`SetCursorPos` 也是物理像素，不用管 DPI 缩放。
但**先确认坐标在屏幕内**：这台机器上微信主窗口在 `y=-48`，
窗口标题栏那条整条在屏幕外，按坐标点会点到别处。

### 75. `SendMessage` 找不到会话时**静默成功**

```csharp
await c.SendMessage("__不存在的会话__", "hi");   // 返回 ok，微信里什么都没发
```

所以「接口回 200 就当发成功了」是错的。至少要拿
`GetVisibleConversations()` 对一遍目标在不在列表里，不在就记一条警告 ——
注意这个列表可能分页，只能用来记日志，不能用来拦截，误拦的代价更大。

`文件传输助手` 不在可见会话列表里（它是独立入口），
所以拿它做发送自测会得到**假通过**。要自测就发给一个真实存在的会话。

### 76. 启动时枚举抖动 → 整轮监听没挂上

wxapi 启动后要 `SubmitMonitor("*")` 才会开始轮询。日志里出现过：

```
fail: 获取微信窗口失败: An item with the same key has already been added. Key: ...
[warm] init error: ...
```

一次失败就等于 `listening: []` —— 端口通、`/api/status` 回 `online:true`、
`/api/send` 也能发出去，**但没有任何人在听新消息**。
表现就是「机器人突然不理人，但你让它发消息它照发」。

判据是 `/api/monitor/status` 里的 `listening` 为空。
启动代码改成最多试 6 次、每次 6 秒，直到每个会话都注册上为止。

### 77. 脚本文件不写 BOM，中文注释能把整个脚本干死

这条在 `.bat` 和 `.ps1` 上各撞了一次，机理一样：

- `.bat` → cmd 按 GBK 解析，含中文的行会被切碎当命令执行（`'批深层目录' is not recognized`）
- `.ps1` → Windows PowerShell 5.1 对无 BOM 的 UTF-8 **按 ANSI/GBK 解码**，
  多字节被切碎后偶尔拼出一个引号，整个文件语法错误
- **进程退出码 1，脚本自己该写的第一行日志都没有**（`Set-Content` 在解析失败的文件里根本没执行）

判据：`上次结果: 1` 且脚本本该产生的日志文件不存在 = 解析期就死了，不是逻辑错。
解法：`.ps1` 一律 UTF-8 **with BOM**；`.bat` 一律 GBK + `chcp 936` + 全 CRLF；
或者干脆脚本里只写 ASCII，中文留给输出的文本。

### 78. `schtasks` / `tasklist` 的参数经过两层 ssh 必被拆坏

```
tasklist /fi "PID eq 6380"     # 到对面变成 /fi PID eq 6380 → 无效参数
```

本机 → 跳板 → VM 这种两层 ssh，内层引号先被跳板吃掉，再被 cmd 重解析。
`/fi`、`/tr`、带空格的路径全中招。

可用姿势：命令里**不出现引号和带空格的参数**（`schtasks /run /tn 名字` 是安全的），
复杂参数一律落成 `.xml` 再 `schtasks /create /xml`。
`.xml` 必须是 **UTF-16 带 BOM**（`ff fe`），否则报错。

另外 `schtasks /create` 带 `/rp` 会把 `LogonType` 变成
`InteractiveTokenOrPassword`，没有缓存凭据时会降级到 session 0，**GUI 不可见**。
要 `InteractiveToken` 就**不要给 `/rp`**，用 `/ru 用户 /it`。

### 79. 「静默死」比「报错死」难查得多

这次的真实代价不是修不好，而是**没人知道它坏了**：

- wxbridge 看到 `WechatNotOpenedException` 只会当成网络抖动，
  每失败 5 次就重启一次 wxapi，日志里刷成一片 `wxapi 重启，游标归零`
- 重启当然修不好 —— 微信根本没登录
- 结果从凌晨 4:44 一直烂到上午 11:20 才被人发现

所以判据要放在**最外层**：`/api/status` 返回 `online:true` 只能说明进程活着，
要有一个定时任务每 2 分钟看一次窗口类名：

- 出现 `mmui::LoginWindow` → 点一次「登录」（账号是记住的，一次点击就回来）
- 主窗口 `IsIconic` → `ShowWindow(SW_RESTORE)`，SDK 看不见最小化窗口
- 都没有 → 微信进程真没了，记一条 WARN，别乱拉进程

这个自愈任务比任何日志告警都值 —— **退出登录是这台机器唯一天天可能发生、
而人又不会立刻发现的故障**。
