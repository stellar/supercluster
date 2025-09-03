#!/bin/bash

# TCP Reset Script - Restores Default Linux TCP Settings

echo "Resetting TCP settings to defaults..."

# Reset Linux TCP buffer sizes
sysctl -w net.ipv4.tcp_rmem="4096 131072 6291456"
sysctl -w net.ipv4.tcp_wmem="4096 16384 4194304"

# Reset socket buffer
sysctl -w net.core.rmem_default=212992
sysctl -w net.core.wmem_default=212992
sysctl -w net.core.rmem_max=212992
sysctl -w net.core.wmem_max=212992
sysctl -w net.core.netdev_max_backlog=1000

# Reset to default congestion control (usually cubic)
sysctl -w net.ipv4.tcp_congestion_control=cubic

# Various settings
sysctl -w net.ipv4.tcp_timestamps=1
sysctl -w net.ipv4.tcp_sack=1
sysctl -w net.ipv4.tcp_window_scaling=1
sysctl -w net.ipv4.tcp_no_metrics_save=0
sysctl -w net.ipv4.tcp_moderate_rcvbuf=1
sysctl -w net.core.netdev_budget=300

echo "TCP settings reset to defaults"

# If running as a DaemonSet pod, exit after a brief wait
if [ "$1" = "--daemon" ]; then
    echo "Reset complete, exiting in 10 seconds..."
    sleep 10
fi