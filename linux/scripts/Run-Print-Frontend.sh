#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project_dir="$(cd -- "$script_dir/../.." && pwd)"
queue_name="HP_Color_LaserJet_Pro_M478f_9f_C277D2"

if ! lpstat -r 2>/dev/null | grep -q 'scheduler is running'; then
  printf 'CUPS is not running. Run: sudo systemctl enable --now cups\n' >&2
  exit 1
fi
if ! lpstat -p "$queue_name" >/dev/null 2>&1; then
  printf 'The printer queue is missing. Run: ./linux/scripts/Setup-Physical-Printer.sh\n' >&2
  exit 1
fi
python3 - <<'PY'
try:
    import cups, fastapi, multipart, uvicorn
except ImportError as exc:
    raise SystemExit(f"Missing dependency {exc}. Install: sudo apt install python3-cups python3-fastapi python3-multipart python3-uvicorn")
PY

exec python3 "$project_dir/linux/frontend/app.py"
