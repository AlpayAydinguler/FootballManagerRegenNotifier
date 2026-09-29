# Project specification and design record

This document preserves the original brief, the research that informed the
design, and the decisions taken — including the places where the implementation
deliberately departs from the brief, and why. It exists so that the reasoning
survives for future sessions and for anyone picking the project up.

---

## Part 1 — The original brief

> **Project: Build a C# Companion Application for Football Manager 2026**
>
> **Role:** Act as an expert C# developer and software architect. Your task is to
> design and build a complete, feature-rich desktop application from scratch.
>
> **Language:** C# (You have the freedom to choose the .NET version, UI
> framework, architecture, and any third-party libraries you think are best
> suited for this task).
>
> ### Memory & Version Control Requirements
>
> **Memory:** Please keep this entire project specification, all architectural
> decisions we make, and the final code in your persistent memory.
>
> **GitHub:** Please set up and maintain a GitHub repository for this project.
> Commit your work regularly with clear commit messages. The repository should
> include all source code, a comprehensive `README.md`, a `LICENSE` file (suggest
> MIT unless you have a better recommendation), a `.gitignore` appropriate for C#
> projects, and this specification saved into the repository.
>
> ### The Goal
>
> Create a comprehensive companion application for the PC game Football Manager
> 2026.
>
> **Purpose & Version Compatibility:** This software is designed specifically for
> Football Manager 2026 because the developers removed the in-game Notes and
> Reminders feature, leaving players without a way to track important dates like
> youth intake days.
>
> However, the application is designed to be version-agnostic and future-proof.
> It should work with future versions of Football Manager (FM27, FM28, etc.) and
> even past versions (FM24, FM23) as long as the user can identify the screen
> location where the date is displayed. The coordinate system, OCR tuning, and
> user-configurable settings are all designed to make this tool adaptable across
> different game versions and screen resolutions.
>
> The app must run alongside the game, read the in-game date from the screen,
> compare it to a list of "Youth Intake" (Regen) dates, and alert the user when a
> match occurs. The app should function as a control center with live previews,
> customization options, and logging.
>
> ### Core Features & UI Requirements
>
> **1. Coordinate Setup & Calibration**
> - **Visual Snipping Tool:** a button that launches a visual snipping tool
>   (similar to the Windows Snipping Tool). The user should be able to draw a
>   rectangle directly over the screen (ideally over the game) to auto-populate
>   the coordinates.
> - **Manual Entry:** four input fields representing two corner points: Top-Left
>   X, Top-Left Y, Bottom-Right X, Bottom-Right Y. The software should calculate
>   the rectangle from these two points.
> - **Default Values:** Top-Left X: 2035, Top-Left Y: 5, Bottom-Right X: 2180,
>   Bottom-Right Y: 40. (These work perfectly for the user's current resolution.)
> - **Live Mouse Tracker:** a live-updating label showing the current global
>   mouse cursor coordinates.
>
> **2. Live Preview & OCR Tuning**
> - **Image Preview:** show the exact image (the captured rectangle) that is
>   currently being sent to the OCR engine in a PictureBox.
> - **Text Preview:** show the raw text read by the OCR engine, and the final
>   parsed date in separate labels.
> - **Advanced OCR Settings Panel:** UI controls allowing the user to live-tweak
>   the image preprocessing pipeline: Upscale Factor (1x to 6x), Binarization
>   Threshold (0 to 255), toggle between Grayscale and Black & White, toggle
>   character whitelist on/off.
> - **Default Settings Recommendation:** use the exact settings found to work
>   during manual testing: Upscale Factor 4x, Binarization Threshold 128, Mode
>   Black & White (thresholded), Interpolation Mode NearestNeighbor (for sharp
>   pixels), Character Whitelist `0123456789/` (toggle-able, default ON).
> - If the user changes any of these, the app should immediately attempt to
>   re-read the current screen region with the new settings so they can see the
>   effect in the live preview.
>
> **3. Country Selection Dashboard**
> - **Data Structure:** a comprehensive dataset of major football nations
>   including Name, Continent, Youth Rating, and Regen Start Date. Use the list
>   from `https://www.passion4fm.com/football-manager-youth-intake-dates/`.
> - **Trigger Logic:** alert the user on the earliest youth intake start date for
>   each country. The reasoning: once the intake window opens, the user will be
>   checking the World > Transfers > Youth Intake screen daily until regens stop
>   coming anyway. So the alert should mark the beginning of that period.
> - **Collapsible Grouping:** group the countries by Continent. The continent
>   headers must be collapsible/expandable.
> - **Checkboxes:** allow the user to select/deselect individual countries.
> - **Sorting:** provide a way to sort the list within the groups by Name, Youth
>   Rating, or Regen Date.
> - The list of countries with their dates and youth rates should be kept in an
>   excel file so it can be easily modified for future/older versions.
> - **Bulk Actions:** "Select All", "Deselect All", "Select Top Tier"
>   (automatically checks any country with a Youth Rating of 130 or higher).
>
> **4. Alerts & Anti-Spam Guard**
> - When the read date matches a trigger date for a checked country, the
>   application must alert the user (popup, sound, flashing overlay, or bringing
>   the app to the front).
> - **Anti-Spam Guard:** the alert must only fire once per in-game day per
>   country.
> - **Activity Log / Event Stream:** a dedicated log panel showing a rolling
>   stream of events, such as: "Date read: 01/07/2025", "OCR Confidence: 94%",
>   "Matched date for England. Alert triggered.", "Spam guard blocked duplicate
>   alert for England."
>
> **5. Execution Control & Persistence**
> - A "Start Monitoring" button. When pressed, it should begin the checking
>   process and disable the coordinate inputs, OCR tuning inputs, and country
>   checkboxes to prevent accidental changes while running. A "Stop" button
>   should re-enable them.
> - **Settings Persistence:** save ALL user configurations (Coordinates, OCR
>   tuning parameters, Checked Countries) to a local settings file when the app
>   closes, and load them on startup.
>
> ### Technical Constraints & Best Practices
> - **Reading Interval:** check the screen at a regular interval (e.g. every 1
>   second).
> - **Overlap Protection:** ensure that if one reading process is still running,
>   the next scheduled interval does not start a new one (prevent re-entrancy).
> - **Error Handling:** if the text cannot be read or parsed, fail silently or log
>   it to the Activity Log without crashing.
> - **The OCR Challenge:** the font used in the game makes the number "7" look
>   exactly like a "1" at low resolutions. Implement image preprocessing (e.g.
>   upscaling, contrast adjustment, binarization) to ensure the text recognition
>   engine can accurately distinguish between these numbers.
>
> ### README Requirements
> A clear description of what the app does and why it was created (mentioning the
> removal of Notes/Reminders in FM26); screenshots or GIFs of the UI; installation
> instructions (including how to set up the `tessdata` folder for OCR); usage
> instructions (how to calibrate coordinates, select countries, start monitoring);
> a section on version compatibility; a section on troubleshooting; and a
> "Contributing" section.

---

## Part 2 — Research findings

Research was carried out before any code was written, and several findings
changed the design.

### The premise is sound

- FM26 shipped 4 November 2025, is the first Football Manager built on Unity, and
  is **frozen at build 26.3.2** — development moved to FM27 (November 2026).
- The removal of Notes/Reminders is confirmed by SI's own forums, and it was
  never restored. **The gap is real and permanent.**
- No existing tool does this. The adjacent ones (FMSuperScout, the commentary
  mod, FM Assistant) are all reactive or manual; none is a background daemon.

### Cheaper alternatives to OCR were checked and ruled out

The obvious question is whether the date can be obtained without screen reading.
Each candidate was investigated, and one was tested directly on the target
machine rather than assumed:

| Route | Verdict |
|---|---|
| **`Player.log`** | **Ruled out empirically.** The 14.6 MB log was grepped: it contains only the real-world date and Unity renderer spam, with no occurrence of `GameDate`, `CurrentDate`, `advance`, `Continue`, `newgen` or `youth intake`. |
| **Windows UI Automation** | Dead end. Unity's desktop screen-reader support landed in 6.3; FM26 is Unity 6.0. Corroborated by FM26Access, an accessibility mod that had to inject via BepInEx precisely because no UIA tree exists. |
| **Steam / Discord rich presence** | Dead end. FM exposes only "playing Football Manager"; a long-standing feature request for exactly this was never implemented. |
| **`.fm` save parsing** | Wrong shape. Needs a save to exist and be copied; a reminder needs the date continuously. |
| **BepInEx IL2CPP plugin** | Viable and used by other FM26 tools, but it is code injection into the process and sits squarely in the EULA's reverse-engineering clause. Screen reading touches none of it. |

**Conclusion: OCR is genuinely the right approach**, not a fallback chosen for
convenience.

### The correctness finding that reshaped the design

**FM's Continue button advances to the next event, not by one day.** Users report
jumping 2 June → 8 June in a single click, and the intervening dates are never
rendered. An exact date-equality trigger is therefore broken by construction, and
**sampling faster does not fix it** — there is nothing on screen to sample. This
is the single most important thing the research turned up, and the entire
alerting model was rebuilt around it. See [Decision 2](#2-triggers-fire-on-interval-crossing).

### Environment facts measured on the target machine

| Fact | Consequence |
|---|---|
| Windows 10 22H2 build 19045 — **not Windows 11** | Windows Graphics Capture's yellow-border opt-out does not exist on this build, so WGC would paint a permanent border over the game. **GDI BitBlt it is.** |
| 2560×1440 at 100% DPI | The supplied default coordinates are already physical pixels. |
| FM26 renders with **Direct3D 11**, non-Steam install | GDI capture works fine in windowed/borderless. No Steam paths may be assumed. |
| `Tesseract` 5.2.0 ships `PixConverter` only in its `net48` asset | A `net10.0-windows` project resolves `netstandard2.0`, where that type does not exist. The `Bitmap → Pix` bridge is unavailable; images must be passed as encoded bytes. |
| Tesseract natives deploy as loose files into `x64\` | Single-file publish does not work cleanly. Releases ship as a zipped folder. |

---

## Part 3 — Decisions

### 1. No `Windows.Media.Ocr`; a deterministic glyph matcher is the second engine

A zero-install second OCR engine was considered and rejected on measurement.
Using `Windows.Media.Ocr` requires TFM `net10.0-windows10.0.19041.0`, and the
platform floor is **contagious upward through project references** — a
`net10.0-windows` project cannot reference a `10.0.19041.0` one, so the whole
solution moves, and gains ~20 MB of CsWinRT, for an engine with no character
whitelist and no per-character confidence.

Tesseract is the engine, with the full legacy+LSTM language data bundled so the
character whitelist is genuinely enforceable.

### 2. Triggers fire on interval crossing

Rather than `today == triggerDate`, the tracker persists the last confirmed date
and asks whether any armed trigger falls in the half-open interval between it and
the current reading.

```
lastSeen == null             → cold start: adopt, arm, fire nothing (after Reset date:
                               also announce windows open now)
delta == 0                   → no-op
0 < delta <= MAX_JUMP        → fire every trigger T where lastSeen < T <= current
-MAX_JUMP <= delta < 0       → regression: fire nothing, re-arm triggers in (current, lastSeen]
|delta| > MAX_JUMP (400d)    → quarantine: do not commit, do not fire, count towards a re-sync
same date in another year    → quarantine as above, even inside MAX_JUMP (±7 days)
```

Retroactive alerts are labelled with how late they are, so "the window opened
five days ago" reads differently from "the window opens today".

**Quarantine is symmetric, and it is not a dead end.** An earlier version only
bounded forward jumps: a year misread as 2013 therefore went through as a save
reload, was committed and saved, and turned every correct reading afterwards into
an implausible forward jump, permanently. The ceiling can tell that two dates are
far apart but not which one is wrong, so quarantined readings that agree with each
other (each within `MAX_JUMP` of the last) build a **re-sync candidate**. After 10
confirmations (about 20 seconds) with no reading of `lastSeen` in between, the
candidate wins. A single read of `lastSeen`, or a confirmed plausible reading,
starts the count again.

The same-date-in-another-year rule exists because the likeliest misread year is
one wrong last digit, and 2025 read as 2026 is 365 days: inside the ceiling, and
before this rule it fired a whole year of alerts on every flicker. Normal play
never has that shape; a holiday of about a year does, and simply waits out a
re-sync.

**A re-sync replays the candidate from an origin.** The candidate's own moves are
replayed exactly as the rows above would have handled them: re-arm down to the
lowest date it visited, then fire everything from there to where it is now. So an
intake crossed during the wait is reported late rather than lost. A confirmed
reading that carries on from the candidate (closer to it than to `lastSeen`, and
at most 60 days past it) counts towards the candidate instead of being committed:
a save loaded a year back walks out of the misread-year shape within a week of
Continue clicks. Likewise only a reading that carries on from the candidate (at
most 60 days past it, or a reload back within the ceiling, and not in the
misread-year shape) extends it; anything else starts a new candidate, so a save
shown for a few seconds by mistake is not merged with the one loaded after it.
The origin is the first of:

1. the timeline the last re-sync left, with its alerts, when the candidate comes
   back to it: anywhere from `MAX_JUMP` before where it was left to 60 days after,
   or the whole ceiling after if that date came from `state.json` or the
   candidate is the date being left in another year (the real date returning
   after a misread year, possibly past a long holiday). A misread that won, or a
   save switched away from, is picked up where it was left; only the most recent
   one is remembered, in memory only;
2. the committed timeline itself, when the candidate is inside the ceiling and
   was held only for its shape (a year-long holiday comes out as if accepted at
   once, only later);
3. otherwise a fresh start the day before the candidate's first reading.

Alerts never carry across timelines any other way. Two saves at the same time of
year in different years look exactly like a misread year; carrying one's alerts
into the other would silently suppress an open window, where the alternative, a
misread year that wins repeating an intake, only costs a duplicate. Windows
already open at the new date are then announced unless the origin had alerted
them, so a re-sync never leaves an open window unmentioned.

**A date restored from `state.json` is a hint, not a fact.** It may belong to
another save, or be a misread saved by an earlier version, so until it has been
seen on screen the first confirmed reading beyond the ceiling wins at once. One
held only for its misread-year shape still waits: believed at once, two samples of
a misread digit would fire a year of alerts. A plausible reading still counts from
the restored date, which is what reports the intakes crossed while the app was
closed, and so does a return to it after a misread won at launch.

### 3. Anti-spam is two-layer

The brief asks for "once per in-game day per country". Implemented literally that
breaks the retroactive case: one holiday jump legitimately crosses forty nations
at once and a per-day key would fire the first and swallow thirty-nine.

So arming is keyed on the **occurrence** — `(country, in-game year, kind)` — and a
**real-time cooldown** (60 s, configurable) sits at the alert channel. That
restores the protection actually wanted in the save-scum loop, where reload →
advance → reload → advance would otherwise re-fire the same alert every few
seconds, without ever swallowing a legitimate fan-out. Every fire is written to
the Activity Log regardless of whether it was delivered.

### 4. Commit-on-confirm

A new date must be observed twice consecutively before it is committed. A single
misread that moved `lastSeen` forward would silently mark a trigger as crossed —
the alert then never fires and nothing indicates anything went wrong, which is the
worst available outcome. Per-symbol confidence gating catches the malformed glyph
that a line-level mean comfortably hides.

### 5. The privacy gate does not apply to the preview

Gating capture on `fm.exe` (requested) would make the tuning panel permanently
blank while setting up with the game closed, defeating the live-preview
requirement. The gate applies to the sampler loop and the rolling file log; an
explicit, user-initiated preview refresh still works, shows "FM not running", and
never writes OCR text to disk.

### 6. Two preprocessing stages the brief did not ask for

The brief names upscaling, contrast and binarization. Two further stages do more
for the 7-vs-1 problem than any of them:

- **Polarity inversion.** FM draws light text on a dark bar; Tesseract's models
  are trained on dark text on light paper.
- **Quiet-zone padding.** Tesseract degrades on text flush to the frame edge, and
  a crop that shaves the crossbar off a `7` leaves something that genuinely is a
  `1`. The preview warns when ink touches the edge.

A **stroke-thickening** control was added after measurement (below).

### 7. Integer pixel replication, not `InterpolationMode.NearestNeighbor`

GDI+'s nearest-neighbour offsets the sample grid by half a pixel unless
`PixelOffsetMode.Half` is also set. That shift smears glyph edges, and a smeared
crossbar is exactly how a `7` becomes a `1`. Integer replication has no such
subtlety. The *intent* of the requested setting is preserved exactly; the
mechanism is more reliable.

### 8. The shipped threshold stays at 128 despite contrary measurement

A sweep over font size, threshold and dilation against synthetic dates found
**threshold 160 with one thickening pass read 6/6 at every size tested**, where
128 was erratic on thin text (0–5 out of 6).

The default was **not** changed. The 128 value was validated against the real game
by the user; the test renderer uses a stock Windows font as a proxy and is not
evidence about FM26's Unity font. Overriding a real-world-validated default on
proxy evidence would be overreach. Instead the finding became documented tuning
guidance in the README and a new `StrokeThickenPasses` control, defaulting to 0.

### 9. Frames cross every boundary as a managed `byte[]`

Never a `Bitmap` or `HBITMAP`. This removes the entire class of GDI-handle leaks
that kills a process sampling once a second for hours, and makes every
preprocessing stage a pure function testable against fixtures with no desktop.

### 10. Confidence is normalised to 0..1 at the adapter boundary

Tesseract reports the line mean on 0..1 but per-symbol confidence on 0..100.
Letting both scales travel upward produces a confidence floor that rejects every
sample forever — which presents to the user as an app that has simply stopped
doing anything. There is a test asserting 0.54 rejects and 0.56 accepts.

### 11. Settings and runtime state are separate files

`settings.json` changes when the user touches a control; `state.json` changes
every time the clock moves. Sharing one file would put a laboriously tuned
capture rectangle behind a write that runs all session long. Both write via a
temp file and replace.

### 12. The dataset is not MIT, and the app runs without it

Code is MIT. The intake table is third-party factual data redistributed with
attribution under `data/LICENSE-DATA.md`. The canonical copy is a plain-text seed
in the repository so changes are reviewable in a diff; the `.xlsx` is generated on
first run. The application starts and runs with the dataset absent, which is both
correct engineering and means a takedown request costs one file.

### 13. Virtualization is explicitly off in the country list

Wrapping the `ItemsPresenter` in an `Expander` measures it with infinite height,
so `VirtualizingPanel.IsVirtualizingWhenGrouping` realises every container
anyway. Shipping the attribute would imply a guarantee it does not deliver. At 91
rows full realisation costs nothing, so the honest thing is to say so.

---

## Part 4 — Requirement coverage

| # | Requirement | Status |
|---|---|---|
| 1a | Visual snipping tool | Done — full-desktop overlay over a frozen screenshot |
| 1b | Manual four-field coordinate entry | Done |
| 1c | Live global mouse tracker | Done, 20 Hz |
| 1d | Defaults 2035/5/2180/40 | Done |
| 2a | Preview of the exact image sent to OCR | Done — post-preprocessing, not the raw grab |
| 2b | Raw text and parsed date shown separately | Done, plus confidence |
| 2c | Upscale, threshold, grayscale/B&W, whitelist controls | Done, plus invert, quiet zone, thickening, engine mode |
| 2d | NearestNeighbor interpolation | Done via integer replication — see Decision 7 |
| 2e | Immediate re-read on any setting change | Done, debounced ~180 ms |
| 3a | Collapsible continent grouping | Done |
| 3b | Per-country checkboxes | Done |
| 3c | Sort by name / rating / date | Done |
| 3d | Select All, Deselect All, Select Top Tier ≥130 | Done, threshold configurable |
| 3e | Country list in an editable Excel file | Done, seeded on first run |
| 4a | Alerts | Done — sound, flash, tray, optional foreground |
| 4b | Anti-spam guard | **Redesigned** — see Decision 3 |
| 4c | Activity log event stream | Done, colour-coded and capped |
| 5a | Start/Stop monitoring | Done |
| 5b | Inputs disabled while running | Done |
| 5c | Persist all configuration | Done, across two files |
| T1 | ~1 s polling interval | Done, configurable |
| T2 | Re-entrancy guard | Done — `PeriodicTimer` plus a shared semaphore |
| T3 | Errors logged, never crash | Done |
| T4 | Distinguish 7 from 1 | Done — see Decisions 6, 7, 8 |
| V | Version-agnostic | Done — re-calibrate and edit the spreadsheet |

Additions beyond the brief, all agreed in advance: the privacy gate, the tray
countdown, a `--screenshot` switch for documentation, and per-row data confidence
surfaced in the dashboard.

---

## Part 5 — Known limitations

- **The dataset is FM24-era** and unverified against FM26. Nine African and
  Middle-Eastern nations share one default date; Egypt and Lithuania are newly
  playable but carry stale values; U.A.E., Hong Kong, Malaysia, Singapore,
  Nigeria and DR Congo are absent. Confidence is shown per row rather than hidden.
- **Exclusive fullscreen cannot be captured.** Borderless windowed is required.
- **Intake windows are per nation, not per club.** A nation's window can span
  three weeks and a given club lands on one day inside it. The alert marks the
  start of the period, which is what the brief asked for.
- **No save identity.** No `.fm` files exist under the user profile on the target
  machine, so distinguishing "reloaded this save" from "switched saves" is not
  possible. An earlier save is treated as a regression and re-arms; a later one
  within 400 days as a holiday, reporting every intake in between as missed; one
  further away as a probable misread, switched to after about twenty seconds (at
  once after a restart). Only the save most recently switched away from is picked
  up where it was left. The header's **Reset date** is the explicit override.
- **A misread can win.** A wrong date that is all the app sees for twenty seconds
  is switched to, because it is indistinguishable from loading another save. The
  switch back loses nothing, but while a misread year is the tracked date intakes
  fire on the right day under the wrong year, and are reported again, correctly,
  once the real date is back. A misread by exactly one year that wins is
  indistinguishable from a year-long holiday, and reports that year's intakes as
  missed.
- **A restored date is remembered all session.** After a restart, a save loaded
  up to a year after the saved date is taken for that save played on while the
  app was closed, even hours later, and the gap is reported as missed. Narrowing
  this would lose the alerts in the case it exists for, which produces the same
  readings; **Reset date** clears it.
- **FM27 arrives November 2026** and will invalidate the default coordinates.
  Re-calibration and a spreadsheet edit are the intended response.
