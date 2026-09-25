# PrintCapturePOC

This repository has a shared **Print & Save** app that runs on Windows, macOS, and Linux. The user selects a document in a local browser page; the app saves the exact source plus JSON metadata and SHA-256, then sends that same file directly to an IPP network printer. It does not require CUPS on Windows or macOS.

It also contains OS-specific passive monitoring experiments. The Windows monitor detects jobs submitted by other applications and makes a best-effort copy of `.SPL`/`.SHD` files. The Linux monitor performs the comparable CUPS experiment. Monitoring every application's jobs cannot use one portable implementation because each OS protects and represents spool data differently.

## Automatically select the passive monitor

The unified launcher detects Windows or Linux and starts that platform's monitor. From the repository root, build once and run:

```sh
dotnet build PrintCapturePOC.sln -c Release
dotnet run --project src/PrintCaptureLauncher --
```

On Windows, open the terminal as Administrator to capture spool files. On Linux, the launcher requests `sudo` for access to CUPS spool files; install `python3-cups` and have CUPS running first. The launcher stores Windows captures in `captured_jobs/` and Linux captures in `captured_jobs_linux/`. Pass monitor options after `--`, for example `dotnet run --project src/PrintCaptureLauncher -- --no-spool-capture` for metadata-only monitoring.

The passive monitor supports Windows and Linux. macOS can run the shared Print & Save app below, but this repository does not include a macOS passive print-queue monitor.

## Cross-platform Print & Save app

Requirements for a source checkout: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and an IPP/IPPS network printer that supports the selected document format. The configured test printer is `ipp://192.168.8.43/ipp/print`.

Windows PowerShell:

```powershell
cd PrintCapturePOC
.\scripts\Run-PrintSave.ps1
```

macOS or Linux Terminal:

```bash
cd PrintCapturePOC
chmod +x scripts/Run-PrintSave.sh
./scripts/Run-PrintSave.sh
```

The app opens `http://127.0.0.1:8765`. Save/test the printer, choose a file, and click **Print and save locally**. The archive is written to `Documents/PrintCapturePOC/printed_jobs`, including `metadata.json` and the exact selected source. Printer settings are local to each computer under the user's `.printcapturepoc` folder and are not committed.

PDF and JPEG are the safest portable inputs. Other formats work only when the printer reports that MIME type as supported; otherwise export to PDF first. This controlled app captures only prints initiated from its own page. See the [cross-platform plan](docs/CROSS_PLATFORM_PLAN.md) for why capturing all prints from Chrome, Word, Preview, and other applications remains OS-specific.

## Linux/CUPS version

A terminal-runnable Linux implementation is included under `linux/`. To run the isolated fake-printer test without a physical printer:

```bash
chmod +x linux/monitor_cups.py linux/scripts/*.sh
./linux/scripts/Run-Sandbox-Test.sh
```

It requests `sudo`, starts a private CUPS instance, submits a sample through a local test backend, and verifies metadata and spool-file capture without changing system CUPS configuration. See [`linux/README.md`](linux/README.md) for real-printer and metadata-only commands.

## Finding: what Windows actually exposes

**Queue monitoring:** Yes. `FindFirstPrinterChangeNotification` can monitor job changes on a printer or print-server handle. This POC opens the local print-server handle for notifications, then enumerates installed queues and reads jobs with `EnumJobs` level 2. A 500 ms fallback scan covers lost/unsupported notifications. Microsoft documents both the [notification API](https://learn.microsoft.com/en-us/windows/win32/printdocs/findfirstprinterchangenotification) and [`JOB_INFO_2`](https://learn.microsoft.com/en-us/windows/win32/printdocs/job-info-2).

**Direct metadata:** The spooler normally exposes job ID, document name, user, submitting machine, printer, data type, print processor, driver, submission time, status, total/pages printed, and a `DEVMODE` from which requested copies can often be read. Values are driver/application dependent: page count may remain zero until rendering finishes, copies may be implemented by an application or printer and not reflected in `dmCopies`, and status can move too quickly to observe.

**Originating application/process:** `JOB_INFO_2` does not expose a process ID or executable. The POC records this as unavailable rather than guessing from the document title. Reliable process attribution requires a separate, time-correlated ETW/event collector and still is not part of the print job contract.

**Spool files:** The default directory is `%SystemRoot%\System32\spool\PRINTERS`; the configured location is `HKLM\SYSTEM\CurrentControlSet\Control\Print\Printers\DefaultSpoolDirectory`. Microsoft confirms the default/configuration and that successfully printed files are deleted in its [printing troubleshooting guidance](https://learn.microsoft.com/en-us/troubleshoot/windows-server/printing/troubleshoot-printing-scenarios). Files are commonly numbered `.SPL` payloads and `.SHD` shadow metadata. `.SHD` is treated as opaque and is not parsed.

**Payload format:** There is no automatic PDF copy. Windows documents EMF, text, and RAW spool types; RAW may itself be PCL, PostScript, PDF, or another printer-specific page-description language. XPSDrv queues can spool XPS. The protocol documentation lists `EMFSPOOL`, `RAW`, XPS, and custom processor types ([MS-RPRN spool formats](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/e81cbc09-ab05-4a32-ae4a-8ec57b436c43)); Microsoft also explains the [classic spooler formats](https://learn.microsoft.com/en-us/windows/win32/printdocs/print-spooler) and [XPS print path](https://learn.microsoft.com/en-us/windows-hardware/drivers/print/improved-spooling-and-rendering).

**Can the file be copied safely?** Sometimes, not reliably for every job. The spooler deletes files after successful printing. This POC watches create/write events and opens files with read/write/delete sharing, repeatedly preserving the largest snapshot it sees. It does not lock or pause the job. Very small/fast jobs, remote/server-side queues, protected queues, driver streaming, antivirus, or access controls can still cause a miss or partial snapshot. Running elevated is normally required to read the spool directory. Changing “keep printed documents” would improve retention but mutates printer configuration and is intentionally not done.

**Conversion:** There is no universal `.SPL`-to-PDF conversion. A RAW stream is device-specific; an EMFSPOOL file is not simply a standalone `.emf`; XPS is the most archive-friendly common path. The POC performs conservative signature/type identification only and preserves original bytes. It does not create a misleading `converted_output.pdf`. Format-specific conversion can be added after test captures prove that a particular fleet consistently emits XPS, PostScript, or PDF.

## Architecture

```text
Winspool notification + 500 ms fallback scan
             |                         
             +--> JOB_INFO_2 metadata ----+--> job_00042_timestamp/metadata.json
                                          |
spool directory FileSystemWatcher --------+--> spool_file.spl / shadow_file.shd
                                          |
                                          +--> optional multipart HTTP POST
```

The spool-directory watcher and queue reader are independent. This matters because a payload file may appear before the job metadata can be enumerated, or a fast job may leave the queue before a scan. Captures are correlated by the numeric spool filename/job ID and updated atomically.

## Run the experiment on Windows 10/11

Requirements: Windows x64, an installed physical printer, and the .NET 8 SDK for development. Open **Windows Terminal or PowerShell as Administrator** so the process can read the protected spool folder.

```powershell
cd PrintCapturePOC
dotnet build .\PrintCapturePOC.sln
.\scripts\Run-Development.ps1
```

Then print one-page and multi-page samples from Chrome, Word, Notepad, and the chosen PDF reader. Use a physical queue, not “Microsoft Print to PDF,” for the main success test. Captures appear under `captured_jobs`.

The console should show:

```text
[PRINT DETECTED]
Job ID: 42
User: Shivam
Computer: DESKTOP-EXAMPLE
Document: invoice.pdf
Printer: HP LaserJet
Pages: 4
Copies: 1
Time: 2026-09-17 16:45:00 +05:30
Status: Printing
Spool Format: RAW
Captured Folder: ...\captured_jobs\job_00042_...
```

### One-time elevated startup setup

`Install-Poc.ps1` asks for UAC consent, publishes a self-contained executable into `%ProgramData%\PrintCapturePOC`, and registers an elevated per-user logon task. This is the POC interpretation of “permission once.” Windows has no dedicated user-facing “print monitoring permission” API. Review the script before running it.

```powershell
.\scripts\Install-Poc.ps1
```

For a production installer, use a signed Windows service running under a narrowly scoped service identity, explicit consent/privacy UI, ACLs and encryption for captured documents, retention controls, and a signed installer. This POC intentionally does not implement those pieces.

## Optional local upload test

Start the receiver:

```powershell
cd mock_server
py -m venv .venv
.\.venv\Scripts\pip install -r requirements.txt
.\.venv\Scripts\uvicorn app:app --host 127.0.0.1 --port 8000
```

In a second elevated terminal:

```powershell
.\scripts\Run-Development.ps1 --upload-url http://127.0.0.1:8000/api/print-jobs
```

The client sends one `multipart/form-data` POST after a job has disappeared from the queue: a `metadata` JSON field plus zero or more `files` parts. Upload failure is recorded in `metadata.json` and never affects printing.

## Validation matrix

For each application, record the selected physical printer/driver model, whether detection occurred, metadata completeness, captured file sizes, `spoolDataType`, detected signature, and whether output printed normally.

| Source | Expected detection | Expected payload caveat |
|---|---:|---|
| Notepad | Yes | Often EMF/RAW depending on driver |
| Word | Yes | Page count/copies may settle late |
| Chrome | Yes | Document title may be a page title, not filename |
| Adobe/other PDF reader | Yes | Source PDF is not necessarily preserved; driver may rasterize or emit PCL/PS |
| Remote print server queue | Metadata often | Payload may exist only on the server and not be locally capturable |

Test at least: one page, 20+ pages, two copies, duplex, a fast local USB printer, a network printer, and a deliberately offline/paused queue. Compare printed output with the source. Inspect JSON and hashes; do not assume a non-empty `.SPL` is complete merely because it was copied.

## Decision after the experiment

If passive capture meets the fleet-specific capture rate and preserving opaque printer-ready bytes is sufficient, harden this design as a service.

If every document must yield a stable viewable archive, passive spool-folder copying is the wrong contract. The next-smallest controlled solution is usually a **virtual XPS printer/queue** that captures XPS and then forwards a separately rendered job to the chosen physical printer. It changes the user/queue flow and needs careful fidelity testing. A **custom print processor or port monitor** can observe a well-defined point in the pipeline and preserve normal queue selection, but it is driver-adjacent, requires WDK/native code, signing/deployment work, and is substantially riskier. Choose it only after this matrix demonstrates that passive capture misses required jobs or formats.

## Safety and privacy boundaries

- Capture is passive and best-effort; all exceptions are logged and swallowed outside the print path.
- The program never calls `SetJob`, `PausePrinter`, `WritePrinter`, or deletion APIs.
- Captured spool data can contain complete confidential documents, embedded fonts, usernames, and device commands. Restrict ACLs, obtain informed consent, encrypt at rest/in transit, and define retention before any real deployment.
- Do not attempt to render unknown RAW data by sending it to another printer or shelling it into a viewer. Treat it as untrusted binary input.
