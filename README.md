# IP Network Monitor

Windows 10/11 x64 WinForms application. Five independent IP monitoring lanes, editable lane names, multiline clipboard import/export, adaptive four-attempt ping logic, outage history, and Excel-friendly tab-separated report copying.

## Monitoring behavior
- Each lane runs independently and walks its own IP list from top to bottom, then repeats.
- Each IP gets one ping first. On success, the remaining attempts are skipped.
- On failure, up to three more attempts are sent, with a 2-second delay between failed attempts. A 1.3-second ping timeout is used.
- Four consecutive failures open one outage record. Repeated offline cycles do not create duplicate records.
- The first successful ping closes the outage and records recovery time and duration.
- Open outages and settings are persisted under `%LOCALAPPDATA%\IPNetworkMonitor`, and are resumed/reconciled after restart.
- Internet reachability is checked independently every 10 seconds against 1.1.1.1 (shared status, since all five lanes use the same host network).
- Paste accepts IPs separated by newlines, tabs, spaces, commas, semicolons or pipes. Invalid and duplicate entries are skipped and counted.

## Build locally
Install .NET 8 SDK on Windows, then run:

```powershell
dotnet publish .\IPNetworkMonitor\IPNetworkMonitor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o .\publish
```

The executable is `publish\IPNetworkMonitor.exe`.

## Build with GitHub Actions
1. Create a GitHub repository and upload the contents of this folder.
2. Open **Actions** and run **Build Windows portable EXE** (or push to `main`).
3. Download the artifact named `IPNetworkMonitor-win-x64` from the completed workflow run.

No EXE is included in this source package because this build environment does not have the Windows .NET SDK/toolchain. The GitHub Actions workflow builds it on a Windows runner.
