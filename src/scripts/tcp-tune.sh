#!/bin/bash

# TCP Tuning Script for High-Performance Network
# Aggressive settings for maximum throughput (10-25 Gbps)

echo "Applying TCP performance tuning..."

# Use 256MB buffers for maximum throughput
# BDP for 10Gbps at 200ms = 10 * 0.2 / 8 = 250MB
# Using 256MB provides headroom for bursts and multiple connections
sysctl -w net.ipv4.tcp_rmem="4096 131072 268435456" || echo "Failed to set tcp_rmem"
sysctl -w net.ipv4.tcp_wmem="4096 65536 268435456" || echo "Failed to set tcp_wmem"
sysctl -w net.core.rmem_max=268435456 || echo "Failed to set rmem_max"
sysctl -w net.core.wmem_max=268435456 || echo "Failed to set wmem_max"
sysctl -w net.core.rmem_default=524288 || echo "Failed to set rmem_default"
sysctl -w net.core.wmem_default=524288 || echo "Failed to set wmem_default"
sysctl -w net.core.netdev_max_backlog=30000 || echo "Failed to set netdev_max_backlog"

# Use BBR congestion control for maximum throughput with latency
# BBR is much better than cubic for high-BDP networks
modprobe tcp_bbr 2>/dev/null
if sysctl -w net.ipv4.tcp_congestion_control=bbr 2>/dev/null; then
    echo "Using BBR congestion control"
else
    sysctl -w net.ipv4.tcp_congestion_control=cubic
    echo "BBR not available, using cubic"
fi

# Core TCP features - these should always be enabled
sysctl -w net.ipv4.tcp_timestamps=1 || echo "Failed to enable timestamps"
sysctl -w net.ipv4.tcp_sack=1 || echo "Failed to enable SACK"
sysctl -w net.ipv4.tcp_window_scaling=1 || echo "Failed to enable window scaling"

# Performance optimizations
sysctl -w net.ipv4.tcp_no_metrics_save=1 2>/dev/null || true
sysctl -w net.ipv4.tcp_moderate_rcvbuf=1 2>/dev/null || true
sysctl -w net.core.netdev_budget=1200 2>/dev/null || true

# Additional optimizations for 10Gbps+ networks
sysctl -w net.ipv4.tcp_mtu_probing=1 2>/dev/null || true
sysctl -w net.ipv4.tcp_slow_start_after_idle=0 2>/dev/null || true
sysctl -w net.ipv4.tcp_fin_timeout=10 2>/dev/null || true
sysctl -w net.ipv4.tcp_tw_reuse=1 2>/dev/null || true
sysctl -w net.ipv4.tcp_max_syn_backlog=8192 2>/dev/null || true

# Try to set netdev_budget_usecs if available (kernel 4.12+)
sysctl -w net.core.netdev_budget_usecs=5000 2>/dev/null || true

echo "TCP tuning applied successfully"

# Verify settings were applied correctly
echo "Verifying TCP settings on node $(hostname -f):"
echo "TCP receive buffer: $(sysctl -n net.ipv4.tcp_rmem)"
echo "TCP send buffer: $(sysctl -n net.ipv4.tcp_wmem)"
echo "Max receive: $(sysctl -n net.core.rmem_max)"
echo "Max send: $(sysctl -n net.core.wmem_max)"
echo "Congestion control: $(sysctl -n net.ipv4.tcp_congestion_control)"

# If running as a DaemonSet pod, keep container running
if [ "$1" = "--daemon" ]; then
    echo "Running in daemon mode, keeping container alive..."
    # Keep running for a bit to ensure settings propagate
    sleep 10
    echo "TCP settings verification complete on node $(hostname)"
    sleep infinity
fi