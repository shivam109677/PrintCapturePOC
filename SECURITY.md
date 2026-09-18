# Security and privacy

Print captures can contain complete confidential documents, usernames, hostnames, printer addresses, embedded fonts and device commands.

This repository is a proof of concept. Before production use:

- obtain explicit informed consent;
- restrict capture directories with least-privilege ACLs;
- encrypt documents at rest and in transit;
- authenticate uploads and implement retry/idempotency controls;
- define retention and deletion policies;
- sign installers, executables and privileged helpers;
- treat captured and printer-ready data as untrusted binary input;
- avoid rendering or replaying unknown RAW spool data automatically;
- provide a visible way to stop monitoring and inspect/delete local captures.

Do not report vulnerabilities with real captured documents attached. Use synthetic samples and redact printer addresses, usernames and tokens.
