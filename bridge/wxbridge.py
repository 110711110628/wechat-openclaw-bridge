#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""wxbridge — VM 微信 <-> OpenClaw 双向桥

链路：VM(192.168.122.100:18800 wxapi) -> 本机 OpenClaw agent -> wxapi /api/send
- 主人发的消息 = 指令，走 owner（全权 agent）
- 其他人发的消息 = 纯聊天，走 chat（零工具 agent）
"""
import base64
import json
import os
import subprocess
import sys
import time
import urllib.parse
import urllib.request

VM = os.environ.get("WXAPI_URL", "http://192.168.122.100:18800")
TOKEN = os.environ.get("WXAPI_TOKEN", "CHANGE_ME_WXAPI_TOKEN")
OWNER = os.environ.get("WX_OWNER", "主人")
PRIV_AGENT = os.environ.get("WX_PRIV_AGENT", "owner")
CHAT_AGENT = os.environ.get("WX_CHAT_AGENT", "chat")
ACCEPT_INTERVAL = int(os.environ.get("WX_ACCEPT_INTERVAL", "30"))
POLL_INTERVAL = 1
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
FAIL_LIMIT = int(os.environ.get("WX_FAIL_LIMIT", "4"))
RECOVER_SH = "/opt/wxbridge/recover-wxapi.sh"


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


def is_group(who):
    if who in GROUP_CACHE:
        return GROUP_CACHE[who]
    g = False
    try:
        r = api("/api/group/members?name=" + urllib.parse.quote(who), timeout=90)
        g = bool(r.get("ok") and (r.get("members") or []))
    except Exception as e:
        log("group probe error", repr(e))
    GROUP_CACHE[who] = g
    log("probe", who, "-> group" if g else "-> direct")
    return g


def is_mentioned(text):
    t = (text or "").replace(chr(0x2005), " ").replace(chr(0xa0), " ")
    return ("@" in t) and (BOT_NAME.lower() in t.lower())


IMAGE_HINTS = ("图片", "[图片]", "【图片】")
STICKER_HINTS = ("动画表情", "[动画表情]", "[表情]", "表情包", "自定义表情")


def decorate(text):
    """把微信的占位符换成人能听懂的说明。返回空串表示这条忽略。"""
    t = (text or "").strip()
    if not t:
        return ""
    if any(t == h or t.startswith(h) for h in STICKER_HINTS):
        return ("（对方发来一个表情包或贴图。你只收到占位文字，看不到画面内容，"
                "不要假装看见了图，可以自然接一句，或问问他是什么意思）")
    if any(t == h or t.startswith(h) for h in IMAGE_HINTS):
        return ("（对方发来一张图片。你只收到占位文字，看不到画面内容，"
                "如实说明看不到图，请他用文字描述一下）")
    return t


def collect_outbox(d, since):
    out = []
    if not d or not os.path.isdir(d):
        return out
    try:
        names = os.listdir(d)
    except OSError:
        return out
    for name in names:
        p = os.path.join(d, name)
        if not os.path.isfile(p):
            continue
        try:
            if os.path.getmtime(p) >= since - 1:
                out.append(p)
        except OSError:
            pass
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


def archive(paths):
    for p in paths:
        try:
            d = os.path.join(os.path.dirname(p), "sent")
            os.makedirs(d, exist_ok=True)
            os.replace(p, os.path.join(d, os.path.basename(p)))
        except Exception as e:
            log("archive error", repr(e))


def group_context(who, limit=25):
    """读群聊最近对话。优先用 deep（带滚动，条数更多），失败回落到普通 history。"""
    global deep_block_until
    lines = []
    if time.time() < deep_block_until:
        lines = _plain_context(who, limit)
    else:
        try:
            d = api("/api/history/deep?who=%s&pages=2&limit=%d"
                    % (urllib.parse.quote(who), limit), timeout=90)
            for m in (d.get("items") or []):
                t = (m.get("text") or "").strip()
                if not t:
                    continue
                cls = m.get("type") or ""
                if cls.endswith("ChatItemView") and "ChatTextItemView" not in cls and "ChatBubble" not in cls:
                    lines.append("[" + t + "]")
                else:
                    lines.append(t)
            log("group ctx deep", who, len(lines))
        except Exception as e:
            log("deep context error", repr(e))
            # deep 卡住会把整个 wxapi 拖死，失败后冷却 10 分钟只用普通 history
            deep_block_until = time.time() + 600
            lines = _plain_context(who, limit)
    return NL.join(lines[-limit:])


def _plain_context(who, limit):
    lines = []
    try:
        d = api("/api/history?who=" + urllib.parse.quote(who), timeout=90)
        for m in (d.get("items") or [])[-limit:]:
            lines.append("%s  %s" % (m.get("SendDateTime", ""),
                                     (m.get("Message") or "").strip()))
    except Exception as e2:
        log("context fallback error", repr(e2))
    return lines


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


def visual_text(who, raw, agent=None):
    """图片消息 -> 截屏落盘 -> 给 agent 一个可读的本地路径。
    任何一步失败都退回 decorate 的诚实兜底。"""
    if not is_image_msg(raw):
        return decorate(raw)
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

    if is_group(who):
        if not is_mentioned(raw):
            log("SKIP", who, "group-not-mention", repr(raw[:50]))
            return
        agent = GROUP_AGENT
        session = "agent:%s:wx-%s" % (agent, who)
        text = visual_text(who, raw, agent)
        ctx = group_context(who)
        prompt = ("[微信群「%s」里有人 @ 了你]" + NL
                  + "群里最近的对话：" + NL + "%s" + NL + NL
                  + "刚才有人 @ 你说：%s" + NL + NL
                  + "（你在群聊里：只做查询和回答，"
                    "绝不读写文件、绝不执行命令、绝不改配置；"
                    "用简体中文、简短口语化回复，不要 markdown）") % (who, ctx, text)
        log("RECV", who, "GROUP->", agent, repr(raw[:100]))
    else:
        agent = PRIV_AGENT if who == OWNER else CHAT_AGENT
        session = "agent:%s:wx-%s" % (agent, who)
        text = visual_text(who, raw, agent)
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
        reply = reply.strip()[:MAX_REPLY]
        if reply:
            try:
                api("/api/send", {"who": who, "text": reply}, timeout=180)
                log("SENT", who, repr(reply[:100]))
            except Exception as e:
                log("send error", repr(e))
    else:
        log("no reply", who)

    # 回传本轮 agent 生成的文件
    if outdir:
        files = collect_outbox(outdir, t0)
        if files:
            log("OUTBOX", who, [os.path.basename(x) for x in files])
            if send_files(who, files):
                archive(files)


def recover_vm():
    """wxapi 卡死时，进 VM 强制重启（详见 C:\\setup\\restart-wxapi.cmd）。"""
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
    fails = 0
    while True:
        try:
            d = api("/api/inbound?after=%d" % cursor, timeout=30)
            fails = 0
            # wxapi 重启后 seq 会归零，游标必须跟着退回去，否则永远拉不到新消息
            try:
                last = int(d.get("last") or 0)
            except (TypeError, ValueError):
                last = 0
            if last < cursor:
                log("seq reset detected: last=%d cursor=%d -> reset to 0" % (last, cursor))
                cursor = 0
            for it in (d.get("items") or []):
                try:
                    handle(it)
                except Exception as e:
                    log("handle error", repr(e))
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
        time.sleep(POLL_INTERVAL)


if __name__ == "__main__":
    main()
