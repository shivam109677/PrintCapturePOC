# Cross-platform implementation plan

The project has two related but technically different product modes. Keeping them separate prevents the reliable controlled workflow from being blocked by the harder operating-system monitoring problem.

## Mode A: controlled Print & Save

The user opens this application, selects a document, and clicks Print. The application archives the exact selected source and metadata before sending it to the printer. This is the working Linux frontend today.

This mode can share one UI and job model across Windows, macOS, and Linux:

```text
Shared UI
  -> archive exact selected source + metadata + SHA-256
  -> platform print adapter
  -> poll platform job status
  -> optional upload adapter
```

Recommended production implementation:

- Shared desktop UI: Avalonia on .NET 8, or a signed local-webview shell around the existing local UI.
- Shared archive module: job directory, source bytes, metadata JSON, hashing, retention and upload queue.
- `IPrintAdapter` interface: discover printers, validate capabilities, submit, query status, cancel only when the user explicitly requests it.
- Windows adapter: Windows Print Spooler/PrintTicket APIs.
- Linux adapter: CUPS/IPP.
- macOS adapter: CUPS/IPP initially, followed by native PrintCore where signing/sandbox rules require it.
- Direct IPP adapter: optional for known IPP Everywhere printers. It provides the most consistent cross-platform path when the selected printer accepts PDF/JPEG/Office formats directly.

Mode A guarantees the archived source because the application controls submission. It does not capture jobs initiated in Chrome, Word, Preview, or other applications.

## Mode B: monitor every normal application

This mode observes jobs initiated outside this application. It cannot be implemented as one identical cross-platform component:

| Platform | Detection | Payload capture |
|---|---|---|
| Windows | Winspool job notifications and `EnumJobs` | Best-effort `.SPL`/`.SHD`; format is driver-dependent |
| Linux | CUPS IPP job queries | Best-effort CUPS control/data files; usually requires service privileges |
| macOS | CUPS/PrintCore observation | Protected spool storage and platform security require a signed helper and macOS validation |

Passive payload capture is inherently best-effort. A print job may be deleted quickly, rendered remotely, streamed, or represented as PCL/PostScript/raster rather than the original document.

If every external print must produce a viewable archive, the next controlled design is a managed virtual printer/queue that receives PDF or XPS and forwards a separately rendered job. That is platform-specific driver-adjacent work and should follow fleet testing of the current passive experiments.

## Delivery phases

1. Harden the working Linux controlled frontend: configurable printers, retention, upload retries, tests and packaging.
2. Extract shared archive/job/upload contracts.
3. Package the shared frontend and add Windows and macOS controlled-submit adapters.
4. Validate installers, code signing, least-privilege helpers and automatic updates per OS.
5. Continue passive-monitor experiments separately on each OS.
6. Decide whether fleet requirements justify a virtual printer or print processor.

## Repository safety

Never commit captured documents, spool files, received uploads, local printer IPs, secrets or machine-specific configuration. The repository `.gitignore` excludes runtime capture folders and `linux/frontend/config.json`; only `config.example.json` is versioned.
