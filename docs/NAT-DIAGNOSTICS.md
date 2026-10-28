# Game ports & NAT

This page answers **what happened to SockTuner's UDP packets**, not whether every game has an
Open NAT or whether a network change is needed. A restrictive observation without gaming symptoms
is not a fault. This test does not measure latency, TCP or IPv6.

## Use

1. Open **Game ports & NAT**. Choose a game/platform reference or a custom local UDP port.
2. Check the publisher reference: listed ports can be remote destinations rather than local source
   ports. Presets offer one representative port; they are not a scan of the published ranges or a
   recommendation to forward all of them. Hosting presets describe a server, not a game client.
3. Close the game/server before using its port. SockTuner binds exclusively and never silently
   substitutes another port. `0` explicitly requests a temporary port. Access denial can mean an OS
   reservation rather than another process: the result says **unavailable**, not necessarily in use.
4. Start the test. It generates a small, bounded amount of UDP traffic, runs for at most 35 seconds
   and can be cancelled. No network traffic is generated just by opening the page.
5. Read the separate observations and their limitations. If gaming already works, leave the network
   alone. Router guidance is informational, not an automatic action plan.

The socket is disposed after the run or cancellation. No dependencies, elevated worker calls, UPnP
requests, firewall changes, router changes or public port-scanning services are involved. A Windows
firewall prompt, if the OS presents one, is not an instruction to grant a broad exception.

## Measurement

One IPv4 socket on the OS-selected route is used throughout:

- `stun.nextcloud.com:443` supplies the baseline public mapping and, if supported, OTHER-ADDRESS.
- RFC 5780 CHANGE-REQUEST with only `change-port` requests a reply from the **same server IP** on
  the alternate port, before SockTuner has sent anything to that port. The request is retried once.
- If that reply is missing, SockTuner contacts the alternate port and repeats the changed-source
  request as a positive control. Only **missing before / received after**, with matching public
  mappings, supports a port-dependent filtering observation. An ignored or unsupported request,
  changing mapping or failed control stays unsupported/inconclusive. Packet loss cannot be excluded.
- `stun.cloudflare.com:3478` and `stun.l.google.com:19302` supply further destination mappings from the
  same local socket (the Google one is skipped if unreachable). A final baseline request checks for a
  changed mapping over time. Results are limited to the sampled
  destinations; different mappings are not automatically labelled "symmetric NAT".
- Replies must have the expected source address and port, success message type, magic cookie,
  transaction ID, valid declared lengths and an IPv4 mapped endpoint. Malformed or unrelated packets
  are ignored within a bounded timeout. Server-advertised addresses are not arbitrary probe targets:
  the alternate test remains on the primary server's resolved IP.

These public services have no availability guarantee. The first service may stop advertising the
required behavior extension. A timeout or unsupported server **does not mean the game port is closed**.
Both providers can see the public IP and source port. No game packets, credentials or reports are sent.
The UI displays the public endpoint locally; this separate result is not added to saved/exported
latency diagnostic reports.

## Gamer reading

The headline maps the measured dimensions onto console-style wording (`NatGamerReading`): a changing
public port is **Strict-like**, a stable port with replies only after contact **Moderate-like**, a
stable port with replies from the alternate port **Open-like**. It concerns peer-to-peer, party chat
and hosting; client-to-dedicated-server play does not depend on it, which is why a "Strict" label
elsewhere often does not match what happens toward a game server. All probes share one local socket,
so a changing port is a real per-destination mapping, not the artefact of one socket per probe that
browser (WebRTC) checkers produce.

## Test with a friend (optional)

The STUN test above stays as it is; the friend test is a second, separate choice. Two people run
SockTuner on the same local port (for example the game's) and exchange one text code each by chat.
A code holds the sender's public IPv4 endpoint, a short secret that only filters stray packets (it is
not encryption) and the UTC start time. It is parsed as untrusted input: private, loopback, CGNAT,
link-local and multicast targets, bad checksums and expired codes are rejected.

- Round 1: the guest sends, the host only listens. Round 2: the reverse. Round 3: both send from the
  shared port. Rounds last 20 s with 5 s gaps; senders use fixed 16-byte packets, five per second.
- In rounds 1 and 2 the sender uses a throw-away socket on another port, so the listener's socket has
  never sent anything to that peer: an arrival is a cold inbound packet from a new IP and port, which
  is what a game's own server-side check looks for. Round 3 shows whether two routers can connect
  once both sides send first.
- Each side gets a verdict for its own router: cold packet received is Open-like, only round 3 is
  Moderate-like, nothing received is Strict-like *or* blocked inbound by Windows Firewall or security
  software (the two cannot be told apart, and the result says so). The same public IP in both codes
  gives no verdict. The NAT mapping is kept alive until the start; if it moves, the code is void.
- Both people see each other's public IP and the test port. Codes are never stored, logged or exported.
  Nothing is sent except those packets, only to the address in the code, for about 75 s after the start.
  It tests one remote IP; another peer's router or a game's own rules may still differ.

## What is deliberately not claimed

- Receiving an alternate-port reply does **not** prove Full Cone NAT: another source **IP** is not
  tested. This implementation reports dimensions, not the historical cone-NAT taxonomy.
- A public STUN mapping does not exclude CGNAT or double NAT. Compare the router's actual WAN IPv4;
  private addresses in traceroute alone cannot establish either condition.
- Windows rules apply to SockTuner's executable, not the game's. VPN policies, other devices,
  destination rules and game protocols may differ even on the same local port.
- The STUN test has no independent unsolicited probe from another IP, so **game inbound reachability
  stays not verified** by it. The optional friend test below does measure it, for one remote IP.
  No green/red global NAT score is assigned.
- Manual forwarding can make a game's port reachable while a browser's temporary port is filtered.
  Conversely, a successful SockTuner probe does not prove a game can accept connections.
- UPnP and Full Cone NAT are conditional alternatives, not universally ordered security levels.
  DMZ and disabling firewalls are not recommended to improve a badge.

## References and maintenance

The small offline preset list records publisher/platform sources in `GamePortPresets` and shows its
review date. It has no runtime downloads or automatic trust in a community port database. Review
publisher changes before extending it; document platform, local-versus-remote ambiguity and hosting
scope. Current references include Activision's game-port table, Microsoft's Bedrock server properties
and Valve's CS2 dedicated-server documentation. No preset certifies which ports a running game uses.

Protocol references: [RFC 8489](https://www.rfc-editor.org/rfc/rfc8489),
[RFC 5780](https://www.rfc-editor.org/rfc/rfc5780),
[RFC 4787](https://www.rfc-editor.org/rfc/rfc4787).
[RFC 5389 section 19](https://www.rfc-editor.org/rfc/rfc5389#section-19) documents the limitations
of the old universal NAT-type discovery model.

## Verification

`NatDiagnosticProbeTests` uses local UDP peers, not the Internet: alternate-port acceptance/filtering,
ignored change requests, missing extension, changed mapping, absent baseline, exclusive binding,
cancellation, invalid ports, request encoding and malformed/mismatched replies. Normal tests do not
contact public STUN services. Test the UI live only with explicit operator consent and compare before
and after without automatically applying any router or firewall change.
