# Nova Terminal: Test Automation Strategy

This document outlines how Nova Terminal leverages automated testing to maintain correctness, performance, and deterministic behavior.

## 1. Automation Feasibility Matrix

| Category | Automation Status | Primary Tool / Technique |
| :--- | :--- | :--- |
| **VT/ANSI Parsing** | Full | `AnsiParser` Unit Tests |
| **Buffer & Reflow** | Full | `TerminalBuffer` State Assertions |
| **Reflow Edge Cases** | Partial | `ReflowScenariosTests` |
| **Graphics Protocols** | Partial | `GraphicsTests` (Sixel Decoder) |
| **Rendering Fidelity** | Planned | Golden Master Snapshot Testing |
| **UI Interaction** | Manual | Avalonia Headless Testing (Targeted) |
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
- **Focus**: Visual correctness.
- **Goal**: Compare frame-by-frame rendering output against known "good" snapshots.
- **Status**: Future enhancement for the Skia renderer layer.

## 3. Automation Mapping

| QA Section | Automated Test Suite / File |
| :--- | :--- |
| **Core Correctness** | `AnsiParserTests.cs`, `AlternateScreenTests.cs` |
| **Buffer & Reflow** | `ReflowScenariosTests.cs`, `ReflowRegressionTests.cs` |
| **Graphics** | `GraphicsTests.cs` |
| **Platform Parity** | `ReplayTests/` |

## 4. Manual Testing Requirements
The following areas currently require manual validation:
- **High-DPI Clarity**: Requires physical display verification.
- **Transparency / Blur**: GPU/OS interaction is difficult to headless-test.
- **Input Methods (IME)**: Interaction with OS-native input windows.
- **Title Bar Theming**: Native window chrome color verification.
