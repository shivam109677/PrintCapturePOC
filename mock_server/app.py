from pathlib import Path
import json
import re

from fastapi import FastAPI, File, Form, HTTPException, UploadFile

app = FastAPI(title="PrintCapturePOC receiver")
upload_root = Path(__file__).resolve().parent / "received_jobs"
upload_root.mkdir(parents=True, exist_ok=True)


@app.post("/api/print-jobs")
async def receive_print_job(
    metadata: str = Form(...), files: list[UploadFile] = File(default=[])
):
    try:
        parsed = json.loads(metadata)
        job_id = int(parsed["jobId"])
    except (json.JSONDecodeError, KeyError, TypeError, ValueError) as exc:
        raise HTTPException(status_code=400, detail="Invalid metadata JSON/jobId") from exc

    stamp = re.sub(r"[^0-9A-Za-z_.-]", "_", str(parsed.get("firstObservedAt", "unknown")))
    target = upload_root / f"job_{job_id:05d}_{stamp}"
    target.mkdir(parents=True, exist_ok=True)
    (target / "metadata.json").write_text(
        json.dumps(parsed, indent=2, ensure_ascii=False), encoding="utf-8"
    )

    saved = []
    for item in files:
        safe_name = Path(item.filename or "captured_file.bin").name
        destination = target / safe_name
        with destination.open("wb") as output:
            while chunk := await item.read(1024 * 1024):
                output.write(chunk)
        saved.append({"name": safe_name, "bytes": destination.stat().st_size})

    return {"accepted": True, "jobId": job_id, "files": saved}
