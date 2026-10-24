#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""wxbridge — VM 微信 <-> OpenClaw 双向桥

链路：VM(192.168.122.100:18800 wxapi) -> 本机 OpenClaw agent -> wxapi /api/send
- 主人发的消息 = 指令，走 owner（全权 agent）
- 其他人发的消息 = 纯聊天，走 chat（零工具 agent）
"""
import base64
import queue
import re
import zipfile
import json
import os
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request

VM = os.environ.get("WXAPI_URL", "http://192.168.122.100:18800")
TOKEN = os.environ.get("WXAPI_TOKEN", "CHANGE_ME_WXAPI_TOKEN")
OWNER = os.environ.get("WX_OWNER", "主人")
PRIV_AGENT = os.environ.get("WX_PRIV_AGENT", "owner")
CHAT_AGENT = os.environ.get("WX_CHAT_AGENT", "chat")
ACCEPT_INTERVAL = int(os.environ.get("WX_ACCEPT_INTERVAL", "30"))
POLL_INTERVAL = 0.5
MAX_REPLY = 1200
OC = "/home/wechat/.npm-global/bin/openclaw"

# 各 agent 的「成品出口」目录：agent 把要发给用户的文件写到这里，本轮结束自动回传微信
OUT_DIRS = {
    PRIV_AGENT: "/home/wechat/.openclaw/workspace/out",
    CHAT_AGENT: "/home/wechat/.openclaw/workspace-chat/out",
    os.environ.get("WX_GROUP_AGENT", "group"): "/home/wechat/.openclaw/workspace-group/out",
}
MAX_FILE = 20 * 1024 * 1024

# 各 agent 的「收件」目录：图片截屏存这里，agent 用 view_image 读
INBOX_DIRS = {
    PRIV_AGENT: "/home/wechat/.openclaw/workspace/inbox",
    CHAT_AGENT: "/home/wechat/.openclaw/workspace-chat/inbox",
    os.environ.get("WX_GROUP_AGENT", "group"): "/home/wechat/.openclaw/workspace-group/inbox",
}

cursor = 0
last_accept = 0.0
deep_block_until = 0.0          # deep 失败后的冷却截止时间
FAIL_LIMIT = int(os.environ.get("WX_FAIL_LIMIT", "6"))   # 冷启动要 30~60s，4 次太容易误判
RECOVER_SH = "/opt/wxbridge/recover-wxapi.sh"

# ── 跨重启去重表 ───────────────────────────────────────────────
# wxapi 重启后 _seen 归零，会把「未读」重新解析一遍再入队；桥这边如果只靠
# 内存游标，就会把已经回过的消息再回一遍（实测 22:29:50 就重回过一次）。
# 把消息唯一串落盘，重启后仍然认得。
STATE_DIR = os.environ.get("WX_STATE", "/opt/wxbridge/state")
SEEN_FILE = os.path.join(STATE_DIR, "seen.txt")
SEEN_MAX = 800
_seen_lock = threading.Lock()


def _load_seen():
    try:
        with open(SEEN_FILE, encoding="utf-8") as f:
            return [x.strip() for x in f if x.strip()]
    except OSError:
        return []


SEEN = _load_seen()
SEEN_SET = set(SEEN)


def seen_before(u):
    return bool(u) and u in SEEN_SET


def _save_seen_locked():
    try:
        os.makedirs(STATE_DIR, exist_ok=True)
        tmp = SEEN_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            f.write("\n".join(SEEN))
        os.replace(tmp, SEEN_FILE)      # 原子替换，写一半崩了也不会坏文件
    except OSError as e:
        log("seen save error", repr(e)[:100])


def mark_seen(u):
    if not u:
        return
    with _seen_lock:
        if u in SEEN_SET:
            return
        SEEN_SET.add(u)
        SEEN.append(u)
        while len(SEEN) > SEEN_MAX:
            old = SEEN.pop(0)
            SEEN_SET.discard(old)
        _save_seen_locked()


def unmark_seen(u):
    """处理失败要能重试 —— 占位时写的记录得撤回来。"""
    if not u:
        return
    with _seen_lock:
        if u not in SEEN_SET:
            return
        SEEN_SET.discard(u)
        try:
            SEEN.remove(u)
        except ValueError:
            pass
        _save_seen_locked()


# 并发：不同会话并行处理，同一会话保序（按 who 哈希固定到一个 worker）
WORKERS = int(os.environ.get("WX_WORKERS", "3"))
_queues = [queue.Queue() for _ in range(WORKERS)]
# 截屏 / 滚动 / 读历史都要动鼠标键盘，全局串行，否则会互相抢窗口
UI_LOCK = threading.RLock()


def log(*a):
    print(time.strftime("%Y-%m-%d %H:%M:%S"), *a, flush=True)


def api(path, data=None, timeout=120):
    req = urllib.request.Request(VM + path, headers={"X-Token": TOKEN})
    if data is not None:
        req.add_header("Content-Type", "application/json")
        req.data = json.dumps(data, ensure_ascii=False).encode("utf-8")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def find_terminal(o):
    if isinstance(o, dict):
        tr = o.get("terminalReply")
        if isinstance(tr, dict) and tr.get("text"):
            return tr["text"]
        for v in o.values():
            r = find_terminal(v)
            if r:
                return r
    elif isinstance(o, list):
        for v in o:
            r = find_terminal(v)
            if r:
                return r
    return None


def ask(agent, session, text):
    cmd = [OC, "agent", "--agent", agent, "--session-key", session,
           "-m", text, "--json", "--timeout", "180"]
    try:
        p = __import__("subprocess").run(cmd, capture_output=True, text=True,
                                         timeout=300, cwd="/home/wechat")
    except Exception as e:
        log("agent spawn error", repr(e))
        return None
    out = (p.stdout or "").strip()
    if not out:
        log("agent empty stdout rc=%s err=%s" % (p.returncode, (p.stderr or "")[-300:]))
        return None
    i, j = out.find("{"), out.rfind("}")
    if i < 0 or j <= i:
        log("agent no json", out[-300:])
        return None
    try:
        obj = json.loads(out[i:j + 1])
    except Exception as e:
        log("agent bad json", repr(e))
        return None
    return find_terminal(obj)


GROUP_CACHE = {}
IGNORE_CONV = set(x.strip() for x in os.environ.get(
    "WX_IGNORE",
    "腾讯新闻,微信ClawBot,微信团队,文件传输助手,订阅号消息,服务通知"
).split(",") if x.strip())
BOT_NAME = os.environ.get("WX_BOT_NAME", "OpenClaw")
GROUP_AGENT = os.environ.get("WX_GROUP_AGENT", "group")
NL = chr(10)


def is_group(who, sender="", recent=None):
    """判定是不是群聊。

    优先用最可靠的信号：**发言人 != 会话名 → 一定是群**（单聊里 Who 永远等于会话名）。
    探针（/api/group/members）只作为兜底，而且**失败绝不缓存** ——
    否则一次超时就会把群永久记成私聊，从此群里每句话它都接，变成刷屏。
    """
    if who in GROUP_CACHE:
        return GROUP_CACHE[who]
    snd = (sender or "").strip()
    # ⚠️ 自己发出去的话，sender 就是 bot 自己的名字。这个信号没有判别力 ——
    # 拿它当证据的话，agent 只要在私聊里回过一次话，这个私聊就会被**永久**
    # 记成群聊（要求 @ 才回），从此用户不 @ 就再也收不到回复。
    if snd == BOT_NAME:
        snd = ""
    if snd and snd != who:
        GROUP_CACHE[who] = True
        log("probe", who, "-> group (by sender=%s)" % snd)
        return True
    # 只看真人：把 bot 自己从发言人集合里剔掉，否则 recent 里永远两个人
    names = {(m.get("who") or "").strip() for m in (recent or [])} - {"", BOT_NAME}
    if len(names) == 1 and who in names:
        GROUP_CACHE[who] = False
        log("probe", who, "-> direct (by recent)")
        return False
    try:
        with UI_LOCK:                      # 读群成员要动微信窗口，不能和截屏抢
            r = api("/api/group/members?name=" + urllib.parse.quote(who), timeout=30)
        g = bool(r.get("ok") and (r.get("members") or []))
        GROUP_CACHE[who] = g               # 只有探针真的成功了才缓存
        log("probe", who, "-> group" if g else "-> direct")
        return g
    except Exception as e:
        log("group probe error（本次不缓存）", who, repr(e)[:120])
        return False


def is_mentioned(text):
    t = (text or "").replace(chr(0x2005), " ").replace(chr(0xa0), " ")
    return ("@" in t) and (BOT_NAME.lower() in t.lower())


IMAGE_HINTS = ("图片", "[图片]", "【图片】")
STICKER_HINTS = ("动画表情", "[动画表情]", "[表情]", "表情包", "自定义表情")

# 微信各种非文本消息的占位符 -> 给 agent 的诚实说明
# 顺序敏感：先匹配更具体的
KIND_HINTS = [
    (("语音", "[语音]", "【语音】"),
     "一条语音消息。你听不到音频，如实说明，请对方打字或转成文字发来"),
    (("视频", "[视频]", "【视频】"),
     "一条视频。你看不到画面和声音，如实说明，请对方描述或截关键帧发来"),
    (("文件", "[文件]", "【文件】"),
     "一个文件（文档/压缩包等）。你拿不到文件内容，如实说明，"
     "请对方把正文贴过来，或让你做别的事"),
    (("链接", "[链接]", "【链接】"),
     "一个链接卡片。你只看到占位，看不到标题和摘要，请对方把链接文字发来"),
    (("小程序", "[小程序]", "【小程序】"),
     "一个小程序卡片。你打不开小程序，如实说明"),
    (("位置", "[位置]", "【位置】"),
     "一个位置消息。你看不到地图，也不知道是哪，如实说明"),
    (("名片", "[名片]", "【名片】"),
     "一张联系人名片。你只看到占位，看不到是谁，如实说明"),
    (("转账", "[转账]", "【转账】"),
     "一条转账提示。涉及钱的事一律不表态、不承诺、不催促，"
     "只回一句让对方确认清楚，不要给出任何金额相关的建议"),
    (("红包", "[红包]", "【红包】"),
     "一个红包提示。涉及钱的事一律不表态，不要领、不要催"),
    (("通话", "[通话]", "【通话】", "语音通话", "视频通话"),
     "一条通话记录（未接/已接）。你看不到时长细节，简单回应即可"),
    (("撤回", "[撤回]"),
     "一条撤回提示。注意：对方刚刚撤回了消息，你不知道撤的是什么，"
     "不要追问细节，自然接话"),
    (("系统消息", "群公告"),
     "一条系统提示或群公告。你只看到占位，不要假装知道内容"),
]


def decorate(text):
    """把微信的占位符换成人能听懂的说明。返回空串表示这条忽略。"""
    t = (text or "").strip()
    if not t:
        return ""
    for keys, hint in KIND_HINTS:
        if any(t == k or t.startswith(k) for k in keys):
            return "（对方发来%s）" % hint
    if any(t == h or t.startswith(h) for h in STICKER_HINTS):
        return ("（对方发来一个表情包或贴图。你只收到占位文字，看不到画面内容，"
                "不要假装看见了图，可以自然接一句，或问问他是什么意思）")
    if any(t == h or t.startswith(h) for h in IMAGE_HINTS):
        return ("（对方发来一张图片。你只收到占位文字，看不到画面内容，"
                "如实说明看不到图，请他用文字描述一下）")
    return t


def collect_outbox(d, since, token=""):
    """把本轮新产出的文件「领走」。

    同一个 agent 的多个会话共用一个 out 目录，光看 mtime 会两边都抢同一批文件 →
    同一份文件被发两遍，甚至发给错的人。所以用 os.rename 原子领取：
    改名成功的才算自己的，被别人先改走的直接跳过。
    """
    out = []
    if not d or not os.path.isdir(d):
        return out
    try:
        names = os.listdir(d)
    except OSError:
        return out
    box = os.path.join(d, ".claimed", token or "x")
    for name in names:
        p = os.path.join(d, name)
        if not os.path.isfile(p):
            continue
        try:
            if os.path.getmtime(p) < since - 1:
                continue
        except OSError:
            continue
        try:
            os.makedirs(box, exist_ok=True)
            dst = os.path.join(box, name)
            os.replace(p, dst)             # 原子：抢到的才算数
            out.append(dst)
        except OSError:
            continue                        # 已经被别的会话领走
    return sorted(out)


def send_files(who, paths):
    files = []
    for p in paths:
        try:
            if os.path.getsize(p) > MAX_FILE:
                log("SKIP big file", p)
                continue
            with open(p, "rb") as f:
                raw = f.read()
            if not raw:
                continue
            files.append({"name": os.path.basename(p),
                          "data": base64.b64encode(raw).decode("ascii")})
        except Exception as e:
            log("file read error", p, repr(e))
    if not files:
        return False
    try:
        r = api("/api/upload/send", {"who": who, "files": files}, timeout=300)
        log("FILES", who, "count=", len(files), "resp=", str(r)[:160])
        return bool(r.get("ok"))
    except Exception as e:
        log("file send error", repr(e))
        return False


def archive(paths, base=""):
    """发完的文件挪进 <out>/sent/，别堆在 out 里等下一轮被重新收集。"""
    boxes = set()
    for p in paths:
        try:
            root = base or os.path.dirname(p)
            d = os.path.join(root, "sent")
            os.makedirs(d, exist_ok=True)
            os.replace(p, os.path.join(d, os.path.basename(p)))
            boxes.add(os.path.dirname(p))
        except Exception as e:
            log("archive error", repr(e))
    for b in boxes:                        # 清掉 .claimed/<token> 空壳
        try:
            if os.path.basename(os.path.dirname(b)) == ".claimed":
                os.rmdir(b)
        except OSError:
            pass


def group_context(who, limit=25):
    """读群聊最近对话，每行都带发言人。
    只用 SDK 的 history：它带 Who 字段、约 1-7 秒。
    UIA 的 /api/history/deep 试过两条路都不行 —— 一是对带「、」的群名会
    打开错误的会话（读到别的聊天的内容），二是滚动截屏卡住会把整个 wxapi 拖死。"""
    plain = _plain_context(who, limit)
    if not plain:
        # 刚过 0 点的时候「今天」是空的，翻一下昨天的，别让它说「没记录」
        plain = _plain_context(who, limit, day="yesterday")
    log("group ctx", who, len(plain))
    return NL.join(plain[-limit:])


def _plain_context(who, limit, day=""):
    """SDK 的 history：快、带发言人（Who 字段），群聊就靠这个分清谁在说话。"""
    lines = []
    q = "/api/history?who=" + urllib.parse.quote(who)
    if day == "yesterday":
        q += "&date=" + time.strftime("%Y-%m-%d", time.localtime(time.time() - 86400))
    try:
        d = api(q, timeout=90)
        for m in (d.get("items") or [])[-limit:]:
            txt = (m.get("Message") or "").strip()
            if not txt:
                continue
            lines.append(fmt_line(m.get("Who") or "", txt))
    except Exception as e2:
        log("context fallback error", repr(e2))
    return lines


def fmt_line(sender, txt):
    """把一条消息排成「发言人：内容」，说话人一样时就省略前缀。"""
    snd = (sender or "").strip()
    if snd and snd == BOT_NAME:
        snd = "你自己"
    if not snd:
        return "：%s" % txt
    return "%s：%s" % (snd, txt)


def is_image_msg(raw):
    t = (raw or "").strip()
    return any(t == h or t.startswith(h) for h in IMAGE_HINTS)


def find_last_image_index(who):
    """在 UIA 气泡里找最后一条图片消息的下标。表情包跳过。"""
    try:
        d = api("/api/bubbles?who=" + urllib.parse.quote(who), timeout=150)
    except Exception as e:
        log("bubbles error", repr(e))
        return None
    items = d.get("items") or []
    for m in reversed(items):
        cls = m.get("cls") or ""
        nm = (m.get("name") or "").strip()
        if "ChatBubbleRefer" not in cls:
            continue
        if "表情" in nm or "动画" in nm:
            continue
        if "图片" in nm:
            log("image bubble", m.get("i"), repr(nm), cls)
            return m.get("i")
    return None


def shot_image(who, idx, agent=None):
    """把某条消息气泡截屏到 agent 的 inbox 里，返回本地路径。"""
    try:
        r = api("/api/shot?who=%s&index=%d" % (urllib.parse.quote(who), int(idx)),
                timeout=200)
    except Exception as e:
        log("shot error", repr(e))
        return None
    if not r.get("ok"):
        log("shot failed", str(r)[:180])
        return None
    b64 = r.get("base64") or ""
    if not b64:
        return None
    try:
        raw = base64.b64decode(b64)
    except Exception as e:
        log("shot b64 error", repr(e))
        return None
    d = INBOX_DIRS.get(agent) or "/home/wechat/.openclaw/workspace-chat/inbox"
    os.makedirs(d, exist_ok=True)
    p = os.path.join(d, "img_%s.png" % time.strftime("%H%M%S"))
    with open(p, "wb") as f:
        f.write(raw)
    log("shot saved", p, len(raw))
    return p


MAX_DOC_CHARS = 8000


def parse_file_name(raw):
    """微信文件消息的文本长这样：文件 / 文件名 / 150.2K """
    lines = [x.strip() for x in (raw or "").splitlines() if x.strip()]
    if len(lines) >= 2 and lines[0] == "文件":
        return lines[1]
    return ""


def is_file_msg(raw):
    return bool(parse_file_name(raw))


def _office_text(path, low):
    """docx / pptx / xlsx 都是 zip + xml，用标准库就能把正文抠出来。"""
    if low.endswith(".docx"):
        parts = ["word/document.xml"]
    elif low.endswith(".pptx"):
        with zipfile.ZipFile(path) as z:
            parts = sorted(n for n in z.namelist()
                           if n.startswith("ppt/slides/slide") and n.endswith(".xml"))
    else:
        parts = ["xl/sharedStrings.xml"]
    out = []
    with zipfile.ZipFile(path) as z:
        for n in parts:
            try:
                xml = z.read(n).decode("utf-8", "replace")
            except KeyError:
                continue
            xml = xml.replace("</w:p>", "\n").replace("</a:p>", "\n").replace("</w:tr>", "\n")
            out.append(re.sub(r"<[^>]+>", "", xml))
    return "\n".join(out)


def read_document(path):
    """尽量把文件抽成纯文本；抽不出来返回空串（绝不假装读到了）。"""
    low = path.lower()
    try:
        if low.endswith((".txt", ".md", ".csv", ".json", ".log", ".py", ".js", ".html", ".xml", ".yaml", ".yml")):
            return open(path, encoding="utf-8", errors="replace").read()
        if low.endswith(".pdf"):
            r = subprocess.run(["pdftotext", "-layout", "-enc", "UTF-8", path, "-"],
                               capture_output=True, timeout=90)
            return (r.stdout or b"").decode("utf-8", "replace")
        if low.endswith((".docx", ".pptx", ".xlsx")):
            return _office_text(path, low)
    except Exception as e:
        log("read_document error", repr(e)[:140])
    return ""


def fetch_file(who, raw, agent):
    """文件消息 -> 从微信的接收目录把文件取回来 -> 存进 agent 的 inbox -> 抽文本。
    微信收到的文件本来就已经落盘在 xwechat_files 里，不用逆向协议。"""
    name = parse_file_name(raw)
    if not name:
        return None

    hit = None
    for attempt in range(3):                # 刚收到时文件可能还在下载
        try:
            d = api("/api/file/find?name=" + urllib.parse.quote(name), timeout=60)
            items = d.get("items") or []
            if items:
                hit = items[0]
                break
        except Exception as e:
            log("file find error", repr(e)[:120])
        time.sleep(3)

    if not hit:
        log("FILE not on disk", who, name[:60])
        return None

    try:
        g = api("/api/file/get?path=" + urllib.parse.quote(hit["path"]), timeout=300)
        blob = base64.b64decode(g.get("b64") or "")
    except Exception as e:
        log("file get error", repr(e)[:120])
        return None

    d0 = INBOX_DIRS.get(agent) or INBOX_DIRS.get(PRIV_AGENT) or "/tmp"
    try:
        os.makedirs(d0, exist_ok=True)
        dst = os.path.join(d0, "%d_%s" % (int(time.time()), os.path.basename(hit["name"])))
        with open(dst, "wb") as f:
            f.write(blob)
    except Exception as e:
        log("file save error", repr(e)[:120])
        return None

    txt = read_document(dst).strip()
    log("FILE", who, hit["name"], "%d bytes" % len(blob), "text=%d" % len(txt))

    head = ("（对方发来一个文件：%s，约 %.0fKB，已保存到 %s）"
            % (hit["name"], len(blob) / 1024.0, dst))
    if txt:
        more = "\n…（后面还有，全文就在上面那个路径里）" if len(txt) > MAX_DOC_CHARS else ""
        return head + "\n文件内容如下：\n" + txt[:MAX_DOC_CHARS] + more
    return (head + "\n（这个文件我抽不出文字 —— 可能是扫描件、加密 PDF 或者纯图片。"
            "要处理它就直接用这个路径，别猜内容）")


def is_voice_msg(raw):
    return (raw or "").strip().startswith("语音")


def fetch_voice(who, agent=""):
    """语音消息 -> 微信自带的「转文字」。
    气泡的 UIA 名字就是「语音2"秒<转录文本>」，没转过的话尾巴是「未播放」，
    这时让 wxapi 点一下那个自绘的「转文字」按钮再读。"""
    try:
        with UI_LOCK:                       # 会切会话 + 点鼠标，必须串行
            d = api("/api/voice/text?who=%s&wait=8" % urllib.parse.quote(who), timeout=150)
    except Exception as e:
        log("voice error", repr(e)[:140])
        return None
    t = (d.get("transcript") or "").strip()
    if not t:
        log("voice: no transcript", who, (d.get("note") or "")[:80])
        return None
    log("VOICE", who, repr(t[:80]))
    return "（对方发来一条语音，已经转成文字了）语音里说：%s" % t


def ctx_from_recent(recent, limit=8):
    """用 wxapi 随消息一起送来的 recent 拼上下文，省掉一次 7 秒级的读历史。"""
    lines = []
    for m in (recent or [])[-limit:]:
        t = (m.get("text") or "").strip().replace("\n", " ")
        if not t:
            continue
        lines.append(fmt_line(m.get("who") or "", t))
    return NL.join(lines)


def handle_input(who, raw, agent):
    """把一条微信消息变成能交给 agent 的文本；图片、文件都先落盘。"""
    t = (raw or "").strip()
    if is_voice_msg(t):
        got = fetch_voice(who, agent)
        if got:
            return got
        return decorate(t)                  # 转不出来就如实说听不到
    if is_file_msg(t):
        got = fetch_file(who, t, agent)
        if got:
            return got
        return decorate(t)
    return visual_text(who, t, agent)


def visual_text(who, raw, agent=None):
    """图片消息 -> 截屏落盘 -> 给 agent 一个可读的本地路径。
    任何一步失败都退回 decorate 的诚实兜底。"""
    if not is_image_msg(raw):
        return decorate(raw)
    if agent == GROUP_AGENT:
        # 群聊 agent 故意 deny 了 view_image（防止把别人的图当指令去执行），
        # 那截图也没人能看 —— 别让它去调一个必然失败的工具，如实说看不到就行
        log("group image: skip screenshot", who)
        return decorate(raw)
    with UI_LOCK:
        idx = find_last_image_index(who)
        if idx is None:
            log("image but no index", who)
            return decorate(raw)
        p = shot_image(who, idx, agent)
    if not p:
        return decorate(raw)
    return ("（对方发来一张图片。我已经从屏幕上截了一张存在 %s 。"
            "先用 view_image 看一下这个文件再回答；"
            "如果读不到文件，就如实说看不到，不要编图里的内容）" % p)


def send_text(who, text, tries=3):
    """发文本，网络抖动重试；失败只记日志不抛。"""
    for i in range(tries):
        try:
            api("/api/send", {"who": who, "text": text}, timeout=180)
            log("SENT", who, repr(text[:100]))
            return True
        except Exception as e:
            log("send error", who, "try%d" % (i + 1), repr(e)[:120])
            time.sleep(2 + i * 2)
    return False


MD_STRIP = [
    ("**", ""), ("__", ""), ("```", ""), ("`", ""),
    ("### ", ""), ("## ", ""), ("# ", ""),
]

# 列表符只删**行首**的：整行替换会把 "10 - 3 = 7" 变成 "10 3 = 7"
RE_LIST = re.compile(r"^\s*[-*+•]\s+")


def clean_reply(t):
    """微信不渲染 markdown，去掉会露出来的符号；顺手压掉多余空行。"""
    t = (t or "").replace("\r", "")
    for a, b in MD_STRIP:
        t = t.replace(a, b)
    out, blank = [], 0
    for ln in [RE_LIST.sub("", x.rstrip()) for x in t.split("\n")]:
        if ln == "":
            blank += 1
            if blank > 1:
                continue
        else:
            blank = 0
        out.append(ln)
    return "\n".join(out).strip()


def split_long(t, size=500):
    """微信单条太长容易被吞，按句子切成几段发。"""
    if len(t) <= size:
        return [t]
    one = t.replace("！", "！\n").replace("？", "？\n").replace("。", "。\n")
    parts, buf = [], ""
    for seg in one.split("\n"):
        if not seg:
            continue
        if buf and len(buf) + len(seg) > size:
            parts.append(buf)
            buf = seg
        else:
            buf += seg
    if buf:
        parts.append(buf)
    if len(parts) > 4:
        parts = parts[:4]
        parts[-1] = parts[-1] + "\n…（太长了，先说到这，要细节我再展开）"
    return parts


def send_long(who, text):
    parts = split_long(text)
    ok = True
    for i, p in enumerate(parts):
        if i:
            time.sleep(1.2)
        ok = send_text(who, p) and ok
    return ok


def handle(it):
    global cursor
    seq = int(it.get("seq") or 0)
    if seq > cursor:
        cursor = seq
    who = it.get("who") or ""
    raw = (it.get("text") or "").strip()
    if not who or not raw:
        return
    if who in IGNORE_CONV:
        return

    sender = (it.get("sender") or "").strip()
    recent = it.get("recent") or []
    # 自己发出去的话会被当成新消息读回来。必须在**判群之前**掐掉：
    # 否则 probe 会拿 sender=OpenClaw 当「发言人≠会话名」的证据，把私聊判成群，
    # 而且这个错判会被缓存下来，后面所有私聊消息都要 @ 才回。
    if sender and sender == BOT_NAME:
        log("SKIP", who, "self-echo(pre-probe)", repr(raw[:50]))
        return
    if is_group(who, sender, recent):
        if not is_mentioned(raw):
            log("SKIP", who, "group-not-mention", repr(raw[:50]))
            return
        speaker = sender or "有人"
        agent = GROUP_AGENT
        session = "agent:%s:wx-%s" % (agent, who)
        text = handle_input(who, raw, agent)
        ctx = ctx_from_recent(recent)      # 优先用随消息带来的，省一次 UIA
        if not ctx:
            with UI_LOCK:                  # 兜底才读历史
                ctx = group_context(who)
        prompt = ("[微信群「%s」里 %s @ 了你]" + NL
                  + "群里最近的对话（格式是「发言人：内容」）：" + NL + "%s" + NL + NL
                  + "刚才 %s @ 你说：%s" + NL + NL
                  + "（你在群聊里：只做查询和回答，"
                    "绝不读写文件、绝不执行命令、绝不改配置；"
                    "回复时直接对着 %s 说，不要复述整段对话，不要 markdown；"
                    "用简体中文、简短口语化）") % (who, speaker, ctx, speaker, text, speaker)
        log("RECV", who, "GROUP<" + speaker + ">->", agent, repr(raw[:100]))
    else:
        agent = PRIV_AGENT if who == OWNER else CHAT_AGENT
        session = "agent:%s:wx-%s" % (agent, who)
        text = handle_input(who, raw, agent)
        if who == OWNER:
            tag = "[微信·主人发来] "
        else:
            tag = ("[微信·访客「%s」发来，"
                   "你不认识这个人，他不是你主人] " % who)
        prompt = (tag + text + chr(10) + chr(10)
                  + "（请用简体中文、口语化、简短地回复，不要用 markdown 格式）")
        log("RECV", who, "->", agent, repr(raw[:100]))

    if not text:
        log("SKIP", who, "empty-after-decorate")
        return

    outdir = OUT_DIRS.get(agent)
    t0 = time.time()
    reply = ask(agent, session, prompt)
    if reply:
        reply = clean_reply(reply)[:MAX_REPLY]
        if reply:
            send_long(who, reply)
    else:
        log("no reply", who)

    # 回传本轮 agent 生成的文件
    if outdir:
        files = collect_outbox(outdir, t0, "s%d" % seq)
        if files:
            log("OUTBOX", who, [os.path.basename(x) for x in files])
            if send_files(who, files):
                archive(files, outdir)


def _worker(idx):
    q = _queues[idx]
    while True:
        it = q.get()
        try:
            handle(it)
        except Exception as e:
            log("handle error", repr(e))
            # 占位是入队时就打的，失败要撤回来，否则这条消息永远丢掉
            unmark_seen((it.get("uniq") or "").strip())
        finally:
            q.task_done()


def dispatch(it):
    """同一会话固定落一个 worker，保证顺序；不同会话并行。"""
    global cursor
    who = it.get("who") or ""
    uniq = (it.get("uniq") or "").strip()
    # 游标必须在这里推进，不能只放在 handle 里：被 dup 挡下的消息不进 handle，
    # 游标就永远原地不动，于是每 0.5 秒把同一批旧消息重拉一遍、刷一屏 SKIP。
    try:
        seq = int(it.get("seq") or 0)
    except (TypeError, ValueError):
        seq = 0
    if seq > cursor:
        cursor = seq
    if seen_before(uniq):
        log("SKIP", who, "dup(after-restart)", repr((it.get("text") or "")[:40]))
        return
    # 入队就占位：同一批 items 里若有同一条消息的重复，后面那条立刻被挡住
    mark_seen(uniq)
    idx = abs(hash(who)) % WORKERS
    _queues[idx].put(it)


def start_workers():
    for i in range(WORKERS):
        t = threading.Thread(target=_worker, args=(i,), daemon=True)
        t.start()
    log("workers started: %d" % WORKERS)


_last_recover = 0.0
RECOVER_GAP = 240.0     # 4 分钟内不重复重启：wxapi 冷启动本来就要 30~60 秒


def recover_vm():
    """wxapi 卡死时，进 VM 强制重启（详见 C:\\setup\\restart-wxapi.cmd）。"""
    global _last_recover
    now = time.time()
    if now - _last_recover < RECOVER_GAP:
        log("WATCHDOG: %ds 内刚重启过，这次只等它起来" % int(now - _last_recover))
        return
    _last_recover = now
    try:
        log("WATCHDOG: wxapi 无响应，进 VM 重启")
        r = subprocess.run([RECOVER_SH], capture_output=True, timeout=180)
        out = (r.stdout or b"").decode("utf-8", "replace")[-200:]
        err = (r.stderr or b"").decode("utf-8", "replace")[-200:]
        log("WATCHDOG rc=%d out=%s err=%s" % (r.returncode, out, err))
    except Exception as e:
        log("WATCHDOG error", repr(e))


def main():
    global cursor, last_accept
    log("wxbridge started: vm=%s owner=%s priv=%s chat=%s" % (VM, OWNER, PRIV_AGENT, CHAT_AGENT))
    start_workers()
    fails = 0
    last_reset = 0.0
    epoch = None            # wxapi 进程代号：只有它变了才是真的重启
    while True:
        try:
            # 10 秒足够：wxapi 那边最慢的一次 GetChatHistory 也就 7 秒。
            # 原来给 30 秒，一旦它线程池被打满，桥会白等半分钟，期间消息全堵着。
            d = api("/api/inbound?after=%d" % cursor, timeout=10)
            fails = 0
            # 游标重置只能依据「wxapi 换了个进程」，不能依据 last < cursor：
            # last 是队列里最后一条的序号，队列空时就是 0，这跟重启长得一模一样。
            # 拿 last 判断的话，队列一空就会「重置→重拉→再重置」无限循环。
            ep = d.get("epoch")
            if ep is not None:
                if epoch is not None and ep != epoch:
                    log("wxapi 重启（epoch %s -> %s），游标归零" % (epoch, ep))
                    cursor = 0
                epoch = ep
            else:
                # 旧版 wxapi 没有 epoch，退回 last 判断，但加 60 秒冷却，
                # 免得每轮都重置把日志刷满
                try:
                    last = int(d.get("last") or 0)
                except (TypeError, ValueError):
                    last = 0
                if last < cursor and time.time() - last_reset > 60:
                    last_reset = time.time()
                    log("seq reset（旧版接口）: last=%d cursor=%d -> 0" % (last, cursor))
                    cursor = 0
            for it in (d.get("items") or []):
                try:
                    dispatch(it)
                except Exception as e:
                    log("dispatch error", repr(e))
            now = time.time()
            if now - last_accept >= ACCEPT_INTERVAL:
                last_accept = now
                try:
                    r = api("/api/friend/accept", {}, timeout=200)
                    if r.get("passedCount"):
                        log("AUTO-ACCEPT", r.get("passed"))
                except Exception as e:
                    log("accept error", repr(e))
        except Exception as e:
            fails += 1
            log("loop error", repr(e), "fails=%d" % fails)
            if fails >= FAIL_LIMIT:
                recover_vm()
                fails = 0
                time.sleep(25)
                continue
            # 失败后别每 0.5 秒打一次：1→2→3 秒递增，最多 5 秒
            time.sleep(min(fails, 5))
            continue
        time.sleep(POLL_INTERVAL)


if __name__ == "__main__":
    main()
