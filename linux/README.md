# Linux/CUPS terminal POC

Linux uses CUPS rather than the Windows Print Spooler. This POC monitors standard IPP job attributes through `python3-cups` and, when permitted, copies CUPS control/data spool files from `/var/spool/cups` without modifying the print job.

## Local Print & Save frontend

The browser frontend is configured for the verified HP Color LaserJet Pro M478f-9f at `192.168.8.43`. It checks both the printer's IPP port and the installed CUPS queue before enabling Print.

Run the one-time setup if the queue is missing:

```bash
./linux/scripts/Setup-Physical-Printer.sh
```

Start the frontend:

```bash
./linux/scripts/Run-Print-Frontend.sh
```

The local page opens at `http://127.0.0.1:8765`. Select or drag a file, choose copies and color mode, then click **Print and save locally**. Before submission, the frontend saves the exact uploaded source and its SHA-256 hash under:

```text
printed_jobs/job_<cups-id>_<timestamp>/
  metadata.json
  source_<original-name>
```

This controlled frontend guarantees source archiving for jobs submitted through it. It does not replace the passive monitor for jobs initiated from Chrome, LibreOffice, or other applications.

## Zero-hardware terminal test

On Ubuntu/Debian, install prerequisites if needed:

```bash
sudo apt install cups python3-cups
```

From the project root:

```bash
chmod +x linux/monitor_cups.py linux/scripts/*.sh
./linux/scripts/Run-Sandbox-Test.sh
```

When started without arguments, it interactively asks for the document path. Enter an absolute path or drag a file into the terminal. It then asks for an optional upload URL; press Enter for local capture only.

To test a specific document, pass its path:

```bash
./linux/scripts/Run-Sandbox-Test.sh /absolute/path/to/document.pdf
```

The script requests `sudo` because `cupsd` must start with service privileges, then runs a private CUPS scheduler on `127.0.0.1:8631`, installs a private test backend that writes printer-bound bytes to a sandbox file, starts the monitor, and submits `test_document.txt` with `lp`. Temporary CUPS configuration is placed under `/opt/printcapture-poc` and state under `/var/spool/cups/printcapture-poc` so Ubuntu's standard CUPS AppArmor policy permits access; both are removed when the test exits. It does not change the system CUPS configuration.

Expected output includes `[PRINT DETECTED]`, a submitted request such as `CaptureTest-1`, and files under:

```text
captured_jobs_linux/sandbox_run_<timestamp>/job_00001_<timestamp>/
  metadata.json
  control_file.cups
  spool_document_001.bin
```

This validates terminal submission, normal queue processing, job detection, metadata persistence, and access to available CUPS spool bytes. The fake backend's printer-bound copy is also placed under `.cups-sandbox/printer-output` for diagnosis.

## Test with system CUPS and a real printer

Ensure CUPS is running and a printer is installed:

```bash
sudo systemctl enable --now cups
lpstat -r
lpstat -p -d
```

Start the passive monitor (root is used only because `/var/spool/cups` is protected):

```bash
./linux/scripts/Run-System-Monitor.sh
```

In a second terminal, submit a file normally:

```bash
lp -d YOUR_PRINTER ./linux/test_document.txt
```

Printing from Chrome, LibreOffice, Evince, or another desktop application uses the same CUPS queues and should also be detected.

Metadata-only operation does not need spool-directory access:

```bash
python3 linux/monitor_cups.py --no-spool-capture
```

## Linux limitations

- CUPS IPP attributes provide the document/job name, submitting user and host, printer URI, state/reasons, timestamps, requested copies when supplied, sheets and completed sheets when the filter/backend reports them, and document MIME type when known.
- Standard CUPS attributes do not reliably expose the originating application process.
- `/var/spool/cups/cNNNNN` is CUPS control data and `dNNNNN-001` is submitted/filtered document data. Access normally requires root or the CUPS service identity.
- Data files may be the original PDF, PostScript, raster data, or another format chosen by the application/filter chain. They are not guaranteed to be PDF.
- CUPS can remove job files after completion. Passive polling therefore remains best-effort for fast jobs. The sandbox enables `PreserveJobFiles` only inside its private test scheduler; the production monitor does not change that system setting.
- Remote queues may process and retain payloads on another CUPS server, leaving only client-visible metadata locally.
