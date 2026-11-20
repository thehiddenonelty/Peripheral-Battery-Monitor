# Building BatteryBar Device Edition 0.1.3

## Platform
- Windows 10 or Windows 11
- Windows .NET Framework 4.x with C# compiler (`csc.exe`) available at `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (or Framework 32-bit equivalent)

## Build and launch
1. Clone or download this repository on Windows.
2. Double-click `Start-DeviceEdition.cmd`. If the versioned executable is absent, the script compiles the `.cs` files in the repository root, runs `--self-test`, and launches the program.
3. After compilation, you can start `BatteryBar-DeviceEdition-0.1.3.exe` directly.

## Other modes
- `Preview-Demo.cmd` launches demo mode.
- `Diagnose-BatteryBar.cmd` creates `BatteryBar-Device-Diagnostics.txt` in the application folder. Review diagnostic output before posting it publicly.

## Fonts
HarmonyOS Sans files from the original development archive are **not included** in this source package pending confirmation of applicable font redistribution terms. The app uses the `Microsoft YaHei UI` system font as a fallback if font resources are unavailable. Thus UI appearance may differ from the original release.

## Known limits
Device detection depends on the USB/Bluetooth connection mode, firmware, receiver and supported report protocol. `Compatibility.md` records implemented paths; entries are not necessarily hardware-tested in this build.
