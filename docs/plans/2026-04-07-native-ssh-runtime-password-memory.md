# Native SSH Runtime Password Memory Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the profile-gated native SSH password-memory UX with a runtime-only flow: native password prompts always offer `Remember password`, saved passwords are reused automatically, and unchecked submits never delete an existing saved password.

**Architecture:** Keep secure storage in `VaultService` and runtime decision-making in `SshInteractionService`. Remove the editor-level remember-password affordance from the Avalonia SSH editor. Keep terminal core unaware of vault details, aside from passing enough native profile identity for app-layer lookup and save operations.

**Tech Stack:** C#, Avalonia UI, `System.Text.Json`, existing `VaultService`, xUnit, Avalonia.Headless.XUnit

---

### Task 1: Remove the profile-level remember-password UI

**Files:**
- Modify: `src/NovaTerminal.App/Views/Ssh/NewSshConnectionView.axaml`
- Modify: `src/NovaTerminal.App/ViewModels/Ssh/NewSshConnectionViewModel.cs`
- Modify: `tests/NovaTerminal.Tests/Ssh/NewSshConnectionViewModelTests.cs`

**Step 1: Write the failing test**

Add tests that assert:
- the SSH editor no longer exposes the native-only remember-password state
- switching back and forth between backends no longer depends on a remember-password VM flag

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~NewSshConnectionViewModelTests"`

Expected: FAIL because the editor still exposes the profile-level remember-password option.

**Step 3: Write minimal implementation**

Remove:
- the remember-password checkbox from the SSH editor view
- the editor VM property and native-only visibility logic that only existed to surface this checkbox

Preserve the rest of the auth/backend editor behavior.

**Step 4: Run test to verify it passes**

Run the same command and confirm the new tests pass.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Views/Ssh/NewSshConnectionView.axaml src/NovaTerminal.App/ViewModels/Ssh/NewSshConnectionViewModel.cs tests/NovaTerminal.Tests/Ssh/NewSshConnectionViewModelTests.cs
git commit -m "Remove native SSH password memory editor toggle"
```

### Task 2: Make native password prompts always offer remember-password

**Files:**
- Modify: `src/NovaTerminal.App/Services/Ssh/SshInteractionService.cs`
- Modify: `src/NovaTerminal.App/ViewModels/Ssh/AuthPromptViewModel.cs`
- Modify: `src/NovaTerminal.App/Views/Ssh/AuthPromptDialog.axaml`
- Modify: `src/NovaTerminal.App/Views/Ssh/AuthPromptDialog.axaml.cs`
- Modify: `tests/NovaTerminal.Tests/Ssh/SshInteractionServiceTests.cs`

**Step 1: Write the failing test**

Add tests that assert:
- a native password prompt shows `Remember password` whenever the dialog is shown
- passphrase prompts still do not surface the remember-password control

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~SshInteractionServiceTests"`

Expected: FAIL because the dialog is still gated by the old profile preference.

**Step 3: Write minimal implementation**

Change the interaction flow so:
- native password prompts with profile identity enable remember-password in the dialog by default
- passphrase and keyboard-interactive flows remain unchanged

Keep the UI logic explicit and app-layer only.

**Step 4: Run test to verify it passes**

Run the same command and confirm the new tests pass.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Services/Ssh/SshInteractionService.cs src/NovaTerminal.App/ViewModels/Ssh/AuthPromptViewModel.cs src/NovaTerminal.App/Views/Ssh/AuthPromptDialog.axaml src/NovaTerminal.App/Views/Ssh/AuthPromptDialog.axaml.cs tests/NovaTerminal.Tests/Ssh/SshInteractionServiceTests.cs
git commit -m "Always offer remember-password for native SSH prompts"
```

### Task 3: Remove the old profile gate from vault reuse

**Files:**
- Modify: `src/NovaTerminal.App/Services/Ssh/SshInteractionService.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs`
- Modify: `tests/NovaTerminal.Tests/Ssh/SshInteractionServiceTests.cs`
- Modify: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshSessionInteractionTests.cs`

**Step 1: Write the failing test**

Add tests that assert:
- a saved native password is reused automatically even when no profile remember-password flag is set
- native password reuse still depends on profile identity and prompt type, not on OpenSSH or passphrase flows

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~SshInteractionServiceTests"`  
Run: `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshSessionInteractionTests"`

Expected: FAIL because runtime reuse is still gated by `RememberPasswordInVault`.

**Step 3: Write minimal implementation**

Refactor the native auth interaction path so vault lookup is driven by:
- native backend
- password prompt kind
- available profile identity

Do not require a persisted remember-password preference.

**Step 4: Run test to verify it passes**

Run the same commands and confirm the new tests pass.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Services/Ssh/SshInteractionService.cs src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs tests/NovaTerminal.Tests/Ssh/SshInteractionServiceTests.cs tests/NovaTerminal.Core.Tests/Ssh/NativeSshSessionInteractionTests.cs
git commit -m "Make native SSH password reuse runtime-driven"
```

### Task 4: Preserve saved passwords when remember is left unchecked

**Files:**
- Modify: `src/NovaTerminal.App/Services/Ssh/SshInteractionService.cs`
- Modify: `tests/NovaTerminal.Tests/Ssh/SshInteractionServiceTests.cs`
- Modify: `tests/NovaTerminal.Tests/Core/VaultServiceSshKeyTests.cs`

**Step 1: Write the failing test**

Add tests that assert:
- if a saved password already exists and the dialog is shown later, submitting with remember unchecked does not delete the saved password
- only an explicit save operation updates vault contents

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~SshInteractionServiceTests|FullyQualifiedName~VaultServiceSshKeyTests"`

Expected: FAIL because current cleanup logic may still treat unchecked remember as opt-out.

**Step 3: Write minimal implementation**

Update the app-layer save path so:
- checked remember stores/updates the password
- unchecked remember leaves existing vault contents untouched

Avoid adding implicit vault-cleanup behavior to transient auth prompts.

**Step 4: Run test to verify it passes**

Run the same command and confirm the tests pass.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Services/Ssh/SshInteractionService.cs tests/NovaTerminal.Tests/Ssh/SshInteractionServiceTests.cs tests/NovaTerminal.Tests/Core/VaultServiceSshKeyTests.cs
git commit -m "Preserve saved native SSH passwords on unchecked prompts"
```

### Task 5: Reconcile persisted profile state and backward compatibility

**Files:**
- Modify: `src/NovaTerminal.Core/Ssh/Models/SshProfile.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Models/SshProfileNormalizer.cs`
- Modify: `src/NovaTerminal.App/Services/Ssh/SshConnectionService.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Storage/JsonSshProfileStore.cs`
- Modify: `tests/NovaTerminal.Core.Tests/Ssh/JsonSshProfileStoreTests.cs`
- Modify: `tests/NovaTerminal.Tests/Ssh/SshConnectionServiceTests.cs`

**Step 1: Write the failing test**

Add tests that assert:
- older profiles carrying `RememberPasswordInVault` still deserialize safely
- new runtime behavior does not depend on that field
- editor save/load no longer needs to round-trip the old preference as active UX state

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~JsonSshProfileStoreTests"`  
Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~SshConnectionServiceTests"`

Expected: FAIL because persistence and editor-service mappings still assume the old preference is active.

**Step 3: Write minimal implementation**

Choose the lightest safe compatibility approach:
- either keep `RememberPasswordInVault` as ignored legacy state
- or stop emitting it while still tolerating it on read

Do not break existing saved profiles.

**Step 4: Run test to verify it passes**

Run the same commands and confirm the new behavior passes.

**Step 5: Commit**

```bash
git add src/NovaTerminal.Core/Ssh/Models/SshProfile.cs src/NovaTerminal.Core/Ssh/Models/SshProfileNormalizer.cs src/NovaTerminal.App/Services/Ssh/SshConnectionService.cs src/NovaTerminal.Core/Ssh/Storage/JsonSshProfileStore.cs tests/NovaTerminal.Core.Tests/Ssh/JsonSshProfileStoreTests.cs tests/NovaTerminal.Tests/Ssh/SshConnectionServiceTests.cs
git commit -m "Decouple native SSH password memory from profile settings"
```

### Task 6: Final verification

**Files:**
- No new product files

**Step 1: Run focused automated verification**

Run:

```bash
dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Ssh"
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~SshInteractionServiceTests|FullyQualifiedName~SshConnectionServiceTests|FullyQualifiedName~NewSshConnectionViewModelTests|FullyQualifiedName~VaultServiceSshKeyTests"
dotnet build src/NovaTerminal.App/NovaTerminal.App.csproj -c Release -p:SKIP_RUST_NATIVE_BUILD=1
```

Expected: PASS

**Step 2: Manual verification**

Verify:
- native password prompt shows `Remember password` without any profile preconfiguration
- checking it stores the password and later connections skip the dialog
- leaving it unchecked does not remove an already-saved password
- OpenSSH and passphrase flows are unchanged

**Step 3: Commit**

```bash
git add .
git commit -m "Finalize runtime-only native SSH password memory"
```
