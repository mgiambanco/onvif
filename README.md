# OvifViewer

A Windows desktop NVR viewer for ONVIF-compatible IP cameras. Displays multiple live RTSP streams in a resizable, draggable grid with PTZ controls, camera discovery, and persistent layout.

## Requirements

- Windows 10/11 (x64)
- .NET 9 runtime

## Features

### Camera Management

**Discovery**
- Scans the local network via ONVIF WS-Discovery and lists found devices with manufacturer, model, and firmware version
- Credentials and stream profile are configured per camera before adding

**Manual add**
- Add a camera by entering its ONVIF device service URL (`http://<ip>/onvif/device_service`) and display name

**Edit / Manage**
- Right-click any camera in the sidebar list to edit, reconnect, or remove it
- *Cameras → Manage Cameras…* opens a table of all configured cameras with the same edit and remove options
- Editable fields: display name, username, password, stream profile, PTZ enabled, auto-connect on startup
- *Test & Load Profiles* button verifies credentials and populates the stream profile dropdown

**Export / Import**
- *Cameras → Export Camera Config…* saves all cameras to a portable JSON file (passwords stored in plaintext — handle with care)
- *Cameras → Import Camera Config…* reads a previously exported file; passwords are re-encrypted with DPAPI for the current user; cameras already present by ID are skipped

### Live View

**Multi-camera grid**
- All configured cameras with *Auto-connect* enabled are loaded on startup
- Each camera is rendered in its own resizable, draggable panel using LibVLC

**Grid layout presets**
- Toolbar above the canvas: **Auto**, **1×1**, **2×2**, **3×3**, **4×4**
- Auto tiles cameras using a square-root heuristic; numbered presets fix the column count and compute rows from camera count
- *View → Reset Layout* returns to Auto and clears saved panel positions

**Manual layout**
- Drag a camera panel by its title bar to reposition it
- Resize from the right edge, bottom edge, or corners
- Positions are saved automatically (500 ms debounce) and restored on next launch

**Full-screen**
- Click a camera name in the sidebar to expand that camera to fill the canvas
- Click again or press Escape to return to the previous layout
- Double-click a panel's title bar has the same effect

**Per-panel overlay** (appears on hover)
- Title bar with camera name, drag-to-move, close button
- Resize handles on the right edge, bottom edge, and corners
- Right-click menu: Reconnect, Show in Zoom Panel, Remove Camera
- Connection status dot (bottom-right): orange = connecting, green = live, red = error

### Zoom Panel

- Right-click a camera panel overlay → *Show in Zoom Panel*, or click *Show in Zoom Panel* from the overlay context menu
- Opens a floating, borderless window positioned next to the main window
- Drag via the title bar; resize via the grip in the bottom-right corner
- Streams the camera independently of the grid panel
- Shows PTZ controls if the camera supports PTZ (▲ ◄ ■ ► ▼ + Z+ / Z−); hold a direction button to move, release to stop

### PTZ Controls

- Available in both the Zoom Panel and the per-panel overlay (when PTZ is enabled for the camera)
- Continuous move on mouse-down, stop on mouse-up
- Stop button (■) sends an explicit stop command

### Auto-Reconnect

- Each camera stream retries automatically on error using an exponential back-off (up to 30 s between attempts) via Polly
- *View → Reconnect All* manually stops and restarts every stream

### Sidebar

- Lists all active cameras with display name and IP address; IP is color-coded by status
- Collapsible via the ◀ button; re-expand with the ▶ strip on the left edge
- Resizable via the splitter between the sidebar and the canvas

## Settings

Settings are stored in `%AppData%\OvifViewer\settings.json`. Passwords are encrypted with DPAPI (current user scope) and are not portable across Windows user accounts. Use Export / Import to move cameras between machines.

Logs are written to `%AppData%\OvifViewer\logs\` with daily rotation and a 7-day retention window.

## Building

```
dotnet build src/OvifViewer/OvifViewer.csproj -c Release
```

Output: `src/OvifViewer/bin/Release/net9.0-windows/OvifViewer.exe`

## Dependencies

| Package | Purpose |
|---|---|
| LibVLCSharp.WinForms + VideoLAN.LibVLC.Windows | RTSP stream rendering |
| SharpOnvifClient | ONVIF device/media/PTZ operations |
| Polly | Retry policy for stream reconnect |
| Serilog | Structured logging to rolling file |
| Microsoft.Extensions.DependencyInjection | Service container |
