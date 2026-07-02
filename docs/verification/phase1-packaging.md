# Phase 1 Packaging Verification

Verified on: 2026-07-02 14:00 (local system clock, `Get-Date` output: "02 July 2026 14:00:02")
SDK: 10.0.301 (confirmed via `dotnet --version` → `10.0.301`; `dotnet --list-sdks` also shows 8.0.124, 10.0.103, and 10.0.300 installed alongside it, no `global.json` pin present)
Host: Windows (win-x64)

## `dotnet pack`

Command: `dotnet clean RtkSharp.slnx` (Step 1, run first to avoid stale artifacts)
Result: PASS — exit code 0, `Build succeeded. 0 Warning(s) 0 Error(s)`.

Command: `dotnet pack RtkSharp/RtkSharp.csproj -c Release -o .artifacts/packages`
Result: PASS — exit code 0. Output:

```
Determining projects to restore...
Restored C:\Development\rtksharp\RtkSharp\RtkSharp.csproj (in 3.59 sec).
RtkSharp -> C:\Development\rtksharp\RtkSharp\bin\Release\net10.0\RtkSharp.dll
RtkSharp -> C:\Development\rtksharp\RtkSharp\bin\Release\net10.0\RtkSharp.dll
RtkSharp -> C:\Development\rtksharp\RtkSharp\bin\Release\net10.0\publish\
The package RtkSharp.1.0.0 is missing a readme. Go to https://aka.ms/nuget/authoring-best-practices/readme to learn why package readmes are important.
Successfully created package 'C:\Development\rtksharp\.artifacts\packages\RtkSharp.1.0.0.nupkg'.
```

Note: NuGet emitted a non-fatal warning that the package is missing a readme (`NU5039`-style advisory, not an error) — cosmetic, does not affect tool installability. No `PackageReadmeFile` is configured yet; out of scope for this verification task.

## Package contents

Package inspected: `.artifacts/packages/RtkSharp.1.0.0.nupkg`, expanded to `.artifacts/packages/_inspect/`.

`tools/` folder layout: **`tools/net10.0/any/`** — matches the expected default layout. `PublishAot=true` in `RtkSharp.csproj` did **not** change the pack output layout to a RID-specific folder; `dotnet pack` still produces the standard framework-dependent `any` folder containing a regular managed DLL (not a native AOT executable). Full file listing under `tools/net10.0/any/`:

```
DotnetToolSettings.xml
RtkSharp.deps.json
RtkSharp.dll
RtkSharp.pdb
RtkSharp.runtimeconfig.json
Spectre.Console.Ansi.dll
Spectre.Console.dll
```

`DotnetToolSettings.xml` command name: `rtk` (confirmed via `<Command Name="rtk" ... />`)
Entry point / assembly: `RtkSharp.dll`, `Runner="dotnet"` — confirming `AssemblyName=RtkSharp` took effect.

Full contents of `DotnetToolSettings.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<DotNetCliTool Version="1">
  <Commands>
    <Command Name="rtk" EntryPoint="RtkSharp.dll" Runner="dotnet" />
  </Commands>
</DotNetCliTool>
```

## Notes

- No deviation from the expected `tools/net10.0/any/` layout was found. `PublishAot=true` does not affect `dotnet pack`/`dotnet tool` packaging — AOT-related properties only take effect when `dotnet publish` is invoked directly; the tool package still ships a standard IL DLL run via the `dotnet` runner, which is required for `dotnet tool install`/`dnx` to work correctly.
- `PackageId=RtkSharp` produced package file `RtkSharp.1.0.0.nupkg` (version defaulted to `1.0.0` — no `<Version>` property is set in `RtkSharp.csproj`; this is expected default MSBuild/NuGet behavior, not a defect).
- `.artifacts/` was not previously covered by `.gitignore` (confirmed via `git check-ignore -v .artifacts/packages` returning no match). Added `.artifacts/` to `.gitignore` in this same commit so the `.nupkg` and `_inspect/` folder stay untracked.
- No production code changes were made for this task.
