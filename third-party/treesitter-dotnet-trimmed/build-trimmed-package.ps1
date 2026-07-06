<#
.SYNOPSIS
    Rebuilds a size-trimmed redistribution of the TreeSitter.DotNet NuGet package,
    keeping only the native grammars RtkSharp's AstAnalyzerRegistry actually loads.

.DESCRIPTION
    TreeSitter.DotNet 1.3.0 bundles 28+ native tree-sitter grammars per RID (~68MB for
    win-x64 alone). RtkSharp's tree-sitter-backed analyzers only ever load 9 of them
    (C# is Roslyn-based, JavaScript is Acornima-based -- neither touches tree-sitter).
    This script repackages the upstream nupkg (MIT-licensed,
    https://github.com/mariusgreuel/tree-sitter-dotnet-bindings), keeping the managed
    assembly and build props untouched, and dropping every native grammar library not
    in $KeepGrammars from every runtimes/<rid>/native/ folder.

    Re-run this script whenever:
      - TreeSitter.DotNet is upgraded to a new upstream version, or
      - RtkSharp\Ast\AstAnalyzerRegistry.cs registers a new tree-sitter-backed language
        (update $KeepGrammars to match the grammar name used in that analyzer's
        `new TreeSitter.Language("...")` / `new TsLanguage("...")` call).

.EXAMPLE
    pwsh ./build-trimmed-package.ps1
#>
[CmdletBinding()]
param(
    [string]$SourceNupkg = "$env:USERPROFILE\.nuget\packages\treesitter.dotnet\1.3.0\treesitter.dotnet.1.3.0.nupkg",
    [string]$OutputFeed = "$PSScriptRoot\local-feed",
    [string]$PackageId = "RtkSharp.TreeSitter.Trimmed",
    [string]$PackageVersion = "1.3.0-rtksharp.1"
)

$ErrorActionPreference = 'Stop'

# Grammar base names (as they appear in "tree-sitter-<name>.<ext>") actually loaded by
# RtkSharp\Ast\*AstAnalyzer.cs. Keep in sync with AstAnalyzerRegistry.cs.
$KeepGrammars = @('c', 'cpp', 'go', 'java', 'python', 'ruby', 'rust', 'bash', 'typescript')

if (-not (Test-Path $SourceNupkg)) {
    throw "Source nupkg not found: $SourceNupkg (restore TreeSitter.DotNet 1.3.0 into the NuGet cache first)"
}

$work = Join-Path $env:TEMP "treesitter-trim-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    $sourceZip = Join-Path $work 'source.zip'
    Copy-Item $SourceNupkg $sourceZip
    $extracted = Join-Path $work 'extracted'
    Expand-Archive -Path $sourceZip -DestinationPath $extracted -Force

    $beforeBytes = (Get-ChildItem "$extracted\runtimes" -Recurse -File | Measure-Object -Property Length -Sum).Sum

    # Build a regex that matches "tree-sitter" (core runtime, no suffix) or
    # "tree-sitter-<kept-grammar>", so e.g. "cpp" doesn't accidentally match "cp".
    $escaped = ($KeepGrammars | ForEach-Object { [regex]::Escape($_) }) -join '|'
    $keepPattern = "^tree-sitter(-($escaped))?\."

    $removed = 0
    Get-ChildItem "$extracted\runtimes" -Recurse -File -Include '*.dll', '*.so', '*.dylib' | ForEach-Object {
        if ($_.Name -notmatch $keepPattern) {
            Remove-Item $_.FullName -Force
            $removed++
        }
    }

    $afterBytes = (Get-ChildItem "$extracted\runtimes" -Recurse -File | Measure-Object -Property Length -Sum).Sum
    Write-Host ("Trimmed native payload: {0:N1} MB -> {1:N1} MB ({2} files removed)" -f
        ($beforeBytes / 1MB), ($afterBytes / 1MB), $removed)

    # Drop the original OPC signing/metadata parts - not needed for a local-feed package
    # and would otherwise carry a stale signature over content we've changed.
    Remove-Item "$extracted\.signature.p7s", "$extracted\package", "$extracted\_rels", "$extracted\[Content_Types].xml" `
        -Recurse -Force -ErrorAction SilentlyContinue

    $nuspecPath = Join-Path $extracted "$PackageId.nuspec"
    @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd">
  <metadata>
    <id>$PackageId</id>
    <version>$PackageVersion</version>
    <title>TreeSitter.DotNet (RtkSharp-trimmed)</title>
    <authors>Marius Greuel; repackaged for RtkSharp</authors>
    <license type="expression">MIT</license>
    <licenseUrl>https://licenses.nuget.org/MIT</licenseUrl>
    <readme>PACKAGE.md</readme>
    <projectUrl>https://github.com/mariusgreuel/tree-sitter-dotnet-bindings</projectUrl>
    <description>
      Size-trimmed redistribution of TreeSitter.DotNet 1.3.0 (MIT, upstream commit
      8cae484bc033dac6e492ed15166877f3d784850f). Managed assembly and build props are
      untouched; native grammar libraries are pruned to only the set RtkSharp registers:
      $($KeepGrammars -join ', '). See third-party/treesitter-dotnet-trimmed/README.md
      for provenance and how to regenerate this package.
    </description>
    <copyright>Copyright (c) 2025 Marius Greuel</copyright>
    <tags>treesitter tree-sitter parser csharp dotnet bindings trimmed</tags>
    <repository type="git" url="https://github.com/mariusgreuel/tree-sitter-dotnet-bindings.git" commit="8cae484bc033dac6e492ed15166877f3d784850f" />
    <dependencies>
      <group targetFramework=".NETStandard2.0" />
    </dependencies>
  </metadata>
</package>
"@ | Set-Content -Path $nuspecPath -Encoding utf8

    New-Item -ItemType Directory -Path $OutputFeed -Force | Out-Null
    & nuget.exe pack $nuspecPath -OutputDirectory $OutputFeed -NoDefaultExcludes -NoPackageAnalysis
    if ($LASTEXITCODE -ne 0) {
        throw "nuget pack failed with exit code $LASTEXITCODE"
    }

    Write-Host "Wrote $OutputFeed\$PackageId.$PackageVersion.nupkg"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
