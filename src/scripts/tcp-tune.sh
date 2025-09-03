#!/bin/bash

# TCP Tuning Script for High-Performance Network
# Optimized for 10Gbps+ networks with artificial latency

echo "Applying TCP performance tuning..."

# BDP = 10 Gbps * 0.2s = 250MB, using 256MB to be safe
sysctl -w net.ipv4.tcp_rmem="4096 131072 268435456"
sysctl -w net.ipv4.tcp_wmem="4096 65536 268435456"
sysctl -w net.core.rmem_max=268435456
sysctl -w net.core.wmem_max=268435456
sysctl -w net.core.rmem_default=131072
sysctl -w net.core.wmem_default=131072
sysctl -w net.core.netdev_max_backlog=30000

# Use BBR congestion control for better performance with latency
# Fall back to cubic if BBR is not available
sysctl -w net.ipv4.tcp_congestion_control=bbr 2>/dev/null || sysctl -w net.ipv4.tcp_congestion_control=cubic

# Enable TCP optimizations
sysctl -w net.ipv4.tcp_timestamps=1
sysctl -w net.ipv4.tcp_sack=1
sysctl -w net.ipv4.tcp_window_scaling=1
sysctl -w net.ipv4.tcp_no_metrics_save=1
sysctl -w net.ipv4.tcp_moderate_rcvbuf=1
sysctl -w net.core.netdev_budget=600

echo "TCP tuning applied successfully"

# If running as a DaemonSet pod, keep container running
if [ "$1" = "--daemon" ]; then
    echo "Running in daemon mode, keeping container alive..."
    sleep infinity
fi