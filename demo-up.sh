#!/usr/bin/env bash
#
# Brings the demo up: waits for the broker to actually answer, then provisions it.
#
# The broker itself is not started here. It may be a container on this machine or, as is
# more likely for this demo, one running on a Linux server — either way it is started by
# whoever owns it, and this script's job is to not race it and to leave the broker in a
# known state.
#
# Readiness is polled through SEMP rather than by checking that the SMF port accepts a
# connection: SMF starts listening well before the broker is able to create endpoints,
# so a port check passes too early and provisioning then fails for no visible reason.
#
# Idempotent — safe to re-run.
#
# Usage:
#   ./demo-up.sh
#   SOLACE_SMF_HOST=solace.internal ./demo-up.sh

set -uo pipefail

export SOLACE_SMF_HOST="${SOLACE_SMF_HOST:-localhost}"
export SOLACE_SEMP_PORT="${SOLACE_SEMP_PORT:-8080}"
export SOLACE_SEMP_SCHEME="${SOLACE_SEMP_SCHEME:-http}"
export SOLACE_VPN="${SOLACE_VPN:-default}"
export SOLACE_ADMIN_USER="${SOLACE_ADMIN_USER:-admin}"
export SOLACE_ADMIN_PASSWORD="${SOLACE_ADMIN_PASSWORD:-QAZwsx.123456}"

WAIT_SECONDS="${SOLACE_WAIT_SECONDS:-180}"

HOST="${SOLACE_SMF_HOST#tcp://}"
SEMP_BASE="${SOLACE_SEMP_SCHEME}://${HOST}:${SOLACE_SEMP_PORT}/SEMP/v2/config"
AUTH="${SOLACE_ADMIN_USER}:${SOLACE_ADMIN_PASSWORD}"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

printf 'Waiting for the broker at %s\n' "$SEMP_BASE"

deadline=$((SECONDS + WAIT_SECONDS))
ready=0

while [ "$SECONDS" -lt "$deadline" ]; do
    code="$(curl -s -o /dev/null -w '%{http_code}' -u "$AUTH" \
        "${SEMP_BASE}/msgVpns/${SOLACE_VPN}" 2>/dev/null || echo 000)"

    if [ "$code" = "200" ]; then
        ready=1
        break
    fi

    printf '.'
    sleep 3
done

printf '\n'

if [ "$ready" -ne 1 ]; then
    cat <<EOF
Broker did not answer within ${WAIT_SECONDS}s (last HTTP status: ${code:-000}).

Nothing here starts a broker — start yours first, then re-run this script.
For a container on a Linux host, the equivalent of docs/note.md is:

  docker run -d -p 8080:8080 -p 55555:55555 --shm-size=1g \\
    -v /var/lib/solace:/var/lib/solace \\
    --env username_admin_globalaccesslevel=admin \\
    --env username_admin_password=<password> \\
    --name=solace solace/solace-pubsub-standard

Then point this script and both apps at it with SOLACE_SMF_HOST=<host>.
A first boot takes a minute or two before SEMP answers.
EOF
    exit 1
fi

printf 'Broker is up.\n\n'

exec "${script_dir}/provision.sh"
