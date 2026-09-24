; rttsh installer script for Inno Setup 6
; Build via installer\build-installer.ps1 (dotnet publish + ISCC)

#define MyAppName "rttsh"
#define MyAppVersion "0.3.1"
#define MyAppPublisher "rttsh"

; Flavor is passed in by build-installer.ps1 (/DFlavor=...); default for manual compiles.
#ifndef Flavor
#define Flavor "framework"
#endif

[Setup]
AppId={{093654A6-E575-4899-A366-4D1C8F01CFBB}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\rttsh.exe
OutputDir=dist
OutputBaseFilename=rttsh-{#MyAppVersion}-{#Flavor}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; ---- Flat layout: exe and libraries side by side in {app} ----
[Files]
Source: "publish\App\rttsh.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\App\rttsh.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\App\rttsh.deps.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\App\rttsh.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\App\rttsh.core.dll"; DestDir: "{app}"; Flags: ignoreversion
; ELF reader behind RTT symbol resolution (--elf)
Source: "publish\App\ELFSharp.dll"; DestDir: "{app}"; Flags: ignoreversion
; Lua binding for the script subcommand: NLua -> KeraLua -> native lua54
Source: "publish\App\NLua.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\App\KeraLua.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\App\lua54.dll"; DestDir: "{app}"; Flags: ignoreversion
; CLI parser (managed, no native parts). Its localized satellites (cs\ de\ ...)
; stay unshipped: the CLI surface is English-only by design.
Source: "publish\App\System.CommandLine.dll"; DestDir: "{app}"; Flags: ignoreversion
