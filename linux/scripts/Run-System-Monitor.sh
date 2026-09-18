#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project_dir="$(cd -- "$script_dir/../.." && pwd)"

exec sudo python3 "$project_dir/linux/monitor_cups.py" \
  --output "$project_dir/captured_jobs_linux" \
  "$@"
