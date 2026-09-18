#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project_dir="$(cd -- "$script_dir/../.." && pwd)"
if [[ $# -ge 1 && -n "$1" ]]; then
  input_file="$1"
else
  printf '\nPrintCapturePOC interactive Linux test\n'
  printf 'Enter an absolute path, or drag a document into this terminal.\n\n'
  read -e -r -p 'Document path to print: ' input_file
fi

# Terminal drag-and-drop commonly surrounds paths containing spaces with quotes.
input_file="${input_file#\'}"
input_file="${input_file%\'}"
input_file="${input_file#\"}"
input_file="${input_file%\"}"
if [[ "$input_file" == "~" || "$input_file" == "~/"* ]]; then
  input_file="${input_file/#\~/$HOME}"
fi
input_file="$(readlink -m -- "$input_file")"
if [[ ! -r "$input_file" ]]; then
  printf 'Input file is not readable: %s\n' "$input_file" >&2
  exit 2
fi

if [[ $# -ge 2 ]]; then
  upload_url="$2"
else
  read -r -p 'Upload URL (press Enter to save locally only): ' upload_url
fi
if [[ -n "$upload_url" && ! "$upload_url" =~ ^https?:// ]]; then
  printf 'Upload URL must begin with http:// or https:// (or press Enter for local-only).\n' >&2
  exit 2
fi
if [[ $EUID -ne 0 ]]; then
  exec sudo --preserve-env=PATH "$0" "$input_file" "$upload_url"
fi

run_stamp="$(date +%Y%m%d_%H%M%S)"
run_user="${SUDO_USER:-$(id -un)}"
run_group="$(id -gn "$run_user")"
config_root="/opt/printcapture-poc/run_$run_stamp"
sandbox="/var/spool/cups/printcapture-poc/run_$run_stamp"
server_port=8631
capture_output="$project_dir/captured_jobs_linux/sandbox_run_$run_stamp"

mkdir -p "$sandbox"/{cache,run,spool,state,tmp,logs,printer-output}
mkdir -p "$config_root/serverbin/backend"
cp "$script_dir/capture-backend" "$config_root/serverbin/backend/capture"
chmod 755 "$config_root/serverbin/backend/capture"
for component in daemon driver filter notifier; do
  ln -sfn "/usr/lib/cups/$component" "$config_root/serverbin/$component"
done
chown -R "$run_user:$run_group" \
  "$sandbox/cache" "$sandbox/run" "$sandbox/spool" "$sandbox/state" \
  "$sandbox/tmp" "$sandbox/logs" "$sandbox/printer-output" \
  "$project_dir/captured_jobs_linux"
chown -R root:root "$config_root"
chmod 755 "$config_root" "$config_root/serverbin" "$config_root/serverbin/backend"

cat >"$config_root/cups-files.conf" <<EOF
User $run_user
Group $run_group
# CUPS rejects using the same group for child processes and @SYSTEM admins.
SystemGroup root
ServerBin $config_root/serverbin
ServerRoot $config_root
DataDir /usr/share/cups
DocumentRoot /usr/share/cups/doc-root
RequestRoot $sandbox/spool
CacheDir $sandbox/cache
StateDir $sandbox/state
TempDir $sandbox/tmp
AccessLog $sandbox/logs/access_log
ErrorLog stderr
PageLog $sandbox/logs/page_log
EOF

cat >"$config_root/cupsd.conf" <<EOF
LogLevel warn
Listen 127.0.0.1:$server_port
Browsing Off
DefaultAuthType Basic
WebInterface No
PreserveJobFiles Yes
PreserveJobHistory Yes

<Location />
  Order allow,deny
  Allow localhost
</Location>
<Location /admin>
  Order allow,deny
  Allow localhost
</Location>
<Location /admin/conf>
  AuthType None
  Order allow,deny
  Allow localhost
</Location>

<Policy default>
  JobPrivateAccess all
  JobPrivateValues none
  SubscriptionPrivateAccess all
  SubscriptionPrivateValues none
  <Limit All>
    Order allow,deny
    Allow localhost
  </Limit>
</Policy>
EOF

# CUPS refuses insecure service/configuration paths. Keep executable/configuration
# material root-owned while granting the configured service user only state paths.
chown root:root "$config_root" "$config_root/cupsd.conf" "$config_root/cups-files.conf"
chmod 755 "$sandbox"
chmod 644 "$config_root/cupsd.conf" "$config_root/cups-files.conf"

cleanup() {
  set +e
  if [[ -n "${monitor_pid:-}" ]]; then kill "$monitor_pid" 2>/dev/null; fi
  if [[ -n "${cups_pid:-}" ]]; then kill "$cups_pid" 2>/dev/null; fi
  wait 2>/dev/null
  case "$sandbox" in
    /var/spool/cups/printcapture-poc/run_*) rm -rf -- "$sandbox" ;;
  esac
  case "$config_root" in
    /opt/printcapture-poc/run_*) rm -rf -- "$config_root" ;;
  esac
}
trap cleanup EXIT INT TERM

if ! cupsd -t -c "$config_root/cupsd.conf" -s "$config_root/cups-files.conf" \
     2>"$sandbox/logs/cupsd-validation.log"; then
  printf '\nPrivate CUPS configuration validation failed:\n' >&2
  sed -n '1,160p' "$sandbox/logs/cupsd-validation.log" >&2 || true
  exit 1
fi

cupsd -f -c "$config_root/cupsd.conf" -s "$config_root/cups-files.conf" \
  2>"$sandbox/logs/cupsd-startup.log" &
cups_pid=$!

for _ in {1..50}; do
  if ! kill -0 "$cups_pid" 2>/dev/null; then break; fi
  if CUPS_SERVER="127.0.0.1:$server_port" lpstat -r 2>/dev/null | grep -q 'scheduler is running'; then
    break
  fi
  sleep 0.1
done
if ! kill -0 "$cups_pid" 2>/dev/null ||
   ! CUPS_SERVER="127.0.0.1:$server_port" lpstat -r 2>/dev/null | grep -q 'scheduler is running'; then
  scheduler_status="still running but unreachable"
  if ! kill -0 "$cups_pid" 2>/dev/null; then
    set +e
    wait "$cups_pid" 2>/dev/null
    scheduler_status="$?"
    set -e
    cups_pid=""
  fi
  printf '\nPrivate CUPS scheduler failed to start. Diagnostic output:\n' >&2
  sed -n '1,160p' "$sandbox/logs/cupsd-startup.log" >&2 || true
  printf 'cupsd exit status: %s\n' "$scheduler_status" >&2
  exit 1
fi

CUPS_SERVER="127.0.0.1:$server_port" lpadmin \
  -p CaptureTest -E -v "capture:$sandbox/printer-output" -m raw

monitor_arguments=(
  --server "127.0.0.1:$server_port"
  --spool-directory "$sandbox/spool"
  --output "$capture_output"
  --poll-seconds 0.05
)
if [[ -n "$upload_url" ]]; then
  monitor_arguments+=(--upload-url "$upload_url")
fi

printf '\nPrinting: %s\n' "$input_file"
printf 'Local capture destination: %s\n\n' "$capture_output"

python3 "$project_dir/linux/monitor_cups.py" \
  "${monitor_arguments[@]}" &
monitor_pid=$!
sleep 0.4

if [[ "$run_user" == "root" ]]; then
  job_result="$(CUPS_SERVER="127.0.0.1:$server_port" lp -d CaptureTest -n 2 "$input_file")"
else
  job_result="$(runuser -u "$run_user" -- env CUPS_SERVER="127.0.0.1:$server_port" \
    lp -d CaptureTest -n 2 "$input_file")"
fi
printf '%s\n' "$job_result"

for _ in {1..100}; do
  if find "$capture_output" -mindepth 2 -name spool_document_001.bin -print -quit | grep -q . &&
     find "$sandbox/printer-output" -mindepth 1 -name 'printed_job_*' -print -quit | grep -q .; then
    sleep 1
    printf '\nCapture created:\n'
    find "$capture_output" -mindepth 1 -maxdepth 2 -type f -printf '  %p (%s bytes)\n' | sort
    chown -R "$run_user:$run_group" "$capture_output"
    printf '\nSandbox test passed. The fake printer received the job normally.\n'
    exit 0
  fi
  sleep 0.1
done

printf 'The job did not complete both capture and fake-printer delivery.\n' >&2
printf 'Diagnostics from %s/logs/cupsd-startup.log:\n' "$sandbox" >&2
sed -n '1,200p' "$sandbox/logs/cupsd-startup.log" >&2 || true
exit 1
