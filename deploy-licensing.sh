#!/usr/bin/env bash
#
# deploy-licensing.sh - deploy & maintain the self-hosted licensing stack on Linux.
# Bash equivalent of deploy-licensing.ps1 (Ubuntu/Debian).
#
# Commands:
#   check   - verify Docker/Compose, .env presence, and required secrets
#   up      - build & start the stack, then poll /healthz until healthy
#   backup  - export the lic-data volume as a timestamped .tgz (with retention)
#   logs    - follow live container logs
#   down    - stop the stack (volumes are kept)
#
# Examples:
#   ./deploy-licensing.sh check
#   ./deploy-licensing.sh up
#   ./deploy-licensing.sh backup --keep 14
#   ./deploy-licensing.sh logs
#
# Cron (nightly 03:00):
#   0 3 * * * /opt/licensing/deploy-licensing.sh backup --keep 14 >> /opt/licensing/backups/backup.log 2>&1

set -euo pipefail

KEEP=14
HEALTH_TIMEOUT=90
HEALTHZ_URL="${HEALTHZ_URL:-http://127.0.0.1:8000/healthz}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

ENV_FILE="$SCRIPT_DIR/.env"
BACKUP_DIR="$SCRIPT_DIR/backups"

if [[ -t 1 ]]; then
    C_RED='\033[31m'; C_YELLOW='\033[33m'; C_GREEN='\033[32m'; C_CYAN='\033[36m'; C_RESET='\033[0m'
else
    C_RED=''; C_YELLOW=''; C_GREEN=''; C_CYAN=''; C_RESET=''
fi

step() { printf "${C_CYAN}==> %s${C_RESET}\n" "$*"; }
ok()   { printf "${C_GREEN}%s${C_RESET}\n" "$*"; }
warn() { printf "${C_YELLOW}WARNING: %s${C_RESET}\n" "$*" >&2; }
die()  { printf "${C_RED}ERROR: %s${C_RESET}\n" "$*" >&2; exit 1; }

cmd_exists() { command -v "$1" >/dev/null 2>&1; }

# Prints 'compose' for `docker compose`, 'legacy' for `docker-compose`, or fails.
compose_prefix() {
    if cmd_exists docker && docker compose version >/dev/null 2>&1; then
        echo compose
    elif cmd_exists docker-compose && docker-compose --version >/dev/null 2>&1; then
        echo legacy
    else
        return 1
    fi
}

# Runs an arbitrary compose subcommand through the detected binary.
compose() {
    local prefix
    prefix="$(compose_prefix)" || die 'Docker Compose is not available.'
    if [[ "$prefix" == compose ]]; then
        docker compose "$@"
    else
        docker-compose "$@"
    fi
}

# Reads KEY from .env; strips surrounding quotes and CR (compose-compatible).
get_env() {
    sed -n "s/^[[:space:]]*$1[[:space:]]*=[[:space:]]*//p" "$ENV_FILE" \
        | tail -n 1 | tr -d '\r' \
        | sed -e 's/^"//' -e 's/"$//' -e "s/^'//" -e "s/'$//"
}

require_env() {
    local missing=() var
    for var in LIC_ADMIN_KEY LIC_HMAC_SECRET; do
        if [[ -z "$(get_env "$var")" ]]; then
            missing+=("$var")
        fi
    done
    if ((${#missing[@]})); then
        die "Required environment variable(s) not set in .env: ${missing[*]}"
    fi
}

check_weak_secrets() {
    local var value
    for var in LIC_ADMIN_KEY LIC_HMAC_SECRET; do
        value="$(get_env "$var")"
        if ((${#value} < 16)) || [[ "$value" =~ change-me ]]; then
            warn "$var looks weak (shorter than 16 chars or placeholder) - use a long random value."
        fi
    done
}

cmd_check() {
    step 'Checking prerequisites...'

    if ! cmd_exists docker; then
        die 'Docker is not installed or not on PATH.'
    fi
    docker --version || die 'Docker CLI failed to run (is the daemon/docker engine installed?).'

    if compose_prefix >/dev/null; then
        ok 'Docker Compose: OK'
    else
        die 'Docker Compose is not available (neither "docker compose" nor "docker-compose").'
    fi

    if [[ ! -f "$ENV_FILE" ]]; then
        die "'.env' not found in $SCRIPT_DIR (create it from compose.yml requirements)."
    fi
    require_env
    check_weak_secrets
    ok 'All prerequisites OK.'
}

healthz_ok() {
    curl -sf --max-time 3 "$HEALTHZ_URL" 2>/dev/null | grep -q '"status"[[:space:]]*:[[:space:]]*"ok"'
}

wait_healthz() {
    local deadline attempt
    deadline=$(( $(date +%s) + HEALTH_TIMEOUT ))
    attempt=0
    while (( $(date +%s) < deadline )); do
        attempt=$((attempt + 1))
        if healthz_ok; then
            ok "Healthz OK (attempt $attempt)."
            return 0
        fi
        if ((attempt == 1)); then
            step "Waiting for /healthz (timeout ${HEALTH_TIMEOUT}s)..."
        fi
        sleep 2
    done
    return 1
}

cmd_up() {
    if ! cmd_exists curl; then
        die 'curl is required for the /healthz check (install with: apt install curl).'
    fi
    step 'Building and starting the stack...'
    compose up -d --build

    if ! wait_healthz; then
        echo '--- last 25 log lines (diagnostics) ---'
        compose logs --tail 25 license || true
        die "/healthz did not report OK within ${HEALTH_TIMEOUT}s."
    fi
    compose ps
}

cmd_backup() {
    mkdir -p "$BACKUP_DIR"

    local stamp fname local_file size old
    stamp="$(date +%Y%m%d-%H%M%S)"
    fname="lic-data-$stamp.tgz"
    local_file="$BACKUP_DIR/$fname"
    step "Exporting volume 'lic-data' -> $local_file"

    docker run --rm \
        -v lic-data:/data \
        -v "$BACKUP_DIR:/backup" \
        alpine tar czf "/backup/$fname" -C /data .

    size="$(du -h "$local_file" | cut -f1)"
    ok "Backup complete: $fname ($size)"

    old="$(ls -1t "$BACKUP_DIR"/lic-data-*.tgz 2>/dev/null | tail -n +$((KEEP + 1)))"
    if [[ -n "$old" ]]; then
        step "Pruning $(wc -l <<<"$old") backup(s) older than the newest $KEEP..."
        while IFS= read -r f; do
            rm -f -- "$f"
        done <<<"$old"
    fi
}

cmd_logs() {
    compose logs -f
}

cmd_down() {
    step 'Stopping the stack (volumes and data are kept)...'
    compose down
}

usage() {
    cat <<'EOF'
Usage: deploy-licensing.sh <check|up|backup|logs|down> [options]

Commands:
  check    verify Docker/Compose, .env presence, and required secrets
  up       build & start the stack, then poll /healthz until healthy
  backup   export the lic-data volume as a timestamped .tgz (with retention)
  logs     follow live container logs
  down     stop the stack (volumes are kept)

Options:
  --keep <n>     retain newest n backup archives (default 14, backup only)
  --timeout <n>  /healthz wait timeout in seconds (default 90, up only)

Cron (nightly 03:00):
  0 3 * * * /opt/licensing/deploy-licensing.sh backup --keep 14 >> /opt/licensing/backups/backup.log 2>&1
EOF
}

if (($# < 1)); then
    usage
    exit 0
fi

CMD="$1"
shift
while (($#)); do
    case "$1" in
        --keep)
            (($# >= 2)) || die '--keep requires a value.'
            KEEP="$2"; shift 2 ;;
        --timeout)
            (($# >= 2)) || die '--timeout requires a value.'
            HEALTH_TIMEOUT="$2"; shift 2 ;;
        -h|--help)
            usage; exit 0 ;;
        *)
            die "Unknown option: $1" ;;
    esac
done

[[ "$KEEP" =~ ^[0-9]+$ ]] && ((KEEP >= 1)) || die '--keep must be a positive integer.'
[[ "$HEALTH_TIMEOUT" =~ ^[0-9]+$ ]] && ((HEALTH_TIMEOUT >= 1)) || die '--timeout must be a positive integer.'

case "$CMD" in
    check)  cmd_check ;;
    up)     cmd_up ;;
    backup) cmd_backup ;;
    logs)   cmd_logs ;;
    down)   cmd_down ;;
    *)      die "Unknown command: $CMD (use check|up|backup|logs|down)" ;;
esac