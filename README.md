<div align="center">

<img src="public/DesktopKeyboard.png" alt="DesktopKeyboard logo" width="200"/>

# Desktop Keyboard

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square)](https://opensource.org/licenses/MIT)
[![GitHub stars](https://img.shields.io/github/stars/serifpersia/DesktopKeyboard.svg?style=flat-square)](https://github.com/serifpersia/DesktopKeyboard/stargazers)

</div align="center">

**Desktop Keyboard** is a simple on screen keyboard that automatically hides and unhides during input events.


## Getting Started
### Installation
1. Download it [here](https://github.com/serifpersia/DesktopKeyboard/releases).
2. Extract the contents of the zip and run `setup.exe` to install Desktop Keyboard on your system.

### Usage
- Run it and interact with input elements on windows system or applications

## Requirements
- Windows 10 or Windows 11 (x64 architecture).
- [.NET 9.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (x64). The installer stays compact by linking against the shared runtime rather than bundling it.

## Building from Source

### Prerequisites
- **.NET 9.0 SDK** — [download](https://aka.ms/dotnet/download)

### App only
```powershell
dotnet build DesktopKeyboard.csproj -c Release
```

### App + Installer (WiX — no Visual Studio required)

WiX is a free, CLI-based installer toolchain. One-time setup:

```powershell
# Install the WiX tool
dotnet tool install --global wix

# Accept the EULA
wix eula accept wix7

# Install required WiX extensions
wix extension add WixToolset.UI.wixext
wix extension add WixToolset.Util.wixext
```

Then build everything with the included script:

```powershell
.\build.ps1
```

The installer is output to `DesktopKeyboard.Installer\bin\DesktopKeyboard_Setup.msi`.

The main app project (`DesktopKeyboard.csproj`) can still be opened and built directly in Visual Studio — only the installer is CLI/WiX-only.

## License
This project is licensed under the **MIT License**. See the [LICENSE](LICENSE) file for details.
