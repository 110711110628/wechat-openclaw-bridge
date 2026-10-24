#!/bin/bash
# wxbridge-watchdog.sh —— 每 2 分钟探一次 VM 里的 wxapi，连续 3 次无应答才进 VM 重启。
#
# 为什么需要它：
#   UI Automation 调 WPF 控件时，SDK 内部一旦卡住，HTTP 端点会全部超时；
#   哪怕进程还在，18800 也等于死了。bridge 自己的看门狗只管「轮询连续失败」，
#   如果 bridge 也停了或者重启中，就没人管 wxapi 了 —— 所以再挂一个定时器。
#
# 装法：
#   install -m 755 wxbridge-watchdog.sh /opt/wxbridge/
#   install -m 644 wxbridge-watchdog.service wxbridge-watchdog.timer /etc/systemd/system/
#   systemctl daemon-reload && systemctl enable --now wxbridge-watchdog.timer
set -u
TOKEN="${WXAPI_TOKEN:-CHANGE_ME_WXAPI_TOKEN}"
URL="${WXAPI_URL:-http://192.168.122.100:18800}/api/status"
RECOVER="$(dirname "$0")/recover-wxapi.sh"

for i in 1 2 3; do
  r=$(curl -s -m 6 -H "X-Token: $TOKEN" "$URL" || true)
  case "$r" in
    *'"online":true'*) exit 0 ;;
  esac
  sleep 20
done

echo "[$(date '+%F %T')] wxapi 连续 3 次无应答，触发恢复" | systemd-cat -t wxbridge-watchdog
exec "$RECOVER"
