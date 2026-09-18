#!/usr/bin/env python3
"""Local-only browser UI for controlled print, archive, and metadata capture."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import socket
import threading
import time
from typing import Any
import webbrowser

import cups
from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import HTMLResponse
import uvicorn


APP_DIRECTORY = Path(__file__).resolve().parent
PROJECT_DIRECTORY = APP_DIRECTORY.parents[1]
CONFIG_PATH = APP_DIRECTORY / "config.json"
OUTPUT_ROOT = PROJECT_DIRECTORY / "printed_jobs"
CONFIG = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
OUTPUT_ROOT.mkdir(parents=True, exist_ok=True)

app = FastAPI(title="PrintCapturePOC Local Print")
job_directories: dict[int, Path] = {}

JOB_STATES = {
    3: "Pending",
    4: "Held",
    5: "Processing",
    6: "Stopped",
    7: "Canceled",
    8: "Aborted",
    9: "Completed",
}


@app.get("/", response_class=HTMLResponse)
def home() -> str:
    return PAGE.replace("__PRINTER_NAME__", escape_html(CONFIG["displayName"])).replace(
        "__PRINTER_IP__", escape_html(CONFIG["printerIp"])
    )


@app.get("/api/status")
def printer_status() -> dict[str, Any]:
    try:
        with socket.create_connection((CONFIG["printerIp"], 631), timeout=1.5):
            pass
        connection = cups.Connection()
        printers = connection.getPrinters()
        queue = printers.get(CONFIG["queueName"])
        if queue is None:
            return {
                "ready": False,
                "message": f"CUPS queue {CONFIG['queueName']} is not installed.",
                "setupCommand": "./linux/scripts/Setup-Physical-Printer.sh",
            }
        state = int(queue.get("printer-state", 0) or 0)
        # getPrinters() does not always include this attribute (notably for
        # cups-browsed implicitclass queues); absence is not a rejection.
        accepting = bool(queue.get("printer-is-accepting-jobs", state != 5))
        reasons = value_list(queue.get("printer-state-reasons"))
        ready = accepting and state != 5
        return {
            "ready": ready,
            "queueInstalled": True,
            "acceptingJobs": accepting,
            "state": {3: "Idle", 4: "Processing", 5: "Stopped"}.get(state, f"State {state}"),
            "reasons": reasons,
            "printer": CONFIG["displayName"],
            "ip": CONFIG["printerIp"],
            "message": "Printer is ready." if ready else "Printer is installed but needs attention.",
        }
    except OSError as exc:
        return {"ready": False, "message": f"Printer {CONFIG['printerIp']} is unreachable: {exc}"}
    except RuntimeError as exc:
        return {"ready": False, "message": f"Cannot connect to local CUPS: {exc}"}


@app.post("/api/print")
async def print_document(
    document: UploadFile = File(...),
    copies: int = Form(1),
    color_mode: str = Form("auto"),
) -> dict[str, Any]:
    if not 1 <= copies <= 99:
        raise HTTPException(400, "Copies must be between 1 and 99.")
    if color_mode not in {"auto", "color", "monochrome"}:
        raise HTTPException(400, "Invalid color mode.")

    original_name = Path(document.filename or "document.bin").name
    safe_name = safe_filename(original_name)
    stamp = time.strftime("%Y%m%d_%H%M%S")
    job_directory = OUTPUT_ROOT / f"job_pending_{stamp}_{time.time_ns() % 1_000_000:06d}"
    job_directory.mkdir(parents=True, exist_ok=False)
    archived_file = job_directory / f"source_{safe_name}"
    metadata_path = job_directory / "metadata.json"

    digest = hashlib.sha256()
    size = 0
    try:
        with archived_file.open("wb") as output:
            while chunk := await document.read(1024 * 1024):
                size += len(chunk)
                if size > int(CONFIG["maxUploadBytes"]):
                    raise HTTPException(413, "Document exceeds the 64 MiB POC limit.")
                digest.update(chunk)
                output.write(chunk)

        metadata: dict[str, Any] = {
            "schemaVersion": 1,
            "platform": "linux-cups-controlled-submit",
            "documentName": original_name,
            "archivedFile": archived_file.name,
            "bytes": size,
            "sha256": digest.hexdigest(),
            "userName": os.environ.get("SUDO_USER") or os.environ.get("USER"),
            "computerName": socket.gethostname(),
            "printerName": CONFIG["displayName"],
            "printerIp": CONFIG["printerIp"],
            "printerUri": CONFIG["printerUri"],
            "queueName": CONFIG["queueName"],
            "copies": copies,
            "colorMode": color_mode,
            "submittedAt": iso_now(),
            "status": "Submitting",
        }
        write_json(metadata_path, metadata)

        connection = cups.Connection()
        if CONFIG["queueName"] not in connection.getPrinters():
            raise RuntimeError(
                f"CUPS queue '{CONFIG['queueName']}' is missing. Run Setup-Physical-Printer.sh first."
            )
        options = {"copies": str(copies)}
        if color_mode != "auto":
            options["print-color-mode"] = color_mode
        job_id = int(connection.printFile(CONFIG["queueName"], str(archived_file), original_name, options))

        final_directory = OUTPUT_ROOT / f"job_{job_id:05d}_{stamp}"
        os.replace(job_directory, final_directory)
        job_directory = final_directory
        metadata_path = final_directory / "metadata.json"
        archived_file = final_directory / archived_file.name
        metadata.update(
            {
                "jobId": job_id,
                "status": "Submitted",
                "localDirectory": str(final_directory),
                "lastUpdatedAt": iso_now(),
            }
        )
        write_json(metadata_path, metadata)
        job_directories[job_id] = final_directory
        return {
            "ok": True,
            "jobId": job_id,
            "status": "Submitted",
            "documentName": original_name,
            "sha256": metadata["sha256"],
            "bytes": size,
            "savedTo": str(final_directory),
            "printer": CONFIG["displayName"],
        }
    except HTTPException:
        shutil.rmtree(job_directory, ignore_errors=True)
        raise
    except Exception as exc:
        metadata = locals().get("metadata", {})
        metadata.update({"status": "SubmissionFailed", "error": str(exc), "lastUpdatedAt": iso_now()})
        write_json(metadata_path, metadata)
        raise HTTPException(500, f"The document was saved, but print submission failed: {exc}") from exc


@app.get("/api/jobs/{job_id}")
def job_status(job_id: int) -> dict[str, Any]:
    directory = job_directories.get(job_id)
    if directory is None:
        candidates = sorted(OUTPUT_ROOT.glob(f"job_{job_id:05d}_*"), reverse=True)
        directory = candidates[0] if candidates else None
    if directory is None:
        raise HTTPException(404, "Unknown job")
    metadata_path = directory / "metadata.json"
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    try:
        attributes = cups.Connection().getJobAttributes(
            job_id,
            requested_attributes=[
                "job-state",
                "job-state-reasons",
                "job-media-sheets",
                "job-media-sheets-completed",
            ],
        )
        state = int(attributes.get("job-state", 0) or 0)
        metadata.update(
            {
                "status": JOB_STATES.get(state, f"Unknown ({state})"),
                "statusCode": state,
                "statusReasons": value_list(attributes.get("job-state-reasons")),
                "totalPages": optional_int(attributes.get("job-media-sheets")),
                "pagesCompleted": optional_int(attributes.get("job-media-sheets-completed")),
                "lastUpdatedAt": iso_now(),
            }
        )
        write_json(metadata_path, metadata)
    except cups.IPPError:
        pass
    return {
        "jobId": job_id,
        "status": metadata.get("status"),
        "statusReasons": metadata.get("statusReasons", []),
        "pagesCompleted": metadata.get("pagesCompleted"),
        "totalPages": metadata.get("totalPages"),
        "savedTo": str(directory),
    }


def safe_filename(value: str) -> str:
    cleaned = re.sub(r"[^A-Za-z0-9._ -]", "_", value).strip(" .")
    return cleaned[:180] or "document.bin"


def escape_html(value: str) -> str:
    return value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


def value_list(value: Any) -> list[Any]:
    if value is None:
        return []
    return list(value) if isinstance(value, (list, tuple)) else [value]


def optional_int(value: Any) -> int | None:
    try:
        return int(value) if value is not None else None
    except (TypeError, ValueError):
        return None


def iso_now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%S%z")


def write_json(path: Path, value: dict[str, Any]) -> None:
    temporary = path.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding="utf-8")
    os.replace(temporary, path)


PAGE = r"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
  <title>Print & Save</title>
  <style>
    :root{font-family:Inter,system-ui,sans-serif;color:#172033;background:#eef3f7}*{box-sizing:border-box}
    body{margin:0;min-height:100vh;display:grid;place-items:center;padding:24px}
    main{width:min(680px,100%);background:#fff;border-radius:18px;box-shadow:0 18px 50px #26384d22;padding:30px}
    h1{margin:0 0 6px;font-size:28px}.sub{color:#64748b;margin:0 0 24px}.printer{display:flex;gap:12px;align-items:center;background:#f7fafc;border:1px solid #dbe5ed;border-radius:12px;padding:14px;margin-bottom:22px}
    .dot{width:12px;height:12px;border-radius:50%;background:#94a3b8;box-shadow:0 0 0 4px #e2e8f0}.dot.ok{background:#16a34a;box-shadow:0 0 0 4px #dcfce7}.printer strong,.printer span{display:block}.printer span{color:#64748b;font-size:13px;margin-top:2px}
    .drop{display:block;border:2px dashed #b9c8d6;border-radius:14px;padding:32px 20px;text-align:center;cursor:pointer;transition:.2s}.drop:hover,.drop.active{border-color:#2563eb;background:#eff6ff}.drop input{display:none}.file{font-weight:700;margin-top:9px;word-break:break-all}.hint{font-size:13px;color:#64748b;margin-top:5px}
    .row{display:grid;grid-template-columns:1fr 1fr;gap:14px;margin-top:18px}label span{display:block;font-size:13px;font-weight:700;margin-bottom:6px}select,input[type=number]{width:100%;padding:11px;border:1px solid #cbd5e1;border-radius:9px;background:#fff;font-size:15px}
    button{width:100%;margin-top:20px;padding:13px;border:0;border-radius:10px;background:#2563eb;color:#fff;font-weight:800;font-size:16px;cursor:pointer}button:disabled{background:#94a3b8;cursor:not-allowed}
    #result{display:none;margin-top:18px;padding:15px;border-radius:10px;background:#f1f5f9;white-space:pre-wrap;word-break:break-word;font-size:14px}#result.ok{display:block;background:#ecfdf5;color:#166534}#result.err{display:block;background:#fef2f2;color:#991b1b}
  </style>
</head>
<body><main>
  <h1>Print & Save</h1><p class="sub">Select a document. A local copy and metadata are saved before it is sent to the printer.</p>
  <div class="printer"><div id="dot" class="dot"></div><div><strong>__PRINTER_NAME__</strong><span id="printerStatus">Checking __PRINTER_IP__…</span></div></div>
  <form id="form">
    <label class="drop" id="drop"><input id="document" name="document" type="file" required><div>Choose a file or drag it here</div><div id="fileName" class="file">No file selected</div><div class="hint">PDF, JPEG, PNG, Word, Excel, PowerPoint and other printer-supported files</div></label>
    <div class="row"><label><span>Copies</span><input name="copies" type="number" min="1" max="99" value="1"></label><label><span>Color</span><select name="color_mode"><option value="auto">Automatic</option><option value="color">Color</option><option value="monochrome">Black & white</option></select></label></div>
    <button id="printButton" disabled>Print and save locally</button>
  </form><div id="result"></div>
<script>
const form=document.querySelector('#form'), input=document.querySelector('#document'), drop=document.querySelector('#drop'), result=document.querySelector('#result'), button=document.querySelector('#printButton'); let printerReady=false;
async function status(){try{const r=await fetch('/api/status'),s=await r.json();printerReady=s.ready;document.querySelector('#dot').classList.toggle('ok',s.ready);document.querySelector('#printerStatus').textContent=s.message+(s.state?' '+s.state:'');button.disabled=!printerReady||!input.files.length}catch(e){document.querySelector('#printerStatus').textContent='Frontend cannot reach CUPS.'}}
input.onchange=()=>{document.querySelector('#fileName').textContent=input.files[0]?.name||'No file selected';button.disabled=!printerReady||!input.files.length};
for(const e of ['dragenter','dragover'])drop.addEventListener(e,x=>{x.preventDefault();drop.classList.add('active')});for(const e of ['dragleave','drop'])drop.addEventListener(e,x=>{x.preventDefault();drop.classList.remove('active')});drop.addEventListener('drop',e=>{input.files=e.dataTransfer.files;input.onchange()});
form.onsubmit=async e=>{e.preventDefault();button.disabled=true;button.textContent='Saving and submitting…';result.className='';try{const r=await fetch('/api/print',{method:'POST',body:new FormData(form)}),d=await r.json();if(!r.ok)throw Error(d.detail||'Print submission failed');result.className='ok';result.textContent=`Print job ${d.jobId} submitted.\nSaved to: ${d.savedTo}\nSHA-256: ${d.sha256}`;poll(d.jobId)}catch(x){result.className='err';result.textContent=x.message}finally{button.textContent='Print and save locally';button.disabled=!printerReady||!input.files.length}};
async function poll(id){for(let i=0;i<60;i++){await new Promise(r=>setTimeout(r,1000));const r=await fetch(`/api/jobs/${id}`),d=await r.json();result.textContent=`Print job ${id}: ${d.status}\nSaved to: ${d.savedTo}`;if(['Completed','Canceled','Aborted'].includes(d.status))break}}
status();setInterval(status,10000);
</script></main></body></html>"""


if __name__ == "__main__":
    threading.Timer(1.0, lambda: webbrowser.open("http://127.0.0.1:8765")).start()
    uvicorn.run(app, host="127.0.0.1", port=8765, log_level="warning")
