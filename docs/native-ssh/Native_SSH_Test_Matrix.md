# Native SSH Test Matrix

Date: 2026-04-08

## Automated Verification

Executed in this repo on the native SSH dynamic forwarding branch:

- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Ssh" /nodeReuse:false`
  Result: PASS, 71/71
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~Ssh" /nodeReuse:false`
  Result: PASS, 76/76
- `cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml --release`
  Result: PASS
- `dotnet build src/NovaTerminal.App/NovaTerminal.App.csproj -c Release -p:SKIP_RUST_NATIVE_BUILD=1`
  Result: PASS

## Coverage Summary

Verified by automated tests:

- SSH profile persistence and backend round-tripping
- native host-key trust, trusted reconnect, and changed-host-key handling
- native SSH interaction prompts for password, passphrase, and keyboard-interactive auth
- native local port-forward listener lifecycle and teardown
- native dynamic port-forward SOCKS5 `CONNECT` lifecycle and teardown for direct-host sessions
- jump-host planning for single hops and multi-hop chains, preserving chain order
- native rollout gating and failure classification
- native session input, resize, output decoding, and exit behavior
- Dockerized native SSH connect/auth, command execution, alternate-screen recovery, resize-burst recovery, and `vim` downward-scroll behavior
- Dockerized private-key auth (unencrypted and passphrase-protected) and keyboard-interactive auth
  against a server that refuses every other method for that user
- Dockerized ssh-agent auth: a real ssh-agent started by the test holds the only copy of the
  key, and the session must authenticate without a single credential prompt
- Agent discovery and identity listing against russh's own agent server in-process (Rust unit
  tests), including the no-agent state resolving to fall-through rather than error
- Dockerized host-key reporting: the algorithm and fingerprint the backend surfaces are compared
  against what `ssh-keygen` says the server's key is, and a refused key is proven to stop the
  connection before any credential is solicited
- Dockerized local and dynamic (SOCKS5) forwarding carrying real bytes to an in-container echo
  service, rather than only opening a channel
- Dockerized jump-host tunnelling: a one-hop session, a two-hop chain running a live command, and
  dynamic forwarding through a hop — each hop dialling the fixture container back into itself, so
  every hop is a real nested SSH session without a multi-container fixture
- Dockerized remote forwarding: a tcpip-forward listener on the server, dialled from inside the
  container by the session's own shell, carrying real bytes to a local destination and back

## Manual Matrix

Rows marked Automated run in the `Native SSH Docker E2E` CI job (Linux), which sets
`NOVATERM_ENABLE_DOCKER_E2E=1`. Everything else still needs a human against a real endpoint.

| Area | Scenario | Status | Notes |
| --- | --- | --- | --- |
| OpenSSH parity | Existing OpenSSH backend connects exactly as before | Pending manual | No code path fallback was added; validate with a known-good host |
| Native auth | Password auth | Pending manual | Dialog path is automated, endpoint still needs live verification |
| Native auth | Private key auth | Automated | `NativeSshDockerAuthE2eTests.PrivateKeyAuth_WithUnencryptedKey_...` |
| Native auth | SSH agent auth | Automated | `NativeSshDockerAgentAuthE2eTests.AgentAuth_WithTheKeyHeldOnlyByTheAgent_...`; the key is held only by a real ssh-agent the test starts, so a password prompt would fail the assertion |
| Native auth | Encrypted private key auth | Automated | `NativeSshDockerAuthE2eTests.PrivateKeyAuth_WithEncryptedKey_...` |
| Native auth | Keyboard-interactive auth | Automated | `NativeSshDockerAuthE2eTests.KeyboardInteractiveAuth_...`; the image's `kbdnova` user refuses password and pubkey |
| Host keys | First trust flow | Automated + pending manual | `HostKeyPrompt_CarriesTheAlgorithmAndFingerprintTheServerActuallyHas` covers the reported key; dialog copy is still a UI check |
| Host keys | Trusted reconnect | Pending manual | Confirm no dialog after trust is recorded |
| Host keys | Changed key path | Automated + pending manual | Store logic is unit-tested and `HostKeyPrompt_WhenRefused_NeverReachesTheCredentialPrompt` pins fail-closed; warning copy is still a UI check |
| Terminal behavior | Resize handling | Pending manual | Verify shell survives repeated resize |
| Terminal behavior | Fullscreen/alt-screen TUI | Automated + pending manual | Dockerized native SSH validates alternate-screen recovery and `vim` downward scrolling; still validate against a real host |
| Forwarding | One local forward | Automated | `LocalForward_CarriesRealBytesToTheEchoService` |
| Forwarding | One direct-host dynamic forward | Automated | `DynamicForward_CarriesRealBytesThroughSocks5ToTheEchoService` |
| Forwarding | Remote forward | Automated | `NativeSshDockerRemoteForwardE2eTests.RemoteForward_CarriesRealBytesFromTheServerToALocalDestination`; the reply is transformed by the local destination so the assertion cannot match the echoed command |
| Forwarding | One-hop jump-host dynamic forward | Automated | `NativeSshDockerJumpChainE2eTests.DynamicForward_ThroughAJumpHop_...`; forward channels ride the target session regardless of how it was reached |
| Jump host | One-hop jump host | Automated + pending manual | `NativeSshDockerJumpChainE2eTests.JumpHost_OneHop_...` (the hop dials the fixture container back into itself); still validate against a real bastion |
| Jump host | Multi-hop jump chain | Automated + pending manual | `NativeSshDockerJumpChainE2eTests.JumpChain_TwoHops_...` runs a live command through two nested tunnels; still validate against real distinct bastions |
| Rollback | Broken native profile switched back to OpenSSH | Pending manual | Confirm backend selector flow is obvious and safe |

## Rollout Notes

- The Dockerized suite runs in CI as the `Native SSH Docker E2E` job (Linux, blocking).
  Before that job existed the suite was `[DockerFact]`-gated on `NOVATERM_ENABLE_DOCKER_E2E`
  and nothing in CI set it, so it was written and then never executed — the reason auth and
  forwarding rows above stayed "pending manual" while a Dockerized suite sat beside them.
- Native SSH remains opt-in through `TerminalSettings.ExperimentalNativeSshEnabled`,
  toggleable in the app under Settings > SSH.
- `OpenSsh` remains the default backend for new profiles.
- Native backend refusal is explicit when the global experimental toggle is disabled.
- `NativeSshCapability` has one refusal today: a remote forward with source port 0
  (a server-allocated listen port), which the backend cannot yet match back to a
  rule. The gate and every call site (profile editor at save time, factory and
  session at connect time) stay wired, so any shape the backend cannot serve gets
  one refusal reaching all of them at once.
- Forward-channel data reaching the local socket is queued per channel and written
  by a dedicated pump, in both the managed and Rust layers, so a forwarded port
  whose peer stops reading can no longer stall the session's terminal I/O
  (issue #173 item 2).
- The native event queue itself is bounded (issue #173 item 1): data-bearing
  events (terminal output, forward-channel data) share a 4 MiB budget — each
  event charged its payload plus a flat per-event surcharge, so zero-length
  data frames (which consume no SSH window and are never throttled by flow
  control) cannot grow the queue's overhead unbounded either — and
  at the budget the channel readers park instead of reading on — an unread russh
  channel stops having its window replenished, so SSH flow control makes the
  remote hold the stream. A `cat bigfile` against a stalled poll loop now caps
  out at the budget plus one in-flight window instead of buffering the whole
  stream. Control events (prompts, exit, close, forward open/EOF/close notices)
  are exempt, so a full queue can never hold back the events that let the
  managed side notice and act.
- Both directions are bounded at 1 MB per forward channel, but they resolve
  overflow differently, because only one of them has anywhere to push back to:
  - **Remote to local** (managed queue): over budget, the channel is closed.
    Draining slower is not an option — the source is the shared SSH poll loop, and
    stalling it is the bug being fixed.
  - **Local to remote** (native queue): over budget,
    `nova_ssh_channel_write` returns `NOVA_SSH_RESULT_WOULD_BLOCK` and the managed
    pump retries. That stops it reading the local socket, so TCP flow control
    throttles the local peer and nothing is dropped or closed for slowness alone.
    `INativeSshInterop.TryWriteChannel` is the managed half of that contract.
- Native backend supports local and dynamic forwarding, on direct connections and through
  jump hops alike.
- Native backend supports jump chains of any length: one nested direct-tcpip hop per entry,
  ordered client → target, each hop with its own host-key verification and authentication.
  The chain crosses the FFI as a JSON array (`jump_hops_json` / the SFTP request's `jumpHops`),
  so chain length never renegotiates the ABI.
- Remote forwarding is supported natively: the backend sends a `tcpip-forward`
  global request per remote rule once the session is established (on the
  Connected event, sequentially on one task — no thread-pool worker waits out
  the handshake per rule), and each connection arriving on the server's listener
  rides the same forward-channel machinery (queues, pumps, budgets) as the
  outgoing kinds — the announcement event registers the channel before its first
  data event can be seen, so no bytes are lost while the local destination is
  being dialled. A request the server refuses is loud but not fatal, matching
  `ssh -R`: the session survives and a warning naming the listener is printed
  into the terminal, not only the log.
- Unsolicited `forwarded-tcpip` opens are refused at the Rust handler: until the
  session has sent at least one `tcpip-forward` request, a server-opened forward
  channel is closed without being registered, so a hostile server cannot park
  unbounded channels on a session that configured no forwards. The managed poll
  loop also closes an announced channel when no forward session exists, as a
  second line of defense.
- Duplicate remote rules asking for the same `(bind address, port)` listener are
  refused deterministically: the first rule wins, the duplicate is named in a
  terminal warning, and incoming connections are matched by `(address, port)`
  with a port-only fallback taken only when it cannot choose wrongly.
