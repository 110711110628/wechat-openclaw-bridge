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


