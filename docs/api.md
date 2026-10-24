# wxapi HTTP 接口

所有 `/api/*` 都需要请求头 `X-Token`（值等于进程环境变量 `WXAPI_TOKEN`），否则返回 `401 {"ok":false,"error":"bad token"}`。

统一返回 `{"ok":true, ...}`，失败为 `{"ok":false,"error":"ExceptionType: message"}`。

## 基础

| 方法 | 路径 | 参数 | 说明 |
|---|---|---|---|
| GET | `/` | — | 端点清单（自文档） |
| GET | `/api/status` | — | 登录状态：`online` / `nick` / `wxid` |
| GET | `/api/friends` | — | 好友列表 |
| GET | `/api/conversations` | — | 会话列表（含未读数） |
| GET | `/api/visible` | — | **当前可见会话**，`NotReadNumbr` 是未读数，约 50ms |
| GET | `/api/history` | `who` | 当前窗口可见的历史消息（6~8 条） |

## 发送

| 方法 | 路径 | 请求体 | 说明 |
|---|---|---|---|
| POST | `/api/send` | `{who, text}` | 发文本 |
| POST | `/api/sendfile` | `{who, paths[]}` | 发**VM 本地**文件 |
| POST | `/api/upload` | multipart | 上传到 VM 临时目录，返回路径 |
| POST | `/api/upload/send` | `{who, files:[{name,data}]}` | base64 上传并直接发出去（桥用这个） |

`/api/upload/send` 是给远端调用的：桥在 Linux 上，文件在 Linux 上，只能 base64 过去再落盘发送。

## 监听

| 方法 | 路径 | 参数 | 说明 |
|---|---|---|---|
| POST | `/api/monitor/start` | `{who:"*"}` | 启动监听，`*` 表示全部会话 |
| POST | `/api/monitor/stop` | — | 停止 |
| GET | `/api/monitor/status` | — | `{listening, seq, queued, seen}` |
| GET | `/api/inbound` | `after=seq` | 拉取 `seq > after` 的消息：`{seq, who, text, time, uniq, sender, recent}` |
| ↳ 响应 | | | `{ok, count, last, epoch, items}` |

`sender` 是发言人昵称（来自 `Message.Who`），群聊里靠它区分「谁 @ 了你」；
一对一会话里它等于 `who`。

`epoch` 是**本进程的启动时间戳**，客户端必须靠它判断「服务重启了、seq 归零了」。
不要用 `last < cursor` 判断 —— `last` 是队列里最后一条的序号，队列空时它也是 0，
跟"刚重启"长得一模一样，客户端会被骗着把游标反复重置回 0，然后把整条队列重拉一遍。
`recent` 是随消息附带的最近 6 条，省掉客户端再读一次历史（那次要 7 秒）。

监听实现要点：

- 每秒 `GetVisibleConversations()`，只处理 `NotReadNumbr > 0` 的会话
- 命中后 `GetChatHistory(who, DateTime.Today)`，取**最后 6 条**（@ 和正文常分两条发）
- 用 `UniqueString` 去重，去重表落盘 `seen.json`，重启不重复入队
- 启动时做一次 **catch-up 补扫**：把所有会话各读 6 条，未见过且非基线的入队 —— 补回停机期间漏的消息
- 自己的回复通过 `_sent` 集合过滤，避免自问自答；群聊再加一道 `sender == 自己的昵称` 判断

## 气泡与图片

| 方法 | 路径 | 参数 | 说明 |
|---|---|---|---|
| GET | `/api/bubbles` | `who` | 枚举消息气泡：`{i, type, cls, name, x, y, w, h}` |
| GET | `/api/shot` | `who, index, pad` | **按气泡坐标截屏**，返回 base64 PNG |
| GET | `/api/image` | `who, index` | 试 `FetchImageAsync` 取原图（多数环境不可用） |
| GET | `/api/voice/text` | `who, index?, wait?` | **语音转文字**：气泡名里已有转录就直接读，否则点「转文字」再读 |
| GET | `/api/desc` | `who, index` | 反射枚举某个气泡的所有子孙元素（排查自绘控件用） |
| GET | `/api/rightclick` | `who, index, pad` | 右键气泡并截取该区域，返回 base64 PNG（排查菜单用） |
| GET | `/api/key` | `code` | 发一个按键（如 27 = ESC），用来关掉菜单 |

## 文件

| 方法 | 路径 | 参数 | 说明 |
|---|---|---|---|
| GET | `/api/file/find` | `name, limit?` | 在 `xwechat_files\*\msg\file\*\` 里按文件名找，按 mtime 倒序 |
| GET | `/api/file/get` | `path` | 读文件返回 base64；**只允许微信目录下的路径**，单文件上限 30MB |

微信收到的附件本身就已经落盘（见 `docs/pitfalls.md` 38），所以「收文件」只是搜索 + 读取。

`/api/voice/text` 返回 `{ok, clicked, before, transcript, note}`。
`mmui::ChatVoiceItemView` 的 `Name` 形如 `语音2"秒未播放` 或 `语音2"秒哈喽哈喽，听得到吗？`，
「秒」后面就是转录文本 —— 有就直接返回，没有才按坐标点那个自绘的「转文字」按钮。
| GET | `/api/history/deep` | `who, pages, limit` | 滚动翻页读历史，自动重叠合并 |

`/api/shot` 的 `index` 支持负数（`-1` 最后一条，`-2` 倒数第二条），`pad` 是向外扩的像素（默认 4，上限 40）。

`/api/history/deep` 返回 `{ok, rounds, took, count, items:[{type,text}]}`。`pages` 1~12，`limit` 10~200，内部有 55 秒硬上限。

> ⚠️ 这个接口**不要用于聊天上下文**：对带 `、` 的群名，`GetChatHistory(name)` 会打开错误的会话，
> 于是它读到的是别人的聊天记录。它读的是「当前打开会话」的气泡板，没有发言人字段。
> 要发言人就用 `/api/history` 的 `Who`。

## 群

| 方法 | 路径 | 参数 | 说明 |
|---|---|---|---|
| GET | `/api/group/members` | `name` | 群成员列表；返回空数组说明是单聊 |
| POST | `/api/group/create` | `{name, firstWho, members[]}` | 建群 |
| POST | `/api/group/invite` | `{name, members[], reason}` | 拉人 |
| POST | `/api/group/quit` | `{name, clearHistory}` | 退群 |

## 好友

| 方法 | 路径 | 参数 | 说明 |
|---|---|---|---|
| POST | `/api/friend/accept` | — | 通过所有待处理好友申请，返回 `passedCount` |
| POST | `/api/friend/listen` | — | 好友申请监听状态 |

## 会话操作

| 方法 | 路径 | 请求体 | 说明 |
|---|---|---|---|
| POST | `/api/top` | `{who, top}` | 置顶 / 取消置顶 |
| POST | `/api/dnd` | `{who, dnd}` | 免打扰开关 |
| GET | `/api/search` | `q` | 搜索 |

## 已知的调用陷阱

- **`/api/history`、`/api/history/deep`、`/api/shot`、`/api/bubbles` 都会打开会话** —— 副作用是**把未读清零**。监听正在等这个未读，被你先读掉就永远收不到通知了。调试时先看 `/api/visible`，别直接读历史。
- **这些端点都做 UI 自动化，可能卡住**。卡住后整个进程的后续请求全部超时（内部状态被占住）。所以调用方必须短超时 + 冷却 + 看门狗重启，不能假设它一定返回。
- `/api/status` 走的是纯 SDK 信息读取，不碰窗口，卡死时它通常还能响应 —— 可以拿它做存活探针。
