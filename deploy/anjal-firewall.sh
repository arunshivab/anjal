#!/usr/bin/env bash
# Anjal outbound firewall and port-ownership check (ANJAL-INC-01, preventive action P-1).
#
#   A. Mail out (TCP 25, 465, 587): the anjal account only - root and manual tests are refused.
#   B. Anjal's own ports are held only by Anjal (a check every 5 minutes).
#   C. All other outbound traffic: named accounts and purposes only.
#
# Only OUTGOING traffic is filtered, in its own nftables table (inet anjal_egress).
# Incoming traffic stays with E2E's security group and fail2ban's own table.
#
# Usage (as root):
#   anjal-firewall.sh generate   write /etc/anjal/anjal-egress.nft, check it, install the units (nothing active yet)
#   anjal-firewall.sh apply      load the rules now; they REMOVE THEMSELVES after 3 minutes unless confirmed
#   anjal-firewall.sh confirm    keep them: cancel the removal, load at every boot, start the port check
#   anjal-firewall.sh remove     unload the rules and stop loading them at boot
#   anjal-firewall.sh status     show the table, the units and recent refusals
set -euo pipefail

RULES=/etc/anjal/anjal-egress.nft
ADMIN="${ANJAL_ADMIN_USER:-${SUDO_USER:-arun}}"
UNIT_DIR=/etc/systemd/system

die() { echo "anjal-firewall: $*" >&2; exit 1; }
[ "$(id -u)" -eq 0 ] || die "run as root (sudo)"
command -v nft >/dev/null || die "nft not found (apt install nftables)"

uid_of() { id -u "$1" 2>/dev/null || true; }

generate() {
    local anjal apt resolve timesync zabbix admin
    anjal=$(uid_of anjal); apt=$(uid_of _apt); resolve=$(uid_of systemd-resolve)
    timesync=$(uid_of systemd-timesync); zabbix=$(uid_of zabbix); admin=$(uid_of "$ADMIN")
    [ -n "$anjal" ] || die "the anjal account does not exist - install Anjal first"
    echo "anjal-firewall: accounts: anjal=$anjal root=0 _apt=${apt:-absent} systemd-resolve=${resolve:-absent} systemd-timesync=${timesync:-absent} zabbix=${zabbix:-absent} $ADMIN=${admin:-absent}"

    local tmp; tmp=$(mktemp)
    {
    echo "#!/usr/sbin/nft -f"
    echo "# Written by anjal-firewall.sh generate on $(date -u +%Y-%m-%dT%H:%M:%SZ). Do not edit; regenerate."
    echo "# Anjal outbound firewall (ANJAL-INC-01 P-1). Reloading replaces only this table."
    echo "table inet anjal_egress"
    echo "delete table inet anjal_egress"
    echo ""
    echo "table inet anjal_egress {"
    echo "    set smtp_ports { type inet_service; elements = { 25, 465, 587 } }"
    echo ""
    echo "    chain output {"
    echo "        type filter hook output priority filter; policy accept;"
    echo ""
    echo "        # Local traffic, replies to incoming connections, and ICMP (ping, path MTU)."
    echo "        oif \"lo\" accept"
    echo "        ct state established,related accept"
    echo "        meta l4proto { icmp, ipv6-icmp } accept"
    echo ""
    echo "        # A. Mail out: the anjal account ($anjal) only. Root and manual tests are refused."
    echo "        tcp dport @smtp_ports meta skuid $anjal accept"
    echo "        tcp dport @smtp_ports limit rate 30/minute burst 60 packets log prefix \"anjal-egress SMTP-DENY \" level warn"
    echo "        tcp dport @smtp_ports reject with tcp reset"
    echo ""
    echo "        # Replies from this server's own services - SSH, mail, web, HTTP/3 - always go out,"
    echo "        # even on connections that were already open when these rules loaded (the kernel's"
    echo "        # connection tracker sees their next packet as new, not established). Only root and"
    echo "        # Anjal can use these ports. After rule A, so it cannot be used to reach mail ports."
    echo "        tcp sport { 22, 25, 80, 443, 465, 587 } accept"
    echo "        udp sport 443 accept"
    echo ""
    echo "        # C. Everything else: named accounts and purposes only. Every account that may go"
    echo "        #    out also gets DNS, because resolv.conf may point straight at outside servers."
    echo "        # anjal: DNS (reads resolv.conf itself), HTTP for certificate revocation, HTTPS for ACME and backups, UDP 443."
    echo "        meta skuid $anjal meta l4proto { tcp, udp } th dport { 53, 80, 443 } accept"
    [ -n "$resolve" ] && echo "        meta skuid $resolve meta l4proto { tcp, udp } th dport { 53, 853 } accept   # systemd-resolve"
    echo "        meta skuid 0 meta l4proto { tcp, udp } th dport 53 accept                        # root: DNS"
    echo "        meta skuid 0 tcp dport { 80, 443 } accept                                        # root: updates, fwupd"
    [ -n "$apt" ] && echo "        meta skuid $apt meta l4proto { tcp, udp } th dport 53 accept                      # _apt: DNS for downloads"
    [ -n "$apt" ] && echo "        meta skuid $apt tcp dport { 80, 443 } accept                                     # _apt: package downloads"
    echo "        meta skuid 0 udp dport 123 accept                                                # root: time"
    [ -n "$timesync" ] && echo "        meta skuid $timesync meta l4proto { tcp, udp } th dport 53 accept                 # systemd-timesync: DNS for time servers"
    [ -n "$timesync" ] && echo "        meta skuid $timesync udp dport 123 accept                                        # systemd-timesync"
    [ -n "$zabbix" ] && echo "        meta skuid $zabbix meta l4proto { tcp, udp } th dport 53 accept                   # zabbix: DNS for E2E's server name"
    [ -n "$zabbix" ] && echo "        meta skuid $zabbix tcp dport 10051 accept                                      # zabbix agent (active checks)"
    if [ -n "$admin" ]; then
        echo "        meta skuid $admin meta l4proto { tcp, udp } th dport 53 accept                     # $ADMIN: DNS"
        echo "        meta skuid $admin tcp dport { 22, 80, 443 } accept                                # $ADMIN: ssh, downloads"
    fi
    echo ""
    echo "        # Anything else is refused and logged (rate-limited)."
    echo "        limit rate 30/minute burst 60 packets log prefix \"anjal-egress DENY \" level warn"
    echo "        reject"
    echo "    }"
    echo "}"
    } > "$tmp"
    nft -c -f "$tmp" || { rm -f "$tmp"; die "rule check failed - nothing installed"; }
    install -d -m 755 /etc/anjal
    install -m 644 "$tmp" "$RULES"; rm -f "$tmp"
    echo "anjal-firewall: rules written and checked: $RULES"

    cat > "$UNIT_DIR/anjal-egress.service" <<UNIT
[Unit]
Description=Anjal outbound firewall (only Anjal sends mail; allowlist for everything else)
After=nftables.service
Before=anjal-server.service anjal-webmail.service

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/usr/sbin/nft -f $RULES
ExecStop=/usr/sbin/nft delete table inet anjal_egress

[Install]
WantedBy=multi-user.target
UNIT
    install -m 755 "$(dirname "$(readlink -f "$0")")/anjal-portcheck.sh" /usr/local/sbin/anjal-portcheck
    cat > "$UNIT_DIR/anjal-portcheck.service" <<UNIT
[Unit]
Description=Anjal port-ownership check (only Anjal holds its ports)

[Service]
Type=oneshot
ExecStart=/usr/local/sbin/anjal-portcheck
UNIT
    cat > "$UNIT_DIR/anjal-portcheck.timer" <<UNIT
[Unit]
Description=Run the Anjal port-ownership check every 5 minutes

[Timer]
OnBootSec=3min
OnUnitActiveSec=5min

[Install]
WantedBy=timers.target
UNIT
    systemctl daemon-reload
    echo "anjal-firewall: units installed (not active). Next: anjal-firewall.sh apply"
}

apply() {
    [ -f "$RULES" ] || die "no rules yet - run: anjal-firewall.sh generate"
    nft -f "$RULES"
    systemctl stop anjal-egress-undo.timer 2>/dev/null || true
    systemd-run --quiet --unit=anjal-egress-undo --on-active=180 --timer-property=AccuracySec=1s /usr/sbin/nft delete table inet anjal_egress
    echo "anjal-firewall: rules ACTIVE. They remove themselves in 3 minutes unless you run: anjal-firewall.sh confirm"
    echo "anjal-firewall: BEFORE confirming, open a NEW ssh session and load the webmail - both must work."
}

confirm() {
    nft list table inet anjal_egress >/dev/null 2>&1 || die "the rules are not loaded (did the 3 minutes pass?) - run apply again"
    systemctl stop anjal-egress-undo.timer 2>/dev/null || true
    systemctl enable --quiet anjal-egress.service
    systemctl start anjal-egress.service
    systemctl enable --quiet --now anjal-portcheck.timer
    echo "anjal-firewall: confirmed - loaded at every boot; port check every 5 minutes"
}

remove() {
    systemctl stop anjal-egress-undo.timer 2>/dev/null || true
    systemctl disable --quiet --now anjal-portcheck.timer 2>/dev/null || true
    systemctl disable --quiet anjal-egress.service 2>/dev/null || true
    nft delete table inet anjal_egress 2>/dev/null || true
    echo "anjal-firewall: removed - outbound traffic is unrestricted again"
}

status() {
    nft list table inet anjal_egress 2>/dev/null || echo "(table inet anjal_egress not loaded)"
    systemctl is-enabled anjal-egress.service anjal-portcheck.timer 2>/dev/null || true
    systemctl list-timers anjal-egress-undo.timer --no-pager 2>/dev/null | head -3 || true
    echo "== refusals in the last hour"
    journalctl -k --since "-1h" --no-pager 2>/dev/null | grep "anjal-egress" | tail -10 || true
}

case "${1:-}" in
    generate) generate ;;
    apply) apply ;;
    confirm) confirm ;;
    remove) remove ;;
    status) status ;;
    *) sed -n '2,17p' "$0"; exit 2 ;;
esac
