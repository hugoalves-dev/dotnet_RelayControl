# ADR 0009: LAN signaling between SonicRelay clients without RelayControl

- Status: Accepted
- Date: 2026-11-17
- Issues: RelayControl #40 (architecture), desktop_dotnet_SonicRelay #75 (implementation)

## Context

Every SonicRelay session used to depend on RelayControl for discovery, session creation and
signaling. A publisher and a viewer on the same desk could not stream when the internet, the API
or coturn was down. Issue #40 approved a LAN/intranet mode; this ADR records that decision and the
refinements made while implementing it (#75), including the owner's priority order.

## Decision

Discovery, signaling transport and media/ICE policy are **separate decisions**:

| Concern | Options, in priority order |
| --- | --- |
| Discovery | mDNS/DNS-SD on the link (or through a reflector / unicast DNS-SD) → QR code or typed `address:port` + code → remembered endpoints of a paired desktop |
| Signaling | The desktop's own authenticated TLS host (`/lan/v1/signaling`) → RelayControl's existing WebSocket |
| Media | WebRTC/Opus P2P with host candidates → (cloud-signaled only) srflx → TURN as last resort |

Each client has a local `connectivityMode`:

- `auto`: LAN first for paired desktops; RelayControl otherwise, decided **before** the session
  starts. A live session is never migrated between transports.
- `lanOnly`: RelayControl, STUN and TURN are never contacted. LAN-signaled peers use
  `iceServers: []`.
- `cloudOnly`: the previous behaviour.

RelayControl stays a control plane (ADR 0001): it is not contacted in LAN sessions and never
becomes a media relay. When it carries the signaling, ICE still prefers a direct pair, so audio
between two LAN devices stays local even when the signaling went through the internet.

The desktop hosts LAN signaling and plays the backend's session roles for LAN viewers
(`session.joined`, authoritative `participant.capabilities`, `from` stamping, reconnect grace,
`session.ended`). In `auto` a desktop session is dual-homed: the same session id is reachable
through RelayControl and the LAN host.

### Security model

- mDNS/DNS-SD is discovery only. TXT records carry `v=1` and a non-identifying 6-hex hint — no
  name, pin or credential.
- The desktop identity is a self-signed ECDSA P-256 certificate. Phones pin SHA-256 of its DER,
  learned from the QR code or (for a typed code) a 40-bit prefix verified during the 120 s window.
- Pairing secrets are single-use, expire in 120 s, close after 5 failures, are compared in constant
  time and rate-limited per address.
- Pairing issues a per-device 256-bit LAN credential; the desktop stores only its hash. The API
  bearer token is never reused as a peer credential, and there is no custom cryptography.
- Trust is revocable locally. A revocation made in the cloud cannot reach a desktop while the
  devices are offline; users revoke locally as well.

## Consequences

- RelayControl needs no runtime change. The protocol is specified in
  [`protocol/lan-signaling-v1.md`](../protocol/lan-signaling-v1.md) and implemented by both clients.
- **Negative case:** remote signaling cannot bypass a missing IP route or blocked UDP between the
  devices. Then TURN (cloud) or a network change is required; clients must report it
  (`ice.no_direct_route`) rather than claim a P2P path.
- mDNS does not cross routers. Routed subnets rely on QR/typed bootstrap, an mDNS reflector, or
  operator-published unicast DNS-SD records.
- The desktop opens an inbound listener as the current user (no elevation, no firewall changes);
  the OS may prompt once.

## Rejected alternatives

- **A local copy of the API/database on the desktop:** duplicates the domain and its migrations.
- **Four separate WebRTC session types, one per priority level:** the levels differ in discovery
  and signaling only; media is the same WebRTC session.
- **Mutual TLS for phones:** generating and storing client certificates in Dart on Android/iOS is
  fragile; pinned server TLS plus a revocable per-device credential gives the same guarantees.
- **`HttpListener` on Windows:** HTTPS requires admin URL ACL and certificate binding.
