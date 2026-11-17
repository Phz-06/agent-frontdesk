# Bringing desktop sending to Windows: where it stands

This is the hand-off for whoever continues the Windows port, in particular a Claude Code session on a Windows machine. Read it with `README.md` in this folder (the probe) and `skills/add-desktop-engine/references/channels.md` (how sending works today).

## Status

- **Done:** the analysis below, and the probe (`uia-probe.cs`, `probe.ps1`).
- **Step 1 runs:** on Windows 11 (build 26200, PowerShell 5.1, .NET 4.8.1) the helper compiles with `csc.exe` as written, and step 1 (read) runs end to end. The first run needed three fixes in `probe.ps1` and the helper: output paths past 260 characters, a second engine being handed the first engine's window, and a hidden Codex window being picked over its main one.
- **Not done:** steps 2 to 5 (settle, off screen, act, link) have not been run yet.
- **Next:** run steps 2 to 5 on Windows (README), fix whatever stops it, and read the results against the open questions below. Port nothing before that.

## What has to be ported

Reading history and drawing the page are plain files and the shared kit, so they mostly carry over. Paths need work, see the last section. Sending is decided per action across three routes (`channels.md`):

- **A, the app's own interface:** Codex's `thread/queue/add` plus a `codex://threads/wake/<id>` link, and Antigravity's language server. These are cross-platform apart from how the link is opened.
- **B, Codex's window-to-window router:** `~/.codex/ipc/ipc.sock` on the Mac. On Windows it must be a named pipe, because Node and Electron cannot listen on a socket path there. `src/server/codex-desktop.ts` needs only a new `socketPath()`, since `net.createConnection` takes pipe names.
- **C, the accessibility driver:** everything Claude does, and Codex's new chat and settings. This is the real port.

Route C on the Mac is three files plus a TypeScript wrapper:

- `src/server/native/ax-driver.swift` holds the shared actions and three self-checks:
  - one control, or none, before anything is done to it;
  - a menu button found by its place has to prove itself (`menuSigns`);
  - every answer says which way found each control (`via`).
- `claude-desktop.swift` and `codex-desktop.swift` hold each app's steps.
- `profiles/<id>.json` says how each control is told apart.
- `src/server/core/desktop-automation.ts` compiles them with `swiftc`, caches the result by source hash, runs one command at a time and reads back one JSON object.

The native record, not the helper, decides whether a message went out.

**Plan: keep this shape on Windows.** Write the same driver, flows and output in C#, with the same three self-checks. Read the same profiles through a role-mapping table (`AXButton` becomes `Button`, and so on), with a `windows` override section only where the apps differ.

The helper should be compiled on the person's machine with the `csc.exe` that ships with .NET Framework 4.x (C# 5). That is the Windows twin of `swiftc`: nothing is downloaded, and no binary has to be signed or get past SmartScreen. UIA would be reached through hand-declared COM interfaces, the stack vercel-labs/agent-desktop uses and measured. The alternative is a Rust helper on the `uiautomation` crate, built in CI and shipped. It is more proven, but it brings binary distribution.

## Mac to Windows

| Mac (route C) | Windows | Status |
| --- | --- | --- |
| `AXUIElementCreateApplication(pid)`, `AXMainWindow` | The app's top-level `Chrome_WidgetWin_1` window, then `ElementFromHandle` | Straightforward |
| `AXManualAccessibility` / `AXEnhancedUserInterface` | Nothing to switch on: Chromium builds the tree once a UIA client asks. It starts as a shell of a dozen nodes and can take 10 to 25 s cold (agent-desktop's findings) | **Measure** (`settle`) |
| `AXUIElementCopyMultipleAttributeValues` | One cache request over the raw view | Straightforward |
| Role / subrole | `ControlType` plus `AriaRole` | The mapping is a guess the probe checks |
| `AXDOMClassList`, which most locators use | `ClassName`? Chromium raises a ClassName change event when the class changes, but agent-desktop found no class tokens | **Open, and decisive**: without it, locators fall back to names and break in other languages |
| `AXKeyShortcutsValue` | `AcceleratorKey`? And is `Meta+n` written `Control+n`? | **Measure** |
| `AXDOMIdentifier` | `AutomationId` | Probably |
| `AXURL` of the page | The Document's Value | Probably |
| `AXPress` | `Invoke` / `ExpandCollapse` / `LegacyIAccessible.DoDefaultAction` | **Measure** (`act menu`) |
| `AXValue` set on the composer, with `AXSelectedText` as fallback | `Value.SetValue` only, because UIA cannot write selected text | **Measure** (`act fill`) |
| `AXFocused` | `SetFocus` takes the foreground (agent-desktop) | **Do not use**; `act focus` confirms it for Chromium |
| `CGEvent.postToPid` keys: submenus, Codex's Power slider, Escape | **No counterpart**: `PostMessage` keys do nothing in Chromium, and `SendInput` needs the foreground | **Hardest part**: replace with `ExpandCollapse`, `RangeValue` or `Collapse` (`act submenu`). Where none works, do not send and say why |
| `AXScrollToVisible` to refresh an off-screen page | `ScrollItem.ScrollIntoView` | **Measure** (`act refresh`) |
| `CGWindowListCopyWindowInfo` (on screen?) | `IsIconic`, `DWMWA_CLOAKED`, `IVirtualDesktopManager` | Straightforward; whether the tree stays live in those states is **measured** |
| `AXIsProcessTrusted` | No permission; check that the integrity levels match (UIPI) | The message to the person changes |
| `open -g <link>` (does not activate the app) | `ShellExecute`; no "don't activate" flag | **Measure** (`link`) |

The rules do not change on Windows (`skills/add-desktop-engine/references/philosophy.md`):

- Never take the foreground and never send global input.
- Never fall back to a CLI process that runs the turn itself.
- When the desktop app cannot take an action, do not do it, and say why.

## Outside accessibility

- **Paths:**
  - Claude desktop's data is under `%APPDATA%\Claude`.
  - `~/.claude` and `~/.codex` live under `%USERPROFILE%`.
  - The helper cache goes to `%LOCALAPPDATA%\agent-frontdesk`.
  - `src/server/config.ts` (`findClaude`, the `app.asar` paths) and `src/server/claude-bridge.ts` hard-code `Library/Application Support`.
- **Apps:** the profiles' `bundleId` becomes an exe name or an AppUserModelID; the probe lists both, and whether each app is MSIX.
- **Antigravity:** `open -b` in `src/server/antigravity-bridge.ts` becomes starting its exe. `src/server/services/reveal.ts` already handles `win32`.
- **Platform gate:** `DesktopAutomation#check()` returns "only macOS" on any other platform. It becomes a choice between the Swift and C# helpers.
- **CI** runs on `macos-latest` only. A Windows job can compile the helper and run the unit tests, but it cannot reach the real apps.

## Open questions the probe answers

1. Does `ClassName` carry DOM classes, and how many of the profile's class tokens are found?
2. Is aria-keyshortcuts in `AcceleratorKey`, and in what spelling?
3. Do the menu buttons open with `Invoke` or `ExpandCollapse`, without taking the foreground?
4. Do Claude's Effort and More models submenus open without a key? Does Codex's Power row offer `RangeValue`?
5. Does `Value.SetValue` fill the composer so that Send appears?
6. How long does the page take to reach UIA? Does it stay live when the window is minimized, covered or on another virtual desktop? Does `ScrollIntoView` refresh it?
7. What are Codex's pipe name, the deep-link handlers, the data folders, and the exe and package names?
8. Does a deep link bring the app to the front?
