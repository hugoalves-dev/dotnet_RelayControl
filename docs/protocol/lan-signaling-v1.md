# LAN signaling protocol v1

Status: implemented by SonicRelay Desktop (publisher, host) and SonicRelay Mobile (viewer, client).
Decision record: [ADR 0009](../adr/0009-lan-offline-signaling.md). RelayControl is not involved.

## Transport and identity

- HTTPS/WSS (TLS 1.2+) served by the desktop on an automatic port (remembered across runs) or a
  user-fixed port ≥ 1024, on every interface.
- Server certificate: self-signed ECDSA P-256, 5-year validity.
- **Pin** = base64url, no padding, of SHA-256 over the certificate DER. Clients accept no CA and
  no other certificate.
- Limits: 16 connections, 8 KiB request headers, 4 KiB pairing body, 64 KiB WebSocket message,
  10 s handshake. Five failed pairing/auth attempts per remote address per minute → `429` for 60 s.
- Only the two routes below exist; anything else is `404`. There is no unauthenticated info route.

## Pairing window and codes

The desktop opens one window at a time: `pid` (GUID), 80-bit `secret`, TTL **120 s**, single use,
closed after **5** failed attempts.

### QR payload v2

```json
{
  "v": 2,
  "cloud": { "challengeId": "<guid>", "code": "<cloud pairing code>" },
  "lan": {
    "pid": "<guid>",
    "sec": "<base64url 10 bytes>",
    "pin": "<base64url sha256(cert DER)>",
    "did": "<publisher device id, guid>",
    "name": "<desktop name>",
    "ep": ["192.168.1.20:47321", "[fd00::20]:47321", "desk.local:47321"]
  }
}
```

Either block may be absent, never both. `ep` lists at most 4 entries (`host:port`, IPv6 bracketed),
Ethernet first, VPN/tunnel last, then `<hostname>.local`. A payload without `v` is the legacy
cloud-only `{challengeId, code}`.

### Typed code

16 Crockford base32 characters shown as `XXXX-XXXX-XXXX-XXXX`, encoding 80 bits: the first 5 bytes
of the pin digest followed by the first 5 bytes of the secret. Decoding is case-insensitive,
ignores spaces and `-`, and maps `O→0`, `I/L→1`. The client accepts a certificate during pairing
only if its digest starts with the typed prefix, and stores the full pin returned by the desktop
only if it matches both the prefix and the certificate actually presented.

Test vector: pin `A5BYxvLAy0ksUzsKTRTvd8wPeKvMztUofYShogEc-4E` (SHA-256 of bytes `01 02 03`) and
secret `10 20 30 40 50 60 70 80 90 A0` → `0E85-HHQJ-20G3-0G2G`.

## `POST /lan/v1/pair`

Request (JSON): `{ pid, sec }` **or** `{ code }`, plus `viewerDeviceId` (GUID), `viewerName`,
`platform` (each ≤ 128 chars).

| Status | Body | Meaning |
| --- | --- | --- |
| 200 | `{ credential, publisherDeviceId, publisherName, pin }` | Paired. `credential` is 256-bit base64url; the desktop stores only SHA-256 of it. Re-pairing replaces the previous credential. |
| 400 | `{ "error": "pairing_invalid" }` | Wrong/reused secret or code, malformed body, no open window. |
| 410 | `{ "error": "pairing_expired" }` | Window older than 120 s. |
| 429 | `{ "error": "rate_limited" }` | Too many failures from this address. |

## `GET /lan/v1/signaling` (WebSocket)

Headers: `Authorization: SonicRelay-Lan <credential>`, `X-SonicRelay-Device: <viewerDeviceId>`.

Messages use the cloud signaling envelope `{ type, sessionId, to?, from?, payload? }` and the same
types (`viewer.ready`, `webrtc.offer|answer|ice_candidate|renegotiate`, `participant.*`,
`session.*`, `ping`/`pong`). The desktop hub plays the backend's roles:

- On connect it sends the viewer its own `session.joined` (no `from`) and the publisher's
  `session.joined` (`from` = publisher id, `role: publisher`); the viewer answers `viewer.ready`.
- It **stamps `from`** on every viewer message; a client-supplied `from` is ignored.
- Viewers may only send `viewer.ready`, `webrtc.*` addressed `to` the publisher, `participant.capabilities`,
  `participant.audio_state_changed` and `ping`. Anything addressed to another viewer, to another
  session, or of another type is dropped.
- It is the only authority for `participant.capabilities`: `audioSendAllowed` starts `false`
  and `canSendAudio` is clamped to `false` until the publisher grants it (duplex).
- A dropped socket emits `participant.disconnected`; reconnecting within **30 s** emits
  `participant.reconnected`, otherwise `session.left`.
- Ending the session sends `session.ended` then closes with `4410`.

| Close code | Meaning | Client action |
| --- | --- | --- |
| 4401 | Unknown device or wrong credential | Stop; pair again |
| 4403 | Revoked | Stop; pair again |
| 4409 | No live session, or full | Stop; report |
| 4410 | Session ended | Stop |
| 1009 | Message over 64 KiB | — |

Clients retry other closes at most 5 times, then report `lan.unreachable`.

## mDNS / DNS-SD

| Service | Announced by | TXT |
| --- | --- | --- |
| `_sonicrelay._tcp` (instance `SonicRelay-<hint>`, SRV port = host port) | Desktop | `v=1`, `id=<hint>` |
| `_sonicrelay-viewer._tcp` (instance `SonicRelay-<hint>`) | Phone, while in the foreground | `v=1`, `id=<hint>` |

`hint` = first 6 hex characters of SHA-256 over the UTF-8 device id. It only helps a client guess
which paired device it sees; identity is proven by the pinned TLS handshake.

Operators can make routed subnets discoverable with unicast DNS-SD:

```text
_sonicrelay._tcp.<domain>.                    PTR  SonicRelay-<hint>._sonicrelay._tcp.<domain>.
SonicRelay-<hint>._sonicrelay._tcp.<domain>.  SRV  0 0 <port> <desktop-host>.<domain>.
SonicRelay-<hint>._sonicrelay._tcp.<domain>.  TXT  "v=1" "id=<hint>"
```

## ICE

LAN-signaled peers use `iceServers: []` and `iceTransportPolicy: all` on both sides, in every
connectivity mode. If signaling succeeds but ICE fails, the media path is blocked (firewall, ACL,
client isolation) and clients report `ice.no_direct_route`.
