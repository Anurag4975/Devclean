# DevClean — Build, Publish & Microsoft Store

## 1. Build a single .exe (direct download / sideload)

```powershell
dotnet publish -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output: `bin\Release\net10.0-windows\win-x64\publish\DevClean.exe` — one file.
Users need the .NET 10 Desktop Runtime (small web installer). For zero-dependency, use `--self-contained true` (bigger ~150MB).

## 2. Microsoft Store (MSIX)

Store takes 15% (you keep 85%). Requirements checklist:

- **No registry cleaning** — we don't do it (compliant).
- **Consent before any change** — every delete/quarantine asks first (compliant).
- **Clean uninstall** — MSIX handles it; our AppData folder (`%AppData%\DevClean`) is tiny.
- **No bundled software / no background service** — we have none (compliant).
- **Frame as "disk cleanup"**, not "system optimizer / speed-up your PC" (Store policy 10.2).
- **Format-drive feature**: hide it in the Store build (it's behind Settings → Advanced; wrap in `#if !STORE_BUILD` or remove). Launching `format.com` with elevation will fail Store certification. For Store, replace with a button that opens `diskmgmt.msc`.
- **AI (Mistral)**: must disclose in the listing that it sends file *paths* (not contents) to a third-party API, and that the user provides their own key.

### Package.appxmanifest (template)

Create a "Windows Application Packaging Project" in Visual Studio, add DevClean as reference.
Identity: `Publisher=CN=<your publisher ID from Partner Center>`, version `1.0.0.0`.
Capabilities: none needed (we only read user files + write to our own folders).
Declare no extensions. Target: Windows 10 1809+ (min) / Windows 11 (max tested).

## 3. Code signing (for direct downloads)

Buy an OV code-signing cert (~$70/yr) or use Azure Trusted Signing.
```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a DevClean.exe
```
Store signing is handled by Microsoft.

## 4. First-run telemetry-free check

- No network calls except Mistral AI (user-provided key, user-initiated).
- All data stays on-device: quarantine, settings, learning loop.

## 5. Store listing assets needed

- Screenshots: 1240×700 (desktop), at least 1.
- Store logo: 300×300.
- Short description: "The disk cleaner that never deletes anything without undo."
- Keywords: disk cleaner, cleanup, free space, junk cleaner, quarantine.
