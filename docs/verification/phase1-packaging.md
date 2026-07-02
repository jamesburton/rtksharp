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

## `dnx` execution

Confirmed flag (from `dnx --help` on SDK 10.0.301): **neither `-y` nor `--yes` appears in the documented `--help` output**, and no confirmation prompt occurs by default when running `dnx <packageId> ... -- <args>` from a local `--add-source`. However, both `-y` and `--yes` **are silently accepted as valid (undocumented) flags** — confirmed by contrast: `dnx -y` (no packageId) fails with `Required argument missing for command: 'dnx'` (i.e. `-y` was consumed as a recognized option, not treated as the packageId), whereas `dnx --bogus-flag` (a genuinely unrecognized flag) fails differently, with `Unhandled exception: Invalid package id : `--bogus-flag`` (the unrecognized token is treated as the positional `<packageId>` argument). So `-y`/`--yes` are real, silently-accepted options on this build, just omitted from the printed `--help` text.

Full `dnx --help` output:

```
Description:
  Executes a tool from source without permanently installing it.

Usage:
  dotnet dnx <packageId> [<commandArguments>...] [options]

Arguments:
  <packageId>         Package reference in the form of a package identifier like 'dotnetsay' or package identifier and version separated by '@' like 'dotnetsay@2.1.7'.
  <commandArguments>  Arguments forwarded to the tool

Options:
  --version <VERSION>       The version of the tool package to install.
  --allow-roll-forward      Allow a .NET tool to roll forward to newer versions of the .NET runtime if the runtime it targets isn't installed. [default: False]
  --prerelease              Include pre-release packages. [default: False]
  --configfile <FILE>       The NuGet configuration file to use.
  --source <SOURCE>         Replace all NuGet package sources to use during installation with these.
  --add-source <ADDSOURCE>  Add an additional NuGet package source to use during installation.
  -v, --verbosity <LEVEL>   Set the MSBuild verbosity level. Allowed values are q[uiet], m[inimal], n[ormal], d[etailed], and diag[nostic]. [default: normal]
  --disable-parallel        Prevent restoring multiple projects in parallel. [default: False]
  --ignore-failed-sources   Treat package source failures as warnings. [default: False]
  --no-http-cache           Do not cache packages and http requests. [default: False]
  --interactive             Allows the command to stop and wait for user input or action (for example to complete authentication). [default: False]
  -?, -h, --help            Show command line help.
```

Commands run and results (local source added via `dotnet nuget add source (Resolve-Path .artifacts/packages).Path --name rtksharp-local`, then removed afterward per Step 5):

- `dnx RtkSharp --add-source <path> -- --version` (no flag at all): **PASS** — exit 0, output `RtkSharp 1.0.0`.
- `dnx RtkSharp --add-source <path> -- --help`: **PASS** — exit 0, output matched `RtkProgram.GetHelp()`:
  ```
  RtkSharp - reduce command output while preserving command behavior.

  Usage:
    rtk [options] -- <command> [args]
    rtk [options] <command> [args]

  Options:
    -v, --verbose         Increase diagnostic output.
    -u, --ultra-compact   Prefer the most compact summaries.
        --no-color        Disable color output.
    -h, --help            Show help.
        --version         Show version.
  ```
- `dnx RtkSharp --add-source <path> -- git --version`: **PASS** — exit 0, output `git version 2.54.0.windows.1` (host machine's real `git --version`, confirming `ProcessExecutor`/`PathResolver` passthrough works end-to-end from the packed, `dnx`-executed tool).
- `dnx -y RtkSharp --add-source <path> -- --version`: **PASS** — exit 0, output `RtkSharp 1.0.0` (identical to no-flag run; `-y` accepted without error).
- `dnx --yes RtkSharp --add-source <path> -- --version`: **PASS** — exit 0, output `RtkSharp 1.0.0` (identical to no-flag run; `--yes` accepted without error).
- `dnx --bogus-flag RtkSharp --add-source <path> -- --version` (negative control, not part of the required suite): **FAIL as expected** — exit 1, `Unhandled exception: Invalid package id : `--bogus-flag`` — confirms unrecognized single/double-dash tokens are swallowed into the positional `<packageId>` slot rather than rejected as an option error, and confirms by contrast that `-y`/`--yes` are NOT going through that same "unrecognized token" path.

Recommended documented invocation for RtkSharp README/INSTALL: `dnx -y RtkSharp -- <command> [args]` (include `-y` for forward/backward compatibility with SDK versions where `dnx` does prompt for confirmation on first use of an unlisted/local source; it is a harmless no-op on 10.0.301 where no prompt occurs). For this local-source verification specifically, the full form used was: `dnx -y RtkSharp --add-source <path-to-.artifacts/packages> -- <command> [args]`.
