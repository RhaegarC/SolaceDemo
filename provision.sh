#!/usr/bin/env bash
#
# Creates the broker objects the demo needs:
#
#   queue    test/integration/dataTran/sysname/cc/case/attach/q
#                                   durable, so it spools while the consumer is down
#   topic    case/>                 subscribed to that queue, which is how the
#                                   publisher reaches it without knowing it exists
#   acl      case-attach            publish to case/>, consume that queue, and
#                                   nothing else
#   user     appuser                a limited client user carrying that ACL
#
# The apps connect as appuser, never as admin: provisioning is an operator action, not
# something the demo runs at startup.
#
# Works against any broker — local or a remote Linux host — and is idempotent, so
# re-running it is safe.
#
# Usage:
#   ./provision.sh
#   SOLACE_SMF_HOST=solace.internal SOLACE_SEMP_PORT=8080 ./provision.sh

set -uo pipefail

SMF_HOST="${SOLACE_SMF_HOST:-localhost}"
SEMP_PORT="${SOLACE_SEMP_PORT:-8080}"
SEMP_SCHEME="${SOLACE_SEMP_SCHEME:-http}"
VPN="${SOLACE_VPN:-default}"

ADMIN_USER="${SOLACE_ADMIN_USER:-admin}"
ADMIN_PASSWORD="${SOLACE_ADMIN_PASSWORD:-QAZwsx.123456}"

QUEUE="${SOLACE_QUEUE:-test/integration/dataTran/sysname/cc/case/attach/q}"
TOPIC_PREFIX="${SOLACE_TOPIC_PREFIX:-case}"
SUBSCRIPTION="${SOLACE_SUBSCRIPTION:-${TOPIC_PREFIX}/>}"
ACL_PROFILE="${SOLACE_ACL_PROFILE:-case-attach}"
APP_USER="${SOLACE_APP_USER:-appuser}"
APP_PASSWORD="${SOLACE_APP_PASSWORD:-apppass}"

SMF_HOST="${SMF_HOST#tcp://}"
SEMP_BASE="${SEMP_SCHEME}://${SMF_HOST}:${SEMP_PORT}/SEMP/v2/config"
AUTH="${ADMIN_USER}:${ADMIN_PASSWORD}"

# The queue name contains "/", and in a SEMP path that is a path separator rather than
# part of the name: ".../queues/test/integration/.../q" addresses a resource that does
# not exist. Percent-encode the name wherever it appears in a path — the same applies to
# the subscription topic, whose ">" is also not path-safe.
urlencode() {
    local value="$1" encoded="" char i
    for ((i = 0; i < ${#value}; i++)); do
        char="${value:i:1}"
        case "$char" in
            [A-Za-z0-9._~-]) encoded+="$char" ;;
            *) encoded+="$(printf '%%%02X' "'$char")" ;;
        esac
    done
    printf '%s' "$encoded"
}

QUEUE_PATH="$(urlencode "$QUEUE")"
SUBSCRIPTION_PATH="$(urlencode "$SUBSCRIPTION")"

TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT

failures=0

# Prints the HTTP status and leaves the response body in $TMP.
request() {
    local method="$1" path="$2" body="${3:-}"
    if [ -n "$body" ]; then
        curl -sS -o "$TMP" -w '%{http_code}' -X "$method" \
            -u "$AUTH" -H 'Content-Type: application/json' \
            -d "$body" "${SEMP_BASE}${path}" 2>/dev/null || echo "000"
    else
        curl -sS -o "$TMP" -w '%{http_code}' -X "$method" \
            -u "$AUTH" "${SEMP_BASE}${path}" 2>/dev/null || echo "000"
    fi
}

# The broker reports the reason in meta.error; surface it verbatim rather than
# guessing, so a wrong field name is obvious instead of mysterious.
report_failure() {
    printf '    broker said: %s\n' "$(tr -d '\n' < "$TMP" | head -c 600)"
    failures=$((failures + 1))
}

# GET the object itself first, POST only if absent. Checking beats matching on an error
# string — but it has to be the *object's* path, not the collection's: GET on a collection
# answers 200 with an empty list, so a collection check calls every object present and
# creates nothing.
#
#   ensure <post-path> <body> <label> <check-path>
ensure() {
    local path="$1" body="$2" label="$3" check="$4" code

    code="$(request GET "$check")"
    if [ "$code" = "200" ]; then
        printf '  %-28s already exists\n' "$label"
        return 0
    fi

    code="$(request POST "$path" "$body")"
    if [ "$code" = "200" ]; then
        printf '  %-28s created\n' "$label"
        return 0
    fi

    printf '  %-28s FAILED (HTTP %s)\n' "$label" "$code"
    report_failure
    return 1
}

printf 'Provisioning %s (vpn %s)\n\n' "$SEMP_BASE" "$VPN"

printf 'Reachability\n'
if [ "$(request GET "/msgVpns/${VPN}")" != "200" ]; then
    printf '  cannot reach the broker at %s\n' "$SEMP_BASE"
    report_failure
    printf '\nIs the broker running, and is SEMP enabled on port %s?\n' "$SEMP_PORT"
    exit 1
fi
printf '  %-28s ok\n\n' "message-vpn ${VPN}"

printf 'Access control\n'

# clientConnectDefaultAction stays "allow": a user-defined ACL profile defaults it to
# "disallow", and connect exceptions are matched by client IP rather than by username, so
# disallowing here would refuse appuser before any publish rule was ever consulted. The
# profile is only ever assigned to appuser, and publishing and subscribing are still shut
# by default below, so this is the connect half of least privilege rather than a hole.
ensure "/msgVpns/${VPN}/aclProfiles" \
    "$(printf '{"aclProfileName":"%s","clientConnectDefaultAction":"allow","publishTopicDefaultAction":"disallow","subscribeTopicDefaultAction":"disallow"}' "$ACL_PROFILE")" \
    "acl profile" \
    "/msgVpns/${VPN}/aclProfiles/${ACL_PROFILE}"

ensure "/msgVpns/${VPN}/aclProfiles/${ACL_PROFILE}/publishTopicExceptions" \
    "$(printf '{"publishTopicException":"%s","publishTopicExceptionSyntax":"smf"}' "$SUBSCRIPTION")" \
    "publish ${SUBSCRIPTION}" \
    "/msgVpns/${VPN}/aclProfiles/${ACL_PROFILE}/publishTopicExceptions/${SUBSCRIPTION_PATH}"

# A queue subscription is expressed to the ACL as the queue name.
ensure "/msgVpns/${VPN}/aclProfiles/${ACL_PROFILE}/subscribeTopicExceptions" \
    "$(printf '{"subscribeTopicException":"%s","subscribeTopicExceptionSyntax":"smf"}' "$QUEUE")" \
    "consume ${QUEUE}" \
    "/msgVpns/${VPN}/aclProfiles/${ACL_PROFILE}/subscribeTopicExceptions/${QUEUE_PATH}"

ensure "/msgVpns/${VPN}/clientUsernames" \
    "$(printf '{"clientUsername":"%s","password":"%s","enabled":true,"aclProfileName":"%s"}' "$APP_USER" "$APP_PASSWORD" "$ACL_PROFILE")" \
    "client user ${APP_USER}" \
    "/msgVpns/${VPN}/clientUsernames/${APP_USER}"

printf '\nEndpoint\n'
ensure "/msgVpns/${VPN}/queues" \
    "$(printf '{"queueName":"%s","permission":"consume","ingressEnabled":true,"egressEnabled":true}' "$QUEUE")" \
    "queue ${QUEUE}" \
    "/msgVpns/${VPN}/queues/${QUEUE_PATH}"

ensure "/msgVpns/${VPN}/queues/${QUEUE_PATH}/subscriptions" \
    "$(printf '{"subscriptionTopic":"%s"}' "$SUBSCRIPTION")" \
    "topic ${SUBSCRIPTION}" \
    "/msgVpns/${VPN}/queues/${QUEUE_PATH}/subscriptions/${SUBSCRIPTION_PATH}"

printf '\nVerifying\n'
verify() {
    local path="$1" needle="$2" label="$3" code
    code="$(request GET "$path")"
    if [ "$code" = "200" ] && grep -q "$needle" "$TMP"; then
        printf '  %-28s ok\n' "$label"
        return 0
    fi
    printf '  %-28s MISSING (HTTP %s)\n' "$label" "$code"
    failures=$((failures + 1))
    return 1
}

verify "/msgVpns/${VPN}/queues/${QUEUE_PATH}" "$QUEUE" "queue exists"
verify "/msgVpns/${VPN}/queues/${QUEUE_PATH}/subscriptions" "$SUBSCRIPTION" "subscription exists"
verify "/msgVpns/${VPN}/clientUsernames/${APP_USER}" "$APP_USER" "client user exists"

# The broker's own ceiling. The publisher's configured cap has to stay under this,
# and the apps print it at connect so the two can be compared.
printf '\nBroker limits\n'
if [ "$(request GET "/msgVpns/${VPN}")" = "200" ]; then
    max_msg_size="$(grep -o '"maxMsgSize"[[:space:]]*:[[:space:]]*[0-9]*' "$TMP" | grep -o '[0-9]*$' | head -1)"
    if [ -n "${max_msg_size:-}" ]; then
        printf '  maxMsgSize                    %s bytes\n' "$max_msg_size"
    else
        printf '  maxMsgSize                    not reported by this broker\n'
    fi
fi

if [ "$failures" -gt 0 ]; then
    printf '\n%d step(s) failed.\n' "$failures"
    exit 1
fi

printf '\nProvisioned. Start the consumer, then the client:\n'
printf '  dotnet run --project src/SolaceConsumer\n'
printf '  dotnet run --project src/SolaceClient      # or F5 in Visual Studio\n'
