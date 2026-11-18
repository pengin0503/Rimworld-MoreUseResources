#!/usr/bin/env bash
# Prepares an Ubuntu 24.04 VM (Google Compute Engine or any other) for the MoreUseResources
# Linux benchmark: .NET 10 SDK, the tools the run script uses, and optional kernel accounting.
#
#   tools/benchmark/setup-ubuntu.sh                 install what is missing
#   tools/benchmark/setup-ubuntu.sh --delayacct     also enable per-thread block I/O delay accounting
#                                                   (kernel.task_delayacct=1, until the next reboot)
#   tools/benchmark/setup-ubuntu.sh --check         report only, change nothing
#
# Needs sudo for apt-get and sysctl. Safe to run again.
set -euo pipefail

check_only=0
delayacct=0
for arg in "$@"; do
  case "$arg" in
    --check) check_only=1 ;;
    --delayacct) delayacct=1 ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

say() { printf '%s\n' "$*"; }
sudo_run() { if [ "$(id -u)" -eq 0 ]; then "$@"; else sudo "$@"; fi; }

if [ -r /etc/os-release ]; then
  . /etc/os-release
  say "OS: ${PRETTY_NAME:-unknown}"
  if [ "${ID:-}" != "ubuntu" ] || [ "${VERSION_ID:-}" != "24.04" ]; then
    say "warning: written for Ubuntu 24.04; other systems may need different package names"
  fi
fi
say "kernel: $(uname -r), vCPU: $(nproc), memory: $(awk '/MemTotal/ {printf "%.1f GiB", $2/1048576}' /proc/meminfo)"

have_sdk10() { command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; }

missing=()
have_sdk10 || missing+=(dotnet-sdk-10.0)
command -v python3 >/dev/null 2>&1 || missing+=(python3)
python3 -c 'import boto3' >/dev/null 2>&1 || missing+=(python3-boto3)
command -v taskset >/dev/null 2>&1 || missing+=(util-linux)
command -v timeout >/dev/null 2>&1 || missing+=(coreutils)
command -v git >/dev/null 2>&1 || missing+=(git)

if [ "${#missing[@]}" -eq 0 ]; then
  say "packages: all present"
elif [ "$check_only" -eq 1 ]; then
  say "packages missing: ${missing[*]}"
else
  say "installing: ${missing[*]}"
  sudo_run apt-get update -q
  sudo_run env DEBIAN_FRONTEND=noninteractive apt-get install -y -q "${missing[@]}"
fi

if have_sdk10; then
  say ".NET SDK: $(dotnet --list-sdks | grep '^10\.' | tr '\n' ' ')"
else
  say "error: no .NET 10 SDK found after installation" >&2
  [ "$check_only" -eq 1 ] || exit 1
fi

# What the benchmark can measure on this kernel.
[ -r /proc/pressure/io ] && say "PSI (pressure stall information): available" || say "PSI: not available (io/memory pressure columns stay empty)"
if [ -r /proc/sys/kernel/task_delayacct ]; then
  current=$(cat /proc/sys/kernel/task_delayacct)
  if [ "$current" = "1" ]; then
    say "per-thread I/O delay accounting: on"
  elif [ "$delayacct" -eq 1 ] && [ "$check_only" -eq 0 ]; then
    sudo_run sysctl -q -w kernel.task_delayacct=1
    say "per-thread I/O delay accounting: switched on until reboot"
  else
    say "per-thread I/O delay accounting: off (rerun with --delayacct to record io_wait_ms per thread)"
  fi
fi
say "transparent huge pages: $(cat /sys/kernel/mm/transparent_hugepage/enabled 2>/dev/null || echo unknown)"
say "CPU frequency governor: $(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor 2>/dev/null || echo 'not exposed (normal on GCE)')"
say "ready. Next: tools/benchmark/run-gcp-benchmark.sh smoke"
