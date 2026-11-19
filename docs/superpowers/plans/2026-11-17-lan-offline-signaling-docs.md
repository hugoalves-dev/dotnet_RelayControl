# LAN / Offline Signaling (RelayControl docs) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record the LAN/offline decision that #40 approved but never committed, and publish the canonical LAN signaling protocol both clients implement.

**Architecture:** Documentation only. RelayControl's runtime does not change: in LAN mode it is not contacted; in Auto/Cloud it keeps signaling as today and never becomes a media relay.

**Tech Stack:** Markdown.

**Spec:** desktop_dotnet_SonicRelay `docs/superpowers/specs/2026-11-17-lan-offline-signaling-design.md`

## Global Constraints

- Values (routes, headers, close codes, TTLs, sizes, QR v2 shape, manual code layout, mDNS records) copied verbatim from the spec; the protocol doc is the single source both clients link to.

## Review Focus

1. The ADR must state the negative case: remote signaling cannot bypass a missing IP route or blocked UDP.
2. The protocol must state which side stamps `from` (the LAN host) and that viewer-to-viewer messages are dropped.
3. The cloud-revocation-offline limit is documented.
4. ADR index/links (`docs/architecture.md`, `docs/protocol.md`) point to the new files.
5. Naming follows `docs/naming.md` (`SonicRelay`).

---

### Task 1: ADR 0009 and LAN protocol v1

**Files:**
- Create: `docs/adr/0009-lan-offline-signaling.md` (context, decision: discovery/signaling/media separated, priority order, Auto/LAN only/Cloud only, RelayControl stays control-plane-only, security model, consequences, rejected alternatives)
- Create: `docs/protocol/lan-signaling-v1.md` (routes, payloads, headers, close codes, QR v2, manual code, mDNS/DNS-SD records incl. unicast DNS-SD operator records, host emulated messages, limits, security properties)
- Modify: `docs/architecture.md`, `docs/protocol.md` (links)

- [ ] Step 1: write both documents from the spec.
- [ ] Step 2: verify every relative link resolves (`rg -o "\]\(([^)]+\.md)" docs` and check each file exists).
- [ ] Step 3: commit `docs: ADR 0009 and LAN signaling protocol v1 (SonicRelay #75)`.
