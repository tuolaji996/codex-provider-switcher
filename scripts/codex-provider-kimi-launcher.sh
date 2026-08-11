#!/bin/sh
set -eu

umask 077

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
ROUTER="$SCRIPT_DIR/linux-x64/CodexProviderKimiRouter"
BROKER="$SCRIPT_DIR/CodexProviderToken.exe"
STATE_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/codex-provider-switcher"
PID_FILE="$STATE_DIR/kimi-router.pid"
LOCK_DIR="$STATE_DIR/kimi-router.start.lock"
LOG_FILE="$STATE_DIR/kimi-router.log"
HEALTH_URL="http://127.0.0.1:17866/health"

MODE=token
CREDENTIAL_TARGET=

usage_error() {
    printf '%s\n' 'K3 launcher arguments are invalid.' >&2
    exit 3
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --credential-target)
            [ "$#" -ge 2 ] || usage_error
            [ -z "$CREDENTIAL_TARGET" ] || usage_error
            [ "$MODE" = token ] || usage_error
            CREDENTIAL_TARGET=$2
            shift 2
            ;;
        --ensure-only)
            [ "$MODE" = token ] || usage_error
            MODE=ensure
            shift
            ;;
        --health)
            [ "$MODE" = token ] || usage_error
            MODE=health
            shift
            ;;
        --stop)
            [ "$MODE" = token ] || usage_error
            MODE=stop
            shift
            ;;
        *)
            usage_error
            ;;
    esac
done

mkdir -p "$STATE_DIR"
chmod 700 "$STATE_DIR" 2>/dev/null || true

probe_health() {
    if command -v curl >/dev/null 2>&1; then
        body=$(curl --silent --show-error --fail --max-time 1 "$HEALTH_URL" 2>/dev/null) || return 1
    elif command -v wget >/dev/null 2>&1; then
        body=$(wget -q -T 1 -O - "$HEALTH_URL" 2>/dev/null) || return 1
    else
        printf '%s\n' 'K3 launcher requires curl or wget inside WSL.' >&2
        return 1
    fi

    printf '%s' "$body" | grep -F '"status":"ok"' >/dev/null 2>&1 &&
        printf '%s' "$body" | grep -F '"service":"codex-provider-kimi-router"' >/dev/null 2>&1 &&
        printf '%s' "$body" | grep -F '"upstream":"https://sui-xiang.com/v1"' >/dev/null 2>&1 || return 1

    health_pid=$(printf '%s' "$body" |
        sed -n 's/.*"pid"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' |
        sed -n '1p')
    pid_matches_router "$health_pid"
}

router_path() {
    readlink -f "$ROUTER" 2>/dev/null || printf '%s' "$ROUTER"
}

pid_matches_router() {
    pid=$1
    case "$pid" in
        ''|*[!0-9]*) return 1 ;;
    esac
    [ -e "/proc/$pid/exe" ] || return 1
    actual=$(readlink -f "/proc/$pid/exe" 2>/dev/null || true)
    [ -n "$actual" ] && [ "$actual" = "$(router_path)" ]
}

find_router_pid() {
    if [ -f "$PID_FILE" ]; then
        candidate=$(sed -n '1p' "$PID_FILE" 2>/dev/null || true)
        if pid_matches_router "$candidate"; then
            printf '%s' "$candidate"
            return 0
        fi
        rm -f "$PID_FILE"
    fi

    for executable in /proc/[0-9]*/exe; do
        [ -e "$executable" ] || continue
        candidate=${executable#/proc/}
        candidate=${candidate%/exe}
        if pid_matches_router "$candidate"; then
            printf '%s' "$candidate"
            return 0
        fi
    done
    return 1
}

stop_router() {
    candidate=$(find_router_pid 2>/dev/null || true)
    if [ -z "$candidate" ]; then
        rm -f "$PID_FILE"
        return 0
    fi

    kill "$candidate" 2>/dev/null || true
    attempts=0
    while pid_matches_router "$candidate" && [ "$attempts" -lt 50 ]; do
        sleep 0.1
        attempts=$((attempts + 1))
    done
    if pid_matches_router "$candidate"; then
        kill -9 "$candidate" 2>/dev/null || true
    fi
    rm -f "$PID_FILE"
}

release_lock() {
    rm -f "$LOCK_DIR/owner" 2>/dev/null || true
    rmdir "$LOCK_DIR" 2>/dev/null || true
}

acquire_lock() {
    attempts=0
    while ! mkdir "$LOCK_DIR" 2>/dev/null; do
        owner=$(sed -n '1p' "$LOCK_DIR/owner" 2>/dev/null || true)
        case "$owner" in
            ''|*[!0-9]*)
                rm -f "$LOCK_DIR/owner" 2>/dev/null || true
                rmdir "$LOCK_DIR" 2>/dev/null || true
                ;;
            *)
                if ! kill -0 "$owner" 2>/dev/null; then
                    rm -f "$LOCK_DIR/owner" 2>/dev/null || true
                    rmdir "$LOCK_DIR" 2>/dev/null || true
                fi
                ;;
        esac
        attempts=$((attempts + 1))
        [ "$attempts" -lt 100 ] || {
            printf '%s\n' 'Timed out waiting for the K3 router startup lock.' >&2
            return 1
        }
        sleep 0.1
    done
    printf '%s\n' "$$" > "$LOCK_DIR/owner"
    trap release_lock EXIT HUP INT TERM
}

ensure_router() {
    probe_health && return 0
    [ -f "$ROUTER" ] || {
        printf '%s\n' "Bundled WSL K3 router is missing: $ROUTER" >&2
        return 1
    }

    acquire_lock || return 1
    if probe_health; then
        release_lock
        trap - EXIT HUP INT TERM
        return 0
    fi

    chmod 700 "$ROUTER" 2>/dev/null || true
    : > "$LOG_FILE"
    chmod 600 "$LOG_FILE" 2>/dev/null || true
    if command -v setsid >/dev/null 2>&1; then
        DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
            setsid "$ROUTER" </dev/null >>"$LOG_FILE" 2>&1 &
    else
        DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
            nohup "$ROUTER" </dev/null >>"$LOG_FILE" 2>&1 &
    fi
    router_pid=$!
    printf '%s\n' "$router_pid" > "$PID_FILE"

    attempts=0
    while [ "$attempts" -lt 120 ]; do
        if probe_health; then
            release_lock
            trap - EXIT HUP INT TERM
            return 0
        fi
        # Immediately after the background launch, /proc/$pid/exe can still
        # point at setsid/nohup for one scheduler tick. Treat the child as
        # starting while the PID exists; probe_health performs the strict
        # executable-path check before accepting the service.
        if ! kill -0 "$router_pid" 2>/dev/null; then
            break
        fi
        attempts=$((attempts + 1))
        sleep 0.1
    done

    stop_router
    release_lock
    trap - EXIT HUP INT TERM
    printf '%s\n' "WSL K3 router did not become healthy. See $LOG_FILE" >&2
    return 1
}

case "$MODE" in
    health)
        probe_health
        exit $?
        ;;
    stop)
        stop_router
        exit 0
        ;;
    ensure)
        ensure_router
        exit $?
        ;;
esac

[ -n "$CREDENTIAL_TARGET" ] || usage_error
[ -f "$BROKER" ] || {
    printf '%s\n' "Credential broker is missing: $BROKER" >&2
    exit 4
}

ensure_router || exit 4
exec "$BROKER" --credential-target "$CREDENTIAL_TARGET"
