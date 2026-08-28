# Security Policy

## Supported versions

SockTuner is alpha software. Only the most recent pre-release receives fixes; older tags are
snapshots and are not patched. There is no long-term support branch yet.

| Version | Supported |
| --- | --- |
| Latest pre-release | Yes |
| Anything earlier | No |

## Reporting a vulnerability

Report privately through [GitHub's private vulnerability reporting](https://github.com/PrimeBuild-pc/SockTuner/security/advisories/new)
on this repository. Do not open a public issue for a security problem — an issue is world-readable
the moment it is filed, and this application asks for administrator rights.

Include what you did, what happened, and the build you used. A proof of concept is welcome but not
required; a clear description of the privilege boundary you crossed is worth more than an exploit.

Expect an acknowledgement within a week. This is a personal open-source project, not a company with
an on-call rota, so that is a realistic figure rather than a service commitment. If a report is
valid you will be credited in the release notes unless you ask otherwise.

## What is in scope

The privilege boundary is the interesting part, and it is the part worth attacking:

- Anything that makes the elevated worker perform an operation outside its allowlist.
- Anything that makes it write a registry address, a NIC keyword, or a value that no catalog entry
  or driver advertises.
- Anything that turns imported, untrusted data — a capture report, an exported diagnostic, a probe
  report — into code execution, a file path, or a write.
- Anything that causes an exported report or a probe report to contain data the redaction claims to
  have removed.
- Anything that leaves the machine unable to reach the network with no path back to the captured
  state.

Out of scope: SmartScreen warnings on the unsigned pre-release (known and documented), the absence
of a code-signing certificate (known), and reports that a setting SockTuner deliberately declines to
write would have been beneficial.

## How the privilege boundary is built

Stated here so it can be checked rather than trusted.

The user interface runs unelevated and can only read. Every write goes to a separate elevated
process, which is the **same executable** re-launched with `runas` — there is no second binary to
substitute and no service left running afterwards. The two talk over a named pipe with a random
per-request name, carrying newline-delimited JSON. The elevated side is the pipe *client*; the
unelevated side creates the pipe, whose default ACL admits the creating user and administrators.

That channel is deliberately not where the security lives. The elevated worker treats every request
as untrusted input from a peer that may already be compromised, and re-does the entire decision on
its own side:

- The operation must be one of a small set of typed, versioned kinds. Plans never carry executable
  paths, script fragments, shell strings, or free-form registry paths.
- A registry-backed change must resolve to an address the static catalog owns. The address is
  re-resolved inside the worker from the setting id, never taken from the request.
- A NIC change is legal only if the installed driver *still* advertises that keyword for that
  adapter and the value fits the constraints the driver itself reports. There is no static list of
  writable NIC keywords: the driver is the allowlist, and it is re-read inside the worker.
- A small set of keywords is refused at any value, checked before the driver's own constraints, so
  characterisation can withhold a write but can never authorise one.
- Every change is snapshotted before it is applied and verified by reading it back afterwards.
  External drift is refused rather than overwritten.

Moving any of that validation to the calling side would collapse the boundary, which is why it
lives in the worker even where it duplicates a check the UI already made.

## Privacy

SockTuner has no backend, sends no telemetry, and makes no network connection you did not ask for.
The only outbound traffic is the measurement you start: probes to the endpoints you chose, DNS
queries to resolvers you selected, and the throughput transfer you configured.

Reports are written locally and shared only if you choose to share them. Both the diagnostic export
and the capability probe offer a redacted form; the probe masks the machine name, addresses, and
user-assigned values while keeping hardware identity. Redaction is a feature with tests, not a
promise — read a report before you attach it to an issue, and report anything that survives
redaction as a vulnerability under the scope above.
