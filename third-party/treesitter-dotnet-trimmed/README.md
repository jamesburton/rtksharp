# RtkSharp.TreeSitter.Trimmed

A size-trimmed redistribution of [`TreeSitter.DotNet`](https://github.com/mariusgreuel/tree-sitter-dotnet-bindings)
1.3.0, MIT-licensed, upstream commit `8cae484bc033dac6e492ed15166877f3d784850f`.

## Why this exists

Upstream `TreeSitter.DotNet` bundles 28+ native tree-sitter grammars per RID
(~68 MB for `win-x64` alone). RtkSharp's `RtkSharp/Ast/AstAnalyzerRegistry.cs`
only ever loads **9** of them — C# is Roslyn-based (`CSharpAstAnalyzer`) and
JavaScript is Acornima-based (`JavaScriptAstAnalyzer`), so neither touches
`TreeSitter.DotNet` at all. The other 19+ grammars (Verilog, Razor, Scala,
Haskell, Julia, OCaml, PHP, Swift, HTML, CSS, JSON, TOML, ...) are dead weight
that ships with every build regardless.

This package repackages the same upstream binaries (managed assembly and
build props untouched), keeping only:

- `tree-sitter` (the core native runtime, not a grammar)
- `tree-sitter-c`, `-cpp`, `-go`, `-java`, `-python`, `-ruby`, `-rust`,
  `-bash`, `-typescript`

Result: native payload drops from ~68 MB (31 files) to ~13.6 MB (10 files)
per RID; the packed nupkg drops from ~51 MB to ~5.5 MB.

## Regenerating

Run `build-trimmed-package.ps1` from PowerShell. It requires:

- `TreeSitter.DotNet` 1.3.0 already restored into the local NuGet cache
  (`dotnet restore` against the upstream package once is enough)
- `nuget.exe` on `PATH` (ships with Visual Studio / available via
  `C:\Windows\System32\nuget.exe` on most dev machines)

It writes `local-feed/RtkSharp.TreeSitter.Trimmed.<version>.nupkg`, which
`NuGet.Config` at the repo root references as the `rtksharp-local` package
source.

**Re-run this script when:**

- Upgrading the upstream `TreeSitter.DotNet` version — bump
  `$SourceNupkg`/`$PackageVersion` in the script and regenerate.
- Adding a new tree-sitter-backed language to `AstAnalyzerRegistry.cs` — add
  its grammar name (the string literal passed to
  `new TreeSitter.Language("...")` / `new TsLanguage("...")`) to
  `$KeepGrammars` in the script, or the new analyzer's native DLL will be
  silently missing at runtime.

## License

MIT, same as upstream. Copyright (c) 2025 Marius Greuel. See
`local-feed`'s packed `PACKAGE.md`/license metadata, carried through
unmodified from the original package.
