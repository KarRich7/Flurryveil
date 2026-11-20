# Changelog

## 1.0.1

- Confined snowball and snowman interactions to Snow mode. Switching weather releases an active drag and clears the previous snowballs.
- Reduced leaf and blossom buildup on windows. Piles grow from actual landings, and the first landed particle remains visible at the smallest pile size.
- Fixed resuming effects after the display turns back on while the app is running in the tray.

## 1.0.0

- Renamed «Тихий снег» to Flurryveil and added original app, tray and installer artwork.
- Reorganized settings into Weather, Effects, Performance and Preferences. Added a compact navigation rail, light and dark themes, keyboard navigation and a preview of the active weather.
- Preserved the existing data location and read intensity presets from older settings files. Invalid settings now produce a local diagnostic. The file is retained until the next edit, when it is backed up before writing changes.
- Normalized executable names in saved pause and garland rules. Fixed rendering resource lifetimes when Windows recreates overlay handles.
- Decoupled window activation from the frame clock so the app can reopen Settings while weather effects are paused.
- Moved to .NET 10 LTS, added a per-user Windows installer and a self-contained portable build, and added automated Windows validation and tag-based packaging.
