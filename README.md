<p align="center">
  <img src="logo.png" alt="Salad XRay Logo" width="100%" />
</p>

<h1 align="center">🕵️‍♂️ 💻</h1>

<p align="center">
  <strong>Simple X-Ray glasses for your Salad node. Just to see what's happening under the hood!</strong>
</p>

## 🛠️ How to Build / Compiling from Source

If you want to compile Salad XRay from the source code, you'll need the [.NET 8.0 SDK](https://dotnet.microsoft.com/en-us/download) (or newer) installed on your machine.

> ⚠️ **IMPORTANT:** Please open **PowerShell as Administrator** before running the commands below.

### 1. Clone the repository

In your elevated PowerShell window, run:

```powershell
git clone https://github.com/YourUsername/SaladXRayPanel.git
cd SaladXRayPanel
```

### 2. Install Required Dependencies

This project relies on a couple of NuGet packages for the UI and system hardware readings:

```powershell
dotnet add package Spectre.Console
dotnet add package System.Management
```

### 3. Build as a Standalone Executable (Recommended)

To make it easy to run without requiring users to install the .NET runtime, publish it as a single, self-contained executable for Windows x64:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

### 4. Locate your .exe

Once the build is complete, your fresh `SaladXRayPanel.exe` will be located in: 
`\bin\Release\net8.0\win-x64\publish\`

Just run the `.exe` as **Administrator** (to ensure it can read all hardware sensors properly) and enjoy the X-Ray vision!
