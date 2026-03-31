# Native SSH Password Memory Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add native-SSH-only password memory backed by `VaultService`, with profile-level opt-in and runtime prompt save support.

**Architecture:** Persist only a native-only preference flag on `SshProfile`, keep the actual password in `VaultService`, and resolve/save the secret from `SshInteractionService` during native password prompts. UI changes stay in the Avalonia SSH editor and auth dialog; terminal core remains unaware of secret storage details.

**Tech Stack:** C#, Avalonia UI, `System.Text.Json`, existing `VaultService`, xUnit, Avalonia.Headless.XUnit

---

### Task 1: Add failing tests for the profile preference flag

**Files:**
- Modify: `tests/NovaTerminal.Tests/Ssh/NewSshConnectionViewModelTests.cs`
- Modify: `tests/NovaTerminal.Tests/Ssh/SshConnectionServiceTests.cs`
- Modify: `src/NovaTerminal.App/ViewModels/Ssh/NewSshConnectionViewModel.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Models/SshProfile.cs`

**Step 1: Write the failing test**

Add tests that assert:
- a native profile can persist a `RememberPasswordInVault` preference
- an OpenSSH profile does not expose or preserve that preference through the editor flow

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~NewSshConnectionViewModelTests|FullyQualifiedName~SshConnectionServiceTests"`

Expected: FAIL because the preference does not exist yet.

**Step 3: Write minimal implementation**

Add:
- `RememberPasswordInVault` to `SshProfile`
- matching VM state in `NewSshConnectionViewModel`
- mapping in `ToSshProfile`, `ApplySshProfile`, and `SshConnectionService`

Keep the property gated logically to native profiles.

**Step 4: Run test to verify it passes**

Run the same command and confirm the new tests pass.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/Ssh/NewSshConnectionViewModelTests.cs tests/NovaTerminal.Tests/Ssh/SshConnectionServiceTests.cs src/NovaTerminal.App/ViewModels/Ssh/NewSshConnectionViewModel.cs src/NovaTerminal.Core/Ssh/Models/SshProfile.cs src/NovaTerminal.App/Services/Ssh/SshConnectionService.cs
git commit -m "Add native SSH remember-password profile flag"
```

### Task 2: Add the native-only editor UI

**Files:**
- Modify: `src/NovaTerminal.App/Views/Ssh/NewSshConnectionView.axaml`
- Modify: `src/NovaTerminal.App/Views/Ssh/NewSshConnectionView.axaml.cs`
- Modify: `src/NovaTerminal.App/ViewModels/Ssh/NewSshConnectionViewModel.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/NewSshConnectionViewModelTests.cs`

**Step 1: Write the failing test**

Add tests that assert:
- the native backend enables the remember-password checkbox state
- switching away from native clears or disables the remember-password option predictably

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~NewSshConnectionViewModelTests"`

Expected: FAIL because the backend gating does not exist yet.

**Step 3: Write minimal implementation**

Update the editor:
- add a checkbox under the `Auth` tab
- show it only for `BackendKind == Native`
- bind it to the new VM property

Do not add a persistent password textbox.

**Step 4: Run test to verify it passes**

Run the same test command and confirm the new behavior passes.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Views/Ssh/NewSshConnectionView.axaml src/NovaTerminal.App/Views/Ssh/NewSshConnectionView.axaml.cs src/NovaTerminal.App/ViewModels/Ssh/NewSshConnectionViewModel.cs tests/NovaTerminal.Tests/Ssh/NewSshConnectionViewModelTests.cs
git commit -m "Add native SSH remember-password editor option"
```

### Task 3: Add failing tests for vault preference handling

**Files:**
- Modify: `tests/NovaTerminal.Tests/Core/VaultServiceSshKeyTests.cs`
- Modify: `tests/NovaTerminal.Tests/Ssh/SshConnectionServiceTests.cs`
- Modify: `src/NovaTerminal.App/Core/VaultService.cs`
- Modify: `src/NovaTerminal.App/Services/Ssh/SshConnectionService.cs`

**Step 1: Write the failing test**

Add tests that assert:
- turning off the remember-password flag removes the stored password for that profile
- canonical profile-id keys continue to be used

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~VaultServiceSshKeyTests|FullyQualifiedName~SshConnectionServiceTests"`

Expected: FAIL because save/remove behavior is not wired yet.

**Step 3: Write minimal implementation**

Add a small app-layer seam so saving a profile can:
- leave vault contents untouched when remember is enabled and no new password is supplied
- remove the stored password when remember is disabled

Prefer a small explicit helper rather than scattering vault logic across the UI.

**Step 4: Run test to verify it passes**

Run the same test command and confirm the vault behavior passes.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/Core/VaultServiceSshKeyTests.cs tests/NovaTerminal.Tests/Ssh/SshConnectionServiceTests.cs src/NovaTerminal.App/Core/VaultService.cs src/NovaTerminal.App/Services/Ssh/SshConnectionService.cs
git commit -m "Wire native SSH password preference to vault cleanup"
```

### Task 4: Add failing tests for runtime password reuse

**Files:**
- Modify: `tests/NovaTerminal.Tests/Ssh/SshInteractionServiceTests.cs`
- Modify: `src/NovaTerminal.App/Services/Ssh/SshInteractionService.cs`
- Modify: `src/NovaTerminal.App/Core/VaultService.cs`

**Step 1: Write the failing test**

Add tests that assert:
- when a native profile has `RememberPasswordInVault` enabled and a saved password exists, password prompts are answered without showing the dialog
- passphrase prompts still show the dialog even if a password exists in the vault

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~SshInteractionServiceTests"`

Expected: FAIL because the interaction service does not resolve stored secrets yet.

**Step 3: Write minimal implementation**

Extend `SshInteractionService` so it can receive enough profile context to:
- identify the native SSH profile id
- query `VaultService`
- auto-return `SshInteractionResponse.FromSecret(...)` for password prompts only

Keep this native-only.

**Step 4: Run test to verify it passes**

Run the same test command and confirm the new tests pass.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/Ssh/SshInteractionServiceTests.cs src/NovaTerminal.App/Services/Ssh/SshInteractionService.cs src/NovaTerminal.App/Core/VaultService.cs
git commit -m "Auto-use saved password for native SSH prompts"
```

### Task 5: Add failing tests for prompt-side save behavior

**Files:**
- Modify: `tests/NovaTerminal.Tests/Ssh/SshInteractionServiceTests.cs`
- Modify: `src/NovaTerminal.App/ViewModels/Ssh/AuthPromptViewModel.cs`
- Modify: `src/NovaTerminal.App/Views/Ssh/AuthPromptDialog.axaml`
- Modify: `src/NovaTerminal.App/Views/Ssh/AuthPromptDialog.axaml.cs`
- Modify: `src/NovaTerminal.App/Services/Ssh/SshInteractionService.cs`

**Step 1: Write the failing test**

Add tests that assert:
- native password prompts expose a `Remember password` option
- selecting it causes the submitted password to be stored in the vault
- leaving it unchecked submits the password without storing it

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~SshInteractionServiceTests"`

Expected: FAIL because the auth prompt view model does not support remember state yet.

**Step 3: Write minimal implementation**

Update the dialog/view model to support:
- an optional remember-password checkbox
- a response shape that carries the remember decision

Then persist the password in `SshInteractionService` after prompt submission when:
- prompt kind is `Password`
- backend is native
- remember is checked

**Step 4: Run test to verify it passes**

Run the same test command and confirm the new tests pass.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/Ssh/SshInteractionServiceTests.cs src/NovaTerminal.App/ViewModels/Ssh/AuthPromptViewModel.cs src/NovaTerminal.App/Views/Ssh/AuthPromptDialog.axaml src/NovaTerminal.App/Views/Ssh/AuthPromptDialog.axaml.cs src/NovaTerminal.App/Services/Ssh/SshInteractionService.cs
git commit -m "Add remember-password option to native SSH prompt"
```

### Task 6: Verify end-to-end native-only behavior

**Files:**
- Test: `tests/NovaTerminal.Tests/Ssh/NewSshConnectionViewModelTests.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/SshConnectionServiceTests.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/SshInteractionServiceTests.cs`
- Test: `tests/NovaTerminal.Tests/Core/VaultServiceSshKeyTests.cs`
- Build: `src/NovaTerminal.App/NovaTerminal.App.csproj`

**Step 1: Run the targeted automated tests**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~NewSshConnectionViewModelTests|FullyQualifiedName~SshConnectionServiceTests|FullyQualifiedName~SshInteractionServiceTests|FullyQualifiedName~VaultServiceSshKeyTests"
```

Expected: PASS

**Step 2: Run compile verification**

Run:

```bash
dotnet msbuild src/NovaTerminal.App/NovaTerminal.App.csproj /t:Compile /p:Configuration=Release /p:SKIP_RUST_NATIVE_BUILD=1
```

Expected: PASS

**Step 3: Manual verification**

Verify:
- native profile shows remember-password checkbox
- OpenSSH profile does not show the checkbox
- first native password login can save to vault
- second native password login reuses saved password without prompting
- unchecking the profile option removes the stored password
- passphrase prompt still behaves as transient input only

**Step 4: Commit**

```bash
git add .
git commit -m "Verify native SSH password memory flow"
```
