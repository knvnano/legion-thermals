# Installer build

Install.NET10 SDK and NSIS3 on Windows. From the repository root:

```powershell
pwsh -File LegionThermals/installer/Build-Installer.ps1 -Version 1.3.0
```

For a non-default NSIS location, pass `-Compiler "C:\path\to\makensis.exe"`.
The script publishes a self-contained Windows x64 payload, collects third-party licenses from exact NuGet packages, generates bilingual Unicode RTF and an exact uninstall file list, then emits the EXE and SHA256 file under `LegionThermals/dist/`.

Installer UI: English/Russian. Uninstall preserves settings and unrelated files. Silent install requires `/S /ACCEPTEULA=1`; closing the old app before installation/update is required. The installer does not silently enable startup.

Generated payloads, RTF, uninstall lists and distribution files are ignored by Git. Do not ship application settings or diagnostic output.
