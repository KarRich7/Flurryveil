# Architecture

Flurryveil is a per-user WinForms app. The tray icon owns the session so closing Settings can hide it while the overlay continues. A named mutex and activation event let a second launch reopen the running Settings window, including when Windows initially launched the app in the background.

| Area | Implementation |
| --- | --- |
| App lifecycle and settings | [Program.cs](../Snowfall/Program.cs), [AppStorage.cs](../Snowfall/AppStorage.cs), [DesktopPersonalization.cs](../Snowfall/DesktopPersonalization.cs) |
| Settings, controls, preview | [SettingsForm.cs](../Snowfall/SettingsForm.cs), [SettingsControls.cs](../Snowfall/SettingsControls.cs), [WeatherPreview.cs](../Snowfall/WeatherPreview.cs) |
| Rendering and input | [SnowOverlay.cs](../Snowfall/SnowOverlay.cs), [GpuSnowRenderer.cs](../Snowfall/GpuSnowRenderer.cs), [GpuRasterLayer.cs](../Snowfall/GpuRasterLayer.cs), [SnowInteraction.cs](../Snowfall/SnowInteraction.cs) |
| Weather and pause rules | [SeasonalWeather.cs](../Snowfall/SeasonalWeather.cs), [DesktopActivity.cs](../Snowfall/DesktopActivity.cs), [GameActivity.cs](../Snowfall/GameActivity.cs), [ApplicationRules.cs](../Snowfall/ApplicationRules.cs) |
| Build and validation | [Snowfall.Tests](../Snowfall.Tests), [UI smoke runner](../eng/UiSmoke), [packaging script](../eng/Build-Release.ps1), [Windows workflow](../.github/workflows/windows.yml) |

The simulation tracks windows and monitor bounds, then applies accumulation, movement and occlusion in desktop coordinates. The preferred rendering path uses Direct3D 11 and DirectComposition. The same simulation can render through GDI if the GPU path is unavailable. Native input hit testing limits click interception to interactive snow. The overlay excludes the Settings window.

Settings are debounced and written through a temporary file in the user's existing QuietSnow directory. Older intensity-only profiles are read. If the app cannot read a profile, it keeps the source file and uses defaults. Before writing the first update, it copies the old content to a recovery JSON file. Read and write failures are recorded in a rotating local log with error types and codes.

Autostart uses the current user's Run entry. The installer registers Start and optional desktop shortcuts and keeps user settings out of the installation directory.
