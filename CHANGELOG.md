# Changelog

What changed in each release, newest first. Each release's notes on GitHub (and in the update) are its section here, so a version can't be published without one (see `build-installer.ps1`). Keep each bullet on one line: GitHub's release page shows line breaks as they are.

## 1.4.0 (2026-11-09)

### New
- Monitor speakers: click a monitor's tile and choose *Set as default* to play Windows' sound through its speakers.
- Disconnect Bluetooth earbuds, headphones and controllers from their tile: click the tile, then *Disconnect*. They stay paired.
- A pinned Bluetooth headset or earbuds that's disconnected shows a *Connect* button on its tile, like Windows' own.

## 1.3.2 (2026-11-09)

### Fixed
- Monitor brightness and volume work again: in 1.3.1 the monitor's controls stopped opening while LightHub's lighting service was running.

## 1.3.1 (2026-11-09)

### Fixed
- Monitor brightness and volume now wait their turn with LightHub's lighting service too (the one that lights your PC from the lock screen), so the two never use the graphics card's connection at the same moment.

## 1.3.0 (2026-11-03)

### New
- A DualSense on Bluetooth lists the devices it's paired with: click its tile to see what each PS + △ ○ ✕ □ button connects to, with the one in use marked.
- *Look up names* in that list asks the other devices for their names (they need to be switched on and nearby) and remembers them.

## 1.2.0 (2026-11-02)

### New
- A DualSense on its cable shows the vibration waves on its tile: haptics and adaptive triggers already work in games when it's wired.

### Fixed
- A DualSense on a phone charger over Bluetooth showed 100%; it now shows its real level.
- The *Full DualSense features over Bluetooth* switch only shows while the HapticLink app is running (HapticLink 1.2.0 turns itself off without it).

## 1.1.1 (2026-11-02)

### Fixed
- The keyboard tile's *Open LightHub* button finds LightHub again now that LightHub has its own installer (it lives in a new folder).

## 1.1.0 (2026-11-02)

### New
- Works with HapticLink for DualSense (a separate app that makes a DualSense's haptics, adaptive triggers, rumble and lights work in games over Bluetooth): when it's installed, the menu has a *Full DualSense features over Bluetooth* switch that turns it on and off, and your controller's tile keeps its battery and shows animated vibration waves while games use it. HapticLink's virtual controller doesn't get a tile of its own.

## 1.0.6 (2026-11-01)

### Improved
- Monitor brightness and volume now take turns with LightHub on the graphics driver's connection, so the two apps never use it at the same moment (a GPU lighting chip locked up once while both were running). If LightHub is busy for more than a second, that one monitor reading is skipped and tried again on the next round.

## 1.0.5 (2026-11-01)

### New
- **Ctrl+Alt+P** opens and closes the panel from anywhere. If another app already uses that shortcut, the log says so and the tray icon still works.
- **"Set as default" for Bluetooth earbuds and headsets** (Nothing Ear (2) and any other Bluetooth audio device): click the tile to open its drawer, then press the button to play Windows' sound through it. It switches the speakers only and leaves your mic as it is, since a Bluetooth mic in use drops the earbuds to call-quality sound. For headsets that show Windows both a stereo and a hands-free output, the stereo one is used. USB headsets keep switching speakers and mic.

### For plugin authors
- Plugin API 4: `CoreAudio` and `AudioEndpoint` (Windows' sound devices and the default-device switch) are in the SDK, and Bluetooth device records carry their Windows container id.

## 1.0.4 (2026-11-01)

### Improved
- A connected PlayStation controller no longer adds background memory churn: its reports (up to ~250 a second) are read without a copy each. About 2–4 MB less memory while a controller is connected.

### For plugin authors
- Plugin API 3: `HidConnection.ReportSpanReceived` hands out input reports without copying them.

## 1.0.3 (2026-10-31)

### Improved
- Open tiles are followed live (the earbuds' noise control, monitor brightness and volume) only while the panel or widget is on screen and the PC isn't locked. Otherwise the app falls back to its once-a-minute checks, so it no longer keeps the earbuds' connection open all day.

## 1.0.2 (2026-10-31)

### Improved
- Lower memory use: tiles refresh only when a device actually changes, instead of on every scan.

## 1.0.1 (2026-10-31)

### Improved
- Memory use roughly halved: the app draws on the CPU instead of the graphics card (skipping the driver's buffers) and uses a garbage collector setting that keeps the heap small.

## 1.0.0 (2026-10-31)

- First public release. Copies older than this (1.x up to 1.12.0, from before the public repository) need to be uninstalled first; later releases update normally.
