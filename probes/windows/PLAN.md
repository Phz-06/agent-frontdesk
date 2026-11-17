# Bringing desktop sending to Windows: where it stands

This is the hand-off for whoever continues the Windows port, in particular a Claude Code session on a Windows machine. Read it with `README.md` in this folder (the probe) and `skills/add-desktop-engine/references/channels.md` (how sending works today).

## Status

- **Done:** the analysis below, and the probe (`uia-probe.cs`, `probe.ps1`).
- **Step 1 runs:** on Windows 11 (build 26200, PowerShell 5.1, .NET 4.8.1) the helper compiles with `csc.exe` as written, and step 1 (read) runs end to end. The first run needed three fixes in `probe.ps1` and the helper: output paths past 260 characters, a second engine being handed the first engine's window, and a hidden Codex window being picked over its main one.
- **Not done:** steps 2 to 5 (settle, off screen, act, link) have not been run yet.
- **Next:** run steps 2 to 5 on Windows (README), fix whatever stops it, and read the results against the open questions below. Port nothing before that.

## Findings so far (Windows 11 26200, Claude 5.11492, Codex 26.1111)

From step 1 (read) and read-only `act --do inspect` checks. Nothing was pressed.

**Locators and roles**

- **DOM classes:** `ClassName` carries the page's DOM classes on 363 of 476 web nodes. 7 of the Claude profile's 9 class tokens are found. The two missing ones, `group/menu-popup` and `text-on-primary`, belong to menus and dialogs and exist only while one is open. So the profiles can be shared (open question 1: yes).
- **haspopup:** `AriaProperties` carries haspopup (19 nodes), so popup buttons map as on the Mac. `modelMenu` is found by its first way.
- **Meta is written Control.** `newChat` is found by its mark only once `Meta+n` is read as `Control+n`. Claude's radio button in the Claude/Code switch carries `Control+Alt+ArrowLeft`. This becomes a rule in the Windows driver, not a profile edit (open question 2: yes, with Control for Meta).
- **spaceSwitch:** on Windows the Claude/Code switch (a `radiogroup`) is in `dframe-chrome-bar`. That is a 36px draggable bar across the top of `dframe-root`, a sibling of `dframe-sidebar`. It is not inside the sidebar, `epitaxy-titlebar` or the chat pane. So the profile needs a `windows` override with a `chromeBar` locator (class `dframe-chrome-bar`) for `spaceSwitch` to be found by its mark. The role mapping (`AXRadioGroup` to `radiogroup`) is right.
- **Radio buttons:** the space radio buttons offer `Invoke` and `SelectionItem`, but not `Toggle` or `ExpandCollapse`. They carry `checked=true|false` in `AriaProperties`. Which pattern replaces `AXPress` for them has not been tried.
- **automationIds:** base-ui automationIds (`base-ui-_r_30_`) are generated, so they are not stable and cannot be used in locators.

**Reading the page**

- **Speed:** a read of about 480 nodes takes 60 ms, in one round trip (a cache request over the raw view).
- **Cold shell:** Claude's page showed 24 nodes at first contact, then 462 to 480 nodes on the next read, under a minute later. The full curve (`settle`) has not been measured.
- **Minimized Codex:** Codex's window was minimized throughout, and showed 9 nodes. This is a minimized measurement only: Codex has no census yet with its window restored.
- **Background reads:** reads work while Claude is in the background, with another app in the foreground.

**Outside accessibility**

- **Codex's router pipe** is `codex-ipc` (route B's `socketPath()` on Windows).
- **Deep links:** `codex://` is declared only in the MSIX manifest, not under `Software\Classes`. The read step now lists manifest protocols. The two AUMIDs:
  - `OpenAI.Codex_2p2nqsd0c76g0!App` (`app/ChatGPT.exe`, protocol `codex`);
  - `Claude_pzs8sxrjxfjjc!Claude` (`app\Claude.exe`, protocol `claude`, also registered under Classes).
- **Both apps are MSIX packages.** Their window exes are `ChatGPT.exe` and `claude.exe`, under `C:\Program Files\WindowsApps`. The shell and both apps run at the same integrity level, High (12288 = 0x3000; Medium is 8192). They do because UAC is off on this machine (`EnableLUA=0`, and this is the built-in Administrator account), so every process runs elevated. That is not the usual setup, where both the person's apps and the server run at Medium.

**Codex, with the window maximized (census and act, in the person's conversation, with their agreement)**

- **Census:** 367 nodes. `ClassName` is set on 319 of 364 web nodes, and 5 of the profile's 9 class tokens are found. The missing ones are `ClearableSelectorTrigger-*`, `ClearableSelectorTriggerClearButton-*`, `ViewToggle-*` and `comboboxList-*`, which live in menus and pickers. haspopup is set on 15 nodes. These locators are found by their first way: `chatComposer`, `composer`, `composerBody`, `composerMain`, `modeSwitch`, `modelMenu`, `newChat`, `permissionsMenu`, `projectChooser`, `sidebar` and `sidebarHeader`. `send` and `stop` were absent: no run was going on, and the composer was not empty enough to show Send. `acceleratorKey` was empty on every node outside menus.
- **fill:** refused, because the composer already held 12 characters of the person's own text. The probe writes only into an empty box. So `Value.SetValue` is still unmeasured.
- **menu (`modelMenu`):** the button offers `ExpandCollapse` and `LegacyIAccessible`, but not `Invoke`.
  - `ExpandCollapse.Expand` opens the menu (menu rows 4 to 7), **and brings Codex to the front**. This happened both times it was tried, with Claude in the foreground beforehand.
  - Neither `ExpandCollapse.Collapse` nor `LegacyIAccessible.DoDefaultAction` closes it again: the button stays `expanded=true`. Only the person's Esc closed it.
  - `DoDefaultAction` sent to the closed button, with Codex in the background, fails with `0x80040200` (UIA_E_ELEMENTNOTENABLED). Nothing opens and the foreground does not change.
- **submenu:**
  - The Power row (`effortRow`, class `SliderKeyboardControl-*`) is a `menuitem` with `Invoke` only. It has **no `RangeValue`**, and its acceleratorKey is `ArrowLeft ArrowRight`: it is moved by keys alone.
  - The Select model row has no `ExpandCollapse`, but **`Invoke` opens its model list** in place (the menu stays open, and the rows go from 7 to 4) without changing the foreground.
- **focus:** `SetFocus` on the composer brings Codex to the front, as agent-desktop found.
- **The foreground grab is not an artifact of the setup.** The first runs were started from inside the Claude app, which was in front, and it could have handed Codex the right to take the foreground. So the menu run was repeated with the probe started from a standalone PowerShell window (Win+R) and an unrelated app (Chrome) in front. The foreground still went from chrome.exe to ChatGPT.exe the moment `Expand` opened the menu.
  - The foreground lock is on, and stricter than the default (`SPI_GETFOREGROUNDLOCKTIMEOUT` is 2147483647 ms), so a loose setting did not let Codex in.
  - After that run the person said they had not seen Codex come forward: they found the menu open and closed it with a click into Codex. Being the foreground window and being drawn on top are not the same thing, so the act results now also record which top-level window covers the target's centre (`onTop`, from `WindowFromPoint`). On the next run, under the same conditions (standalone PowerShell, Chrome in front), `onTop` went from chrome.exe to ChatGPT.exe the moment `Expand` opened the menu. **So Codex really is raised over the other windows, not only activated.** Why the earlier run looked different is not known; it may have been timing.
  - Whatever grants the right, UI Automation's call into Chromium or the page's own focus code, it would grant it to a background Frontdesk server just the same.
  - Caveat: on this machine everything runs at High integrity with UAC off. The Medium case has not been seen.
- **Codex's app-server** (bundled with Codex 26.1111 as `app\resources\codex.exe` 0.169.0-alpha.11.1, read-only look). `generate-json-schema` leaves experimental methods out unless it is given `--experimental`, and the bridge connects with `capabilities: { experimentalApi: true }`. With `--experimental`, the schema has:
  - `thread/queue/add`, `delete`, `list`, `reorder`, `start` and `update`. So route A's queue exists on Windows.
  - `thread/settings/update` (`ThreadSettingsUpdateParams`: `model` and `effort` "for subsequent turns", plus `approvalPolicy`, `collaborationMode`, `permissions`, `sandboxPolicy`, `serviceTier` and others).
  - Without `--experimental`, the only places to set the model and effort are `thread/start`, `thread/resume` and `turn/start`. `thread/settings/updated` is the server's notification of the result.
- **Codex's model and effort need no menus: route B.** The Windows desktop bundle (`app.asar` in the OpenAI.Codex package) carries the same window-to-window router as the Mac.
  - On win32 its endpoint is the named pipe `\\.\pipe\codex-ipc` (`path.join("\\\\.\\pipe", "codex-ipc")`). Elsewhere it is `<codex home>/ipc/ipc.sock`. Messages are framed as a 4-byte little-endian length followed by UTF-8 JSON.
  - The bundle has 18 `thread-follower-*` requests: steer-turn, interrupt-turn, start-turn, the queue ones, and the two that matter here:
    - **`thread-follower-update-thread-settings`.** A window that does not own the thread sends `{conversationId, threadSettings, activeTurnId?, condition?}`, where `condition` is `{ifModelEquals?, ifEffortEquals?}`. The owner calls its own app-server `thread/settings/update {threadId, ...threadSettings}`, updates its own state so its composer shows the change, and answers `{applied}`.
    - **`thread-follower-read-model-settings`.** It returns `{model, reasoningEffort, …}` from the owner.
  - So changing a Codex chat's model or effort goes through route B, like steer and interrupt in `src/server/codex-desktop.ts`. It avoids the menus, the foreground and the Power slider, on both platforms. Only `socketPath()` changes, as planned.
  - Sending one changes a real chat, so it has not been sent here. It is to be tried in a test chat, with the person's agreement.
  - Codex's new chat (project picker, new-chat button) is still UI-only, so it still meets the menu findings above.

**Against the Mac driver and agent-desktop**

The Mac driver posts keys to the app's pid with `CGEvent.postToPid`, which works in the background. The keys are:
- Escape: closes every Codex menu, and Claude's when off screen;
- Right: opens Claude's Effort and More models submenus;
- Left and Right: move Codex's Power slider;
- Down: opens a Claude menu off screen;
- Space: ticks a question-card option;
- Up ×4: pages back through a long chat.

Windows has no counterpart to `postToPid`. `PostMessage` keys do nothing in Chromium, and `SendInput` goes to the foreground. agent-desktop (vercel-labs, Rust on the `uiautomation` crate) keeps every key and SetFocus behind `--headed`. It has no headless way to close a Chromium web menu either: its probes close one with a `SendInput` Esc while the app is in front (`probes/windows/FINDINGS.md`, A26-12). It never measured Invoke or Expand taking the foreground on Chromium. So on Windows, route by route:

| Mac | Windows, as measured here | What the port does |
| --- | --- | --- |
| Press a button (`AXPress`) | `Invoke` where offered; untested for the foreground on plain buttons | Use `Invoke` first, then `LegacyIAccessible` (agent-desktop's chain) |
| Open a menu (press) | Codex: `Expand` works but takes the foreground; `DoDefaultAction` in the background fails | **Breaks the "never take the foreground" rule**: refuse, unless a way is found that keeps the foreground |
| Close a menu (Escape) | No UIA action closes Codex's menu | Close menus only by pressing a row, which closes them on the Mac too. Otherwise refuse |
| Claude submenu (focus + Right) | Codex's Select model row opens with `Invoke`; Claude's rows are untested | Use `Invoke` on the row |
| Codex Power slider (Left/Right) | No `RangeValue`, only `Invoke` | Not through the UI. Use route B instead: `thread-follower-update-thread-settings` over `\\.\pipe\codex-ipc` (see above). Codex's model choice goes the same way, so its menus are not needed for settings |
| Focus (`AXFocused`) | `SetFocus` takes the foreground | Do not use |
| Fill (`AXValue`) | Untested (the box held text) | `Value.SetValue`, to be measured in an empty composer |

**Still open**

- **Fill:** `Value.SetValue` on Codex's composer, in an empty test conversation.
- **Opening a Codex menu without the foreground:** no way has been found yet (the setup was ruled out, see above). It is open whether giving the foreground back right afterwards is acceptable, and whether a server at Medium integrity behaves differently.
- **Closing a menu by pressing its current row:** whether pressing the already chosen row closes a menu with no change.
- **Settle curves (question 6):** for both apps. Claude's must be run from a standalone PowerShell window, since quitting Claude ends a session that runs inside it.
- **Off-screen states (question 6):** minimized, covered and other virtual desktop, and whether `ScrollIntoView` refreshes the page.
- **The act scenarios (questions 3, 4, 5):** Claude's scenarios (fill, menu, submenu, focus) and refresh, for both apps. Codex's menu, submenu and focus results are above. Run them in a test conversation, and only with the person's agreement. Also which of `Invoke` / `SelectionItem.Select` switches the space.
- **Deep link and foreground (question 8):** whether a deep link brings the app to the front.
- **Integrity levels (UIPI):** when the Frontdesk server runs at a lower level than an app (Medium against High, say), UIPI lets it read the app's UIA tree but not act on it. The port's `check()` should therefore compare the helper's integrity level with the target window's, and say so when they differ, in place of the Mac's `AXIsProcessTrusted` message. Act results from this machine show the same-level case only (High to High). The Medium-against-High case has not been measured.

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
