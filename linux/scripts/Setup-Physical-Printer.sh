#!/usr/bin/env bash
set -euo pipefail

printer_ip="192.168.8.43"
queue_name="HP_Color_LaserJet_Pro_M478f_9f_C277D2"
printer_uri="ipp://$printer_ip/ipp/print"

printf 'Checking HP printer at %s…\n' "$printer_ip"
if ! ping -c 1 -W 2 "$printer_ip" >/dev/null; then
  printf 'Printer is not reachable at %s. Check Wi-Fi/network connectivity.\n' "$printer_ip" >&2
  exit 1
fi

sudo systemctl enable --now cups
if lpstat -p "$queue_name" >/dev/null 2>&1; then
  printf 'Printer queue already exists: %s\n' "$queue_name"
else
  sudo lpadmin -p "$queue_name" -E -v "$printer_uri" -m everywhere
  printf 'Printer queue installed: %s\n' "$queue_name"
fi

sudo cupsenable "$queue_name"
sudo cupsaccept "$queue_name"
lpoptions -d "$queue_name"
lpstat -p "$queue_name" -l
printf 'Printer setup complete.\n'
