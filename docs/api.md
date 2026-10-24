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
| GET | `/api/inbound` | `after=seq` | 拉取 `seq > after` 的消息 |

监听实现要点：

- 每秒 `GetVisibleConversations()`，只处理 `NotReadNumbr > 0` 的会话
- 命中后 `GetChatHistory(who, DateTime.Today)`，取**最后 6 条**（@ 和正文常分两条发）
- 用 `UniqueString` 去重，去重表落盘 `seen.json`，重启不重复入队
- 启动时做一次 **catch-up 补扫**：把所有会话各读 6 条，未见过且非基线的入队 —— 补回停机期间漏的消息
- 自己的回复通过 `_sent` 集合过滤，避免自问自答

## 气泡与图片

| 方法 | 路径 | 参数 | 说明 |
|---|---|---|---|
| GET | `/api/bubbles` | `who` | 枚举消息气泡：`{i, type, cls, name}` |
| GET | `/api/shot` | `who, index, pad` | **按气泡坐标截屏**，返回 base64 PNG |
| GET | `/api/image` | `who, index` | 试 `FetchImageAsync` 取原图（多数环境不可用） |
| GET | `/api/history/deep` | `who, pages, limit` | 滚动翻页读历史，自动重叠合并 |

`/api/shot` 的 `index` 支持负数（`-1` 最后一条，`-2` 倒数第二条），`pad` 是向外扩的像素（默认 4，上限 40）。

`/api/history/deep` 返回 `{ok, rounds, took, count, items:[{type,text}]}`。`pages` 1~12，`limit` 10~200，内部有 55 秒硬上限。

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
