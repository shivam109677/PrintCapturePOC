#!/usr/bin/env python3
"""Passive Linux/CUPS print job metadata and spool-file capture POC."""

from __future__ import annotations

import argparse
import hashlib
import json
import mimetypes
import os
from pathlib import Path
import re
import shutil
import socket
import sys
import time
from typing import Any
from urllib import request
import uuid

try:
    import cups
except ImportError:
    print("python3-cups is required: sudo apt install python3-cups", file=sys.stderr)
    raise SystemExit(2)


STATE_NAMES = {
    3: "Pending",
    4: "Held",
    5: "Processing",
    6: "Stopped",
    7: "Canceled",
    8: "Aborted",
    9: "Completed",
}
TERMINAL_STATES = {7, 8, 9}
SPOOL_PATTERN = re.compile(r"^(?P<kind>[cd])(?P<job>\d+)(?:-(?P<document>\d+))?$", re.I)
JOB_ATTRIBUTES = [
    "job-id",
    "job-name",
    "job-originating-user-name",
    "job-originating-host-name",
    "job-printer-uri",
    "job-state",
    "job-state-reasons",
    "copies",
    "job-copies",
    "job-media-sheets",
    "job-media-sheets-completed",
    "document-format-detected",
    "document-format-supplied",
    "job-k-octets",
    "time-at-creation",
]


class CaptureStore:
    def __init__(self, root: Path):
        self.root = root.resolve()
        self.root.mkdir(parents=True, exist_ok=True)
        self.records: dict[int, dict[str, Any]] = {}

    def get(self, job_id: int) -> dict[str, Any]:
        if job_id not in self.records:
            stamp = time.strftime("%Y%m%d_%H%M%S") + f"_{int(time.time_ns() / 1_000_000) % 1000:03d}"
            directory = self.root / f"job_{job_id:05d}_{stamp}"
            directory.mkdir(parents=True, exist_ok=True)
            self.records[job_id] = {
                "directory": directory,
                "metadata": {
                    "schemaVersion": 1,
                    "platform": "linux-cups",
                    "jobId": job_id,
                    "firstObservedAt": iso_now(),
                    "lastObservedAt": iso_now(),
                    "capturedFiles": [],
                    "captureWarnings": [],
                    "upload": {"enabled": False, "attempted": False, "succeeded": False},
                },
            }
            self.persist(job_id)
        return self.records[job_id]

    def update_job(self, job_id: int, attributes: dict[str, Any]) -> bool:
        record = self.get(job_id)
        metadata = record["metadata"]
        first_detection = "documentName" not in metadata
        printer_uri = attributes.get("job-printer-uri", "")
        submitted = attributes.get("time-at-creation")
        state = int(attributes.get("job-state", 0) or 0)
        metadata.update(
            {
                "documentName": attributes.get("job-name"),
                "userName": attributes.get("job-originating-user-name"),
                "computerName": attributes.get("job-originating-host-name") or socket.gethostname(),
                "printerName": printer_uri.rstrip("/").rsplit("/", 1)[-1] or None,
                "printerUri": printer_uri or None,
                "copies": as_int(attributes.get("copies") or attributes.get("job-copies")),
                "totalPages": as_int(attributes.get("job-media-sheets")),
                "pagesCompleted": as_int(attributes.get("job-media-sheets-completed")),
                "submittedAt": epoch_to_iso(submitted),
                "status": STATE_NAMES.get(state, f"Unknown ({state})"),
                "statusCode": state,
                "statusReasons": list_value(attributes.get("job-state-reasons")),
                "documentFormat": attributes.get("document-format-detected")
                or attributes.get("document-format-supplied"),
                "jobSizeKOctets": as_int(attributes.get("job-k-octets")),
                "lastObservedAt": iso_now(),
                "applicationProcess": None,
                "applicationProcessNote": (
                    "Standard CUPS job attributes do not reliably include the originating process ID."
                ),
            }
        )
        self.persist(job_id)
        return first_detection

    def copy_spool_file(self, source: Path) -> bool:
        match = SPOOL_PATTERN.match(source.name)
        if not match or not source.is_file():
            return False
        job_id = int(match.group("job"))
        kind = match.group("kind").lower()
        document = match.group("document")
        record = self.get(job_id)
        if kind == "c":
            destination_name = "control_file.cups"
            captured_kind = "CUPS control"
        else:
            destination_name = f"spool_document_{int(document or 1):03d}.bin"
            captured_kind = "CUPS document data"
        destination = record["directory"] / destination_name
        temporary = destination.with_suffix(destination.suffix + ".copying")
        try:
            with source.open("rb") as input_stream, temporary.open("wb") as output_stream:
                shutil.copyfileobj(input_stream, output_stream, length=256 * 1024)
            if not destination.exists() or temporary.stat().st_size >= destination.stat().st_size:
                os.replace(temporary, destination)
            else:
                temporary.unlink(missing_ok=True)
            captured = next(
                (item for item in record["metadata"]["capturedFiles"] if item["fileName"] == destination_name),
                None,
            )
            if captured is None:
                captured = {"fileName": destination_name, "kind": captured_kind}
                record["metadata"]["capturedFiles"].append(captured)
            captured.update(
                {
                    "bytes": destination.stat().st_size,
                    "lastCopiedAt": iso_now(),
                    "sha256": sha256(destination),
                    "detectedFormat": detect_format(destination, record["metadata"].get("documentFormat")),
                }
            )
            self.persist(job_id)
            return True
        except (OSError, PermissionError) as exc:
            temporary.unlink(missing_ok=True)
            warning = f"Could not copy {source}: {exc}"
            warnings = record["metadata"]["captureWarnings"]
            if warning not in warnings:
                warnings.append(warning)
                self.persist(job_id)
            return False

    def persist(self, job_id: int) -> None:
        record = self.records[job_id]
        destination = record["directory"] / "metadata.json"
        temporary = destination.with_suffix(".json.tmp")
        temporary.write_text(json.dumps(record["metadata"], indent=2, ensure_ascii=False), encoding="utf-8")
        os.replace(temporary, destination)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path(__file__).parents[1] / "captured_jobs_linux")
    parser.add_argument("--spool-directory", type=Path, default=Path("/var/spool/cups"))
    parser.add_argument("--server", help="CUPS server, for example localhost:8631")
    parser.add_argument("--poll-seconds", type=float, default=0.25)
    parser.add_argument("--upload-url", help="Optional multipart POST endpoint")
    parser.add_argument("--no-spool-capture", action="store_true")
    parser.add_argument("--once", action="store_true", help="Take one snapshot and exit")
    return parser.parse_args()


def connect(server: str | None):
    if server:
        cups.setServer(server)
    return cups.Connection()


def main() -> int:
    args = parse_args()
    if args.poll_seconds < 0.05:
        raise SystemExit("--poll-seconds must be at least 0.05")
    try:
        connection = connect(args.server)
    except RuntimeError as exc:
        print(f"[FATAL] Cannot connect to CUPS: {exc}", file=sys.stderr)
        print("Start CUPS or use linux/scripts/Run-Sandbox-Test.sh.", file=sys.stderr)
        return 2

    store = CaptureStore(args.output)
    announced: set[int] = set()
    uploaded: set[int] = set()
    terminal_since: dict[int, float] = {}
    spool_sizes: dict[Path, int] = {}
    print("PrintCapturePOC - passive Linux/CUPS monitor")
    print(f"CUPS server: {args.server or cups.getServer()}")
    print(f"Capture output: {store.root}")
    if not args.no_spool_capture:
        print(f"Spool directory: {args.spool_directory}")
        if not os.access(args.spool_directory, os.R_OK):
            print("[WARNING] Spool directory is not readable. Run with sudo for payload capture.")
    print("Monitoring. Press Ctrl+C to stop. Printing is never paused or modified.\n")

    while True:
        try:
            jobs = connection.getJobs(
                which_jobs="all",
                my_jobs=False,
                requested_attributes=JOB_ATTRIBUTES,
            )
            for raw_id, attributes in jobs.items():
                job_id = int(raw_id)
                first = store.update_job(job_id, attributes)
                if first and job_id not in announced:
                    announced.add(job_id)
                    print_detected(store.records[job_id]["metadata"], store.records[job_id]["directory"])

            if not args.no_spool_capture:
                try:
                    for source in args.spool_directory.iterdir():
                        if not SPOOL_PATTERN.match(source.name) or not source.is_file():
                            continue
                        try:
                            current_size = source.stat().st_size
                        except OSError:
                            continue
                        if spool_sizes.get(source) != current_size:
                            store.copy_spool_file(source)
                            spool_sizes[source] = current_size
                    for vanished in set(spool_sizes) - set(args.spool_directory.iterdir()):
                        spool_sizes.pop(vanished, None)
                except (OSError, PermissionError) as exc:
                    print(f"[SPOOL WATCH WARNING] {exc}", file=sys.stderr)

            if args.upload_url:
                for job_id, record in list(store.records.items()):
                    state = record["metadata"].get("statusCode")
                    if state in TERMINAL_STATES:
                        terminal_since.setdefault(job_id, time.monotonic())
                    else:
                        terminal_since.pop(job_id, None)
                    ready_to_upload = (
                        state in TERMINAL_STATES
                        and bool(record["metadata"]["capturedFiles"])
                        and time.monotonic() - terminal_since[job_id] >= 0.5
                    )
                    if ready_to_upload and job_id not in uploaded:
                        uploaded.add(job_id)
                        upload(record, args.upload_url)
                        store.persist(job_id)

            if args.once:
                return 0
            time.sleep(args.poll_seconds)
        except KeyboardInterrupt:
            print("\nStopped.")
            return 0
        except cups.IPPError as exc:
            print(f"[CUPS WARNING] {exc}; retrying", file=sys.stderr)
            time.sleep(max(args.poll_seconds, 1.0))
            try:
                connection = connect(args.server)
            except RuntimeError:
                pass


def print_detected(metadata: dict[str, Any], directory: Path) -> None:
    print("[PRINT DETECTED]")
    print(f"Job ID: {metadata['jobId']}")
    print(f"User: {metadata.get('userName') or '(unavailable)'}")
    print(f"Computer: {metadata.get('computerName') or '(unavailable)'}")
    print(f"Document: {metadata.get('documentName') or '(unavailable)'}")
    print(f"Printer: {metadata.get('printerName') or '(unavailable)'}")
    print(f"Pages: {metadata.get('totalPages') or 'unknown while processing'}")
    print(f"Copies: {metadata.get('copies') or 'unavailable'}")
    print(f"Time: {metadata.get('submittedAt') or iso_now()}")
    print(f"Status: {metadata.get('status')}")
    print(f"Document format: {metadata.get('documentFormat') or 'unknown'}")
    print(f"Captured folder: {directory}\n", flush=True)


def upload(record: dict[str, Any], url: str) -> None:
    metadata = record["metadata"]
    metadata["upload"].update({"enabled": True, "attempted": True})
    boundary = f"----PrintCapturePOC{uuid.uuid4().hex}"
    body = bytearray()

    def field(name: str, value: bytes, filename: str | None = None, content_type: str = "text/plain") -> None:
        body.extend(f"--{boundary}\r\n".encode())
        disposition = f'Content-Disposition: form-data; name="{name}"'
        if filename:
            disposition += f'; filename="{filename}"'
        body.extend(f"{disposition}\r\nContent-Type: {content_type}\r\n\r\n".encode())
        body.extend(value)
        body.extend(b"\r\n")

    field("metadata", json.dumps(metadata).encode(), content_type="application/json")
    for captured in metadata["capturedFiles"]:
        path = record["directory"] / captured["fileName"]
        if path.exists():
            field("files", path.read_bytes(), path.name, mimetypes.guess_type(path.name)[0] or "application/octet-stream")
    body.extend(f"--{boundary}--\r\n".encode())
    try:
        req = request.Request(url, data=body, method="POST", headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
        with request.urlopen(req, timeout=30) as response:
            metadata["upload"].update({"succeeded": 200 <= response.status < 300, "httpStatus": response.status})
    except Exception as exc:  # Upload must never affect print monitoring.
        metadata["upload"].update({"succeeded": False, "error": str(exc)})


def as_int(value: Any) -> int | None:
    try:
        return int(value) if value is not None else None
    except (TypeError, ValueError):
        return None


def list_value(value: Any) -> list[Any]:
    if value is None:
        return []
    return list(value) if isinstance(value, (list, tuple)) else [value]


def iso_now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%S%z")


def epoch_to_iso(value: Any) -> str | None:
    number = as_int(value)
    return time.strftime("%Y-%m-%dT%H:%M:%S%z", time.localtime(number)) if number else None


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def detect_format(path: Path, reported: str | None) -> str:
    prefix = path.read_bytes()[:8]
    if prefix.startswith(b"%PDF"):
        return "PDF"
    if prefix.startswith(b"%!"):
        return "PostScript"
    if prefix.startswith(b"PK\x03\x04"):
        return "ZIP package (possibly XPS/OXPS or an office format)"
    if prefix.startswith(b"\x1bE"):
        return "Likely PCL"
    return reported or "Unknown/device-specific"


if __name__ == "__main__":
    raise SystemExit(main())
