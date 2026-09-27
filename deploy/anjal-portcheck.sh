#!/usr/bin/env bash
# Anjal port-ownership check (ANJAL-INC-01, preventive action P-1 B).
# Anjal's ports - TCP 25, 80, 443, 465, 587 and UDP 443 - must be held only by
# processes of the anjal account. Anything else is an ALERT: logged at priority
# "crit" and the check exits 1, so the unit shows as failed.
set -uo pipefail
# A check that cannot look must not report "ok".
if ! command -v ss >/dev/null 2>&1; then
    logger -p auth.crit -t anjal-portcheck "ALERT: cannot check Anjal's ports - the ss command is missing (package iproute2)" 2>/dev/null || true
    echo "anjal-portcheck: ALERT: cannot check - the ss command is missing (package iproute2)" >&2
    exit 2
fi
alerts=0
check() {
    local proto=$1 port=$2 lines pid owner comm
    lines=$(ss -H "-${proto}lnp" "sport = :$port" 2>/dev/null)
    if [ -z "$lines" ]; then
        echo "anjal-portcheck: note: nothing listening on ${proto} ${port}"
        return
    fi
    while read -r pid; do
        [ -n "$pid" ] || continue
        owner=$(ps -o user= -p "$pid" 2>/dev/null | tr -d ' ')
        comm=$(ps -o comm= -p "$pid" 2>/dev/null | tr -d ' ')
        if [ "$owner" != "anjal" ]; then
            logger -p auth.crit -t anjal-portcheck "ALERT: ${proto} port ${port} is held by ${comm:-?} (pid ${pid}, user ${owner:-?}), not by Anjal"
            echo "anjal-portcheck: ALERT: ${proto} port ${port} held by ${comm:-?} (pid ${pid}, user ${owner:-?})"
            alerts=$((alerts + 1))
        fi
    done < <(grep -o 'pid=[0-9]*' <<<"$lines" | cut -d= -f2 | sort -u)
}
for p in 25 80 443 465 587; do check t "$p"; done
check u 443
if [ "$alerts" -eq 0 ]; then echo "anjal-portcheck: ok - Anjal's ports are held only by Anjal"; fi
[ "$alerts" -eq 0 ]
