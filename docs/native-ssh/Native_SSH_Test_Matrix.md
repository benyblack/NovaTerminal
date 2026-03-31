# Native SSH Test Matrix

Date: 2026-03-31

## Automated Verification

Executed in this repo on the native SSH completion branch:

- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Ssh" /nodeReuse:false`
  Result: PASS, 52/52
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~Ssh" /nodeReuse:false`
  Result: PASS, 45/45
- `cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml --release`
  Result: PASS
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~PtySmoke|FullyQualifiedName~SessionAuthSurfaceTests" /nodeReuse:false`
  Result: PASS, 2/2

## Coverage Summary

Verified by automated tests:

- SSH profile persistence and backend round-tripping
- native host-key trust, trusted reconnect, and changed-host-key handling
- native SSH interaction prompts for password, passphrase, and keyboard-interactive auth
- native local port-forward listener lifecycle and teardown
- one-hop jump-host planning and explicit multi-hop rejection
- native rollout gating and failure classification
- native session input, resize, output decoding, and exit behavior

## Manual Matrix

The following checks still require manual validation against real SSH endpoints.

| Area | Scenario | Status | Notes |
| --- | --- | --- | --- |
| OpenSSH parity | Existing OpenSSH backend connects exactly as before | Pending manual | No code path fallback was added; validate with a known-good host |
| Native auth | Password auth | Pending manual | Dialog path is automated, endpoint still needs live verification |
| Native auth | Private key auth | Pending manual | Test with an unencrypted key |
| Native auth | Encrypted private key auth | Pending manual | Test passphrase prompt path with a real key |
| Native auth | Keyboard-interactive auth | Pending manual | Test against a host that requires challenge-response |
| Host keys | First trust flow | Pending manual | Confirm dialog copy and persistence path |
| Host keys | Trusted reconnect | Pending manual | Confirm no dialog after trust is recorded |
| Host keys | Changed key path | Pending manual | Confirm changed-host-key warning copy and replacement trust |
| Terminal behavior | Resize handling | Pending manual | Verify shell survives repeated resize |
| Terminal behavior | Fullscreen/alt-screen TUI | Pending manual | Validate with a real TUI workload |
| Forwarding | One local forward | Pending manual | Validate actual traffic through the forwarded port |
| Jump host | One-hop jump host | Pending manual | Validate end-to-end session through a real bastion |
| Rollback | Broken native profile switched back to OpenSSH | Pending manual | Confirm backend selector flow is obvious and safe |

## Rollout Notes

- Native SSH remains opt-in through `TerminalSettings.ExperimentalNativeSshEnabled`.
- `OpenSsh` remains the default backend for new profiles.
- Native backend refusal is explicit when the global experimental toggle is disabled.
- Multi-hop jump hosts are intentionally unsupported in the native backend at this stage.
