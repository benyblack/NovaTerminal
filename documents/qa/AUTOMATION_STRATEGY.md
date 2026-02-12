# Nova Terminal: Test Automation Strategy

This document outlines how Nova Terminal leverages automated testing to maintain correctness, performance, and deterministic behavior.

## 1. Automation Feasibility Matrix

| Category | Automation Status | Primary Tool / Technique |
| :--- | :--- | :--- |
| **VT/ANSI Parsing** | Full | `AnsiParser` Unit Tests |
| **Buffer & Reflow** | Full | `TerminalBuffer` State Assertions |
| **Reflow Edge Cases** | Partial | `ReflowScenariosTests` |
| **Graphics Protocols** | Partial | `GraphicsTests` (Sixel Decoder) |
| **Rendering Fidelity** | Full | Golden Master Snapshot Testing (`SkiaSharp` + `Avalonia.Headless`) |
| **UI Interaction** | Automated | `Avalonia.Headless.XUnit` (`[AvaloniaFact]`) |
| **Platform Parity** | Automated | Deterministic Replay / ReplayTests |

## 2. Testing Layers

### 2.1 Unit Tests (`NovaTerminal.Tests`)
- **Focus**: Pure logic (Parser, Buffer, Row).
- **Goal**: 100% coverage of VT sequence state machine.
- **Key Files**: `TerminalBufferTests.cs`, `PowerShellBehaviorTests.cs`.

### 2.2 Replay Tests (Gate -1A)
- **Focus**: Reproducing real-world sessions.
- **Goal**: Feed raw byte streams into the core and assert buffer snapshots.
- **Benefit**: Platform-independent reproduction of complex bugs (e.g., Oh-My-Posh wrapping).

### 2.3 Golden Master (Gate -1C)
- **Focus**: Visual correctness & UI Interaction.
- **Goal**: Compare frame-by-frame rendering output against known "good" snapshots.
- **Status**: **Active**. Uses `SnapshotService` to capture Skia bitmaps and `Avalonia.Headless` to validate control state.

## 3. Automation Mapping

| QA Section | Automated Test Suite / File |
| :--- | :--- |
| **Core Correctness** | `AnsiParserTests.cs`, `AlternateScreenTests.cs` |
| **Buffer & Reflow** | `ReflowScenariosTests.cs`, `ReflowRegressionTests.cs` |
| **Graphics** | `GraphicsTests.cs` |
| **Rendering & UI** | `GoldenMasterTests.cs`, `HeadlessUITests.cs` |
| **Performance** | `PerformanceTests.cs`, `StressTests.cs` |
| **Platform Parity** | `ReplayTests/` |

## 4. Manual Testing Requirements
The following areas currently require manual validation:
- **High-DPI Clarity**: Requires physical display verification.
- **Transparency / Blur**: GPU/OS interaction is difficult to headless-test.
- **Input Methods (IME)**: Interaction with OS-native input windows.
- **Title Bar Theming**: Native window chrome color verification.

## 5. Running Tests

### 5.1 Run All Tests
Execute the full suite from the updated `NovaTerminal.Tests` project:
```powershell
dotnet test NovaTerminal.Tests\NovaTerminal.Tests.csproj
```

### 5.2 Category-Specific Runs
Use the `--filter` flag to target specific test traits:

**Performance Benchmarks**:
```powershell
dotnet test --filter "Category=Performance" --logger "console;verbosity=normal"
```

**Regression Suite** (MC, Oh-My-Posh):
```powershell
dotnet test --filter "Category=Regression"
```

### 5.3 Headless UI & Golden Master
Target specific classes or namespaces:
```powershell
# Run only Headless UI interaction tests
dotnet test --filter "FullyQualifiedName~HeadlessUITests"

# Run only Golden Master rendering tests
dotnet test --filter "FullyQualifiedName~GoldenMasterTests"
```

### 5.4 Troubleshooting
If tests fail or hang, increase verbosity to see `DeadlockDetection` or `RendererStatistics` output:
```powershell
dotnet test --logger "console;verbosity=detailed"
```
