# Native SSH Dynamic Forwarding Design

Date: 2026-04-08

## Goal

Add native SSH support for dynamic port forwarding (`SOCKS` proxy mode) for direct-host native sessions, without changing the existing SSH profile model or terminal rendering paths.

## Current State

- The SSH profile model already supports `PortForwardKind.Dynamic`.
- OpenSSH already supports dynamic forwarding through argument/config generation.
- Native SSH explicitly rejects any non-local forward in [NativeSshSession.cs](/d:/projects/nova2/src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs) and [NativePortForwardSession.cs](/d:/projects/nova2/src/NovaTerminal.Core/Ssh/Native/NativePortForwardSession.cs).
- The native Rust crate already supports opening `direct-tcpip` channels, which is sufficient for SOCKS `CONNECT`.

## Chosen Approach

Implement the SOCKS listener in C# inside the existing native forwarding subsystem and reuse the current Rust `direct-tcpip` channel support.

Why this approach:

- It is additive and fits the existing architecture.
- Listener lifecycle already lives in C# for native local forwarding.
- The Rust ABI does not need a new SOCKS-specific surface.
- The terminal/session core remains unchanged.

## Scope

Included in v1:

- Native dynamic forwarding for direct-host native SSH sessions
- SOCKS5 `CONNECT`
- IPv4, IPv6, and domain-name target parsing as supported by the SOCKS request
- Clean teardown, bind failure handling, and deterministic tests

Explicitly excluded from v1:

- SOCKS4
- SOCKS5 `BIND`
- SOCKS5 `UDP ASSOCIATE`
- Dynamic forwarding through native jump hosts

## Follow-Up To Preserve

Add a follow-up roadmap/test-matrix note for:

- native dynamic forwarding through a one-hop jump host

That should be tracked explicitly so it is not lost after the first direct-host implementation lands.

## Architecture

### Session Layer

[NativeSshSession.cs](/d:/projects/nova2/src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs) should stop rejecting `Dynamic` forwards. It should continue to reject unsupported native forward kinds, which after this change will only be `Remote`.

### Forwarding Layer

[NativePortForwardSession.cs](/d:/projects/nova2/src/NovaTerminal.Core/Ssh/Native/NativePortForwardSession.cs) becomes the main host for both native forward types:

- `Local`: current fixed-destination listener behavior
- `Dynamic`: a local SOCKS server that negotiates the target per client connection

The forwarding layer should remain C#-owned because it already owns:

- local bind/listener lifecycle
- socket accept loops
- channel-to-socket byte pumping
- shutdown and cleanup policy

### Rust Layer

[lib.rs](/d:/projects/nova2/src/NovaTerminal.App/native/rusty_ssh/src/lib.rs) should stay focused on SSH transport behavior:

- open `direct-tcpip`
- stream bytes
- surface close/eof/data events

No new SOCKS-specific ABI is required for v1.

## SOCKS Behavior

Implement only SOCKS5 `CONNECT`.

Connection flow:

1. Accept local TCP client on the configured bind address and source port
2. Read SOCKS5 greeting and choose `NO AUTHENTICATION REQUIRED`
3. Read SOCKS5 request
4. Validate command is `CONNECT`
5. Resolve target host/port from IPv4, IPv6, or domain-name form
6. Open a native `direct-tcpip` channel to the requested target
7. Return SOCKS success reply
8. Reuse the existing bidirectional byte pump model

Unsupported or invalid requests should be rejected with a protocol-correct SOCKS failure reply before the client socket is closed.

## Error Handling

Bind/setup policy should match the current native local-forward policy:

- if any enabled forward fails to bind during startup, fail the native session setup

Runtime failures should be logged clearly and closed cleanly:

- invalid SOCKS greeting
- unsupported auth method
- unsupported SOCKS command
- malformed destination payload
- SSH `direct-tcpip` channel open failure

## Testing Strategy

Primary test coverage should stay in C#:

- session accepts native dynamic forwards
- SOCKS5 greeting + request produces expected `OpenDirectTcpIp`
- unsupported SOCKS command is rejected deterministically
- malformed handshake does not crash the listener
- dispose tears down listeners and channels
- local and dynamic forwards can coexist in one native profile

Rust tests should remain minimal unless the implementation forces an interop change.

## Docs Impact

Update these after implementation:

- [Native_SSH_Test_Matrix.md](/d:/projects/nova2/docs/native-ssh/Native_SSH_Test_Matrix.md)
- [SSH_ROADMAP.md](/d:/projects/nova2/docs/SSH_ROADMAP.md)

The docs should state:

- native dynamic forwarding is supported for direct-host native sessions
- native dynamic forwarding through one-hop jump hosts remains a follow-up item
