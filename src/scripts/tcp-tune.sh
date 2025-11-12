#!/bin/bash

echo "Applying TCP performance tuning..."

# Pacing + Congestion Control
sysctl -w net.core.default_qdisc=fq
modprobe tcp_bbr 2>/dev/null || true
sysctl -w net.ipv4.tcp_congestion_control=bbr

# Asymmetric write buffer to account for leader burst
sysctl -w net.ipv4.tcp_rmem="4096 131072 33554432"
sysctl -w net.ipv4.tcp_wmem="4096 131072 50331648"
sysctl -w net.core.rmem_default=524288
sysctl -w net.core.wmem_default=524288
sysctl -w net.core.rmem_max=50331648
sysctl -w net.core.wmem_max=50331648

# Network device queue processing settings
sysctl -w net.core.netdev_max_backlog=16384
sysctl -w net.core.netdev_budget=800
sysctl -w net.core.netdev_budget_usecs=3000
sysctl -w net.ipv4.tcp_notsent_lowat=524288

# Sane defaults
sysctl -w net.ipv4.tcp_sack=1
sysctl -w net.ipv4.tcp_timestamps=1
sysctl -w net.ipv4.tcp_window_scaling=1
sysctl -w net.ipv4.tcp_slow_start_after_idle=0
sysctl -w net.ipv4.tcp_mtu_probing=1

# Keep container running in daemon mode so pod is marked "Ready"
# The orchestrator waits for Ready status to check that the settings were applied,
# then deletes the DaemonSet
if [ "$1" = "--daemon" ]; then
    echo "TCP settings applied on node $(hostname)"
    sleep infinity
fi