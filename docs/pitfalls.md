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
