#!/bin/bash
# recover-wxapi.sh — called by the wxbridge watchdog when wxapi stops responding.
#
# It SSHes into the Windows VM and runs tools/restart-wxapi.cmd, which kills the
# wedged process and relaunches the scheduled task. Then it polls /api/status
# until the service is back online (max 90s).
#
# Credentials are NOT stored here. Provide them via environment variables:
#   export VM_HOST='user@host'
#   export SSHPASS='your-vm-password'        # consumed by sshpass -e
#   export WXAPI_TOKEN='your-api-token'
set -u

VM_HOST="${VM_HOST:-wechat@192.168.122.100}"
TOKEN="${WXAPI_TOKEN:-CHANGE_ME_WXAPI_TOKEN}"
API="http://${VM_HOST#*@}:18800"
REMOTE_CMD="${REMOTE_CMD:-C:\\setup\\restart-wxapi.cmd}"

if [ -z "${SSHPASS:-}" ]; then
  echo "[recover] SSHPASS not set; cannot restart VM service" >&2
  exit 2
fi

echo "[recover] $(date '+%F %T') restart wxapi on $VM_HOST"
sshpass -e ssh -n -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 \
  "$VM_HOST" "$REMOTE_CMD" 2>&1 | tail -3

for i in $(seq 1 18); do
  sleep 5
  r=$(curl -s -m 5 -H "X-Token: $TOKEN" "$API/api/status" || true)
  case "$r" in
    *'"online":true'*) echo "[recover] online after ~$((i*5))s"; exit 0;;
  esac
done

echo "[recover] still offline: $r" >&2
exit 1
