# Verification

## Local Windows checks

| Check | Result |
| --- | --- |
| Release builds | App and regression runner built with warnings treated as errors. |
| Full regression runner | 1,617 assertions passed with GPU and native Windows input checks enabled. |
| Forced GDI run | 903 assertions passed. GPU-only cases were skipped by design. |
| Dependency audit | NuGet reported no known vulnerable packages in the restored app or test dependency graphs. |
| UI rendering | Captured live settings in light and dark themes and a compact 486 × 408 window. The available Windows monitors were both set to 100% scaling. |
| UI smoke runner | Passed 11 groups, including both Windows close commands, activation of the original process, graceful exit, restart persistence and recovery from malformed settings. |
| Layout simulation | Checked 100%, 125%, 150% and 200% scaling at two window sizes. Each of the four sections fit horizontally, and Ctrl+1 through Ctrl+4 selected the corresponding section. |

The [UI smoke runner](../eng/UiSmoke) uses temporary settings and checks weather selection, pause and resume, the 30/60 FPS setting, theme changes, navigation and process lifecycle. Its screenshots use the application's real window. The [social preview](preview/social-preview.png) includes one of those captures.

A short local GPU benchmark ran each weather mode twice, with two 1920 × 1080 monitors and density and falling speed at 200%. It measured 59.98–60.16 FPS and reported zero presentation errors. The run covers a single machine and workload.

Both distribution files matched the release SHA-256 manifest. The release installer blocked setup while the legacy app held the shared mutex. The lifecycle test used a separately compiled installer with a test mutex and the final app payload so the existing legacy process could keep running. The test passed fresh install, a 0.9.0 fixture upgrade to 1.0.0, both executable launches, Start and desktop shortcuts, settings retention, legacy autostart migration, and uninstall.

The workflow YAML passed Actionlint. GitHub Actions execution and release publishing require an authorized remote repository.

## Reproduce

Use the [development commands](../CONTRIBUTING.md) to build and test, then use [eng/Test-Release.ps1](../eng/Test-Release.ps1) from a Windows test profile to exercise the installer and portable zip. The full [Windows workflow](../.github/workflows/windows.yml) also checks Git whitespace and bundles the distributables.
