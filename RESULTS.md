# Measured Token Savings — RtkSharp (.NET rebuild)

Real measurements, not estimates. Run against this repository itself, built locally from source
(`dotnet build -c Release`, run via `dotnet RtkSharp/bin/Release/net10.0/RtkSharp.dll`) on
2026-07-11 and 2026-07-12. The `-l aggressive` and `dotnet build` rows below were captured
before-and-after fixing two real bugs found via this same measurement process (see Observations);
both fixes shipped in the published **v1.0.1** stable release, not v1.0.0.

## Methodology

- **Bytes**: raw stdout byte count (`wc -c`) — the most direct proxy for LLM token cost, since
  tokenizers operate on the byte/character stream, not on whitespace-delimited words.
- **Words**: whitespace-split word count (`wc -w`), the same "tokens" proxy this project's own
  test suite uses (`count_tokens` in `.claude/rules/cli-testing.md`: `split_whitespace().count()`).
  Included for comparability with that convention, but it's an imperfect proxy — see the `find`/
  `wc` rows below for a case where it visibly diverges from the byte-count reality.
- Every pair ran against the same repository state, back-to-back, so results are directly
  comparable to each other but will differ on other repos/machines depending on file counts, git
  history, and OS path lengths.

## Results

| Command | Raw bytes | rtk bytes | Byte savings | Raw words | rtk words | Word savings |
|---------|----------:|----------:|--------------:|----------:|----------:|--------------:|
| `git status` | 1,507 | 1,182 | **21.6%** | 83 | 44 | **47.0%** |
| `git log -20` (vs `-n 20`) | 20,079 | 6,727 | **66.5%** | 2,028 | 787 | **61.2%** |
| `git diff` (uncommitted) | 21,971 | 14,535 | **33.9%** | 2,744 | 1,775 | **35.3%** |
| `find *.cs` (ripgrep glob vs `rtk find`) | 2,496 | 903 | **63.8%** | 63 | 66 | -4.8% |
| `grep "^using "` (`rg` vs `rtk grep`) | 21,033 | 13,730 | **34.7%** | 630 | 413 | **34.4%** |
| `wc -l *.cs` (63 files) | 2,950 | 1,615 | **45.3%** | 128 | 127 | 0.8% |
| `read FilterRegistry.cs` (default level) | 25,336 | 25,336 | 0% | 2,423 | 2,423 | 0% |
| `read FilterRegistry.cs -l minimal` | 25,336 | 11,949 | **52.8%** | 2,423 | 984 | **59.4%** |
| `read FilterRegistry.cs -l aggressive` | 25,336 | 2,081 | **91.8%** | 2,423 | 197 | **91.9%** |
| `read GainCommand.cs -l aggressive` | 39,695 | 3,617 | **90.9%** | 3,489 | 380 | **89.1%** |
| `dotnet build` (clean, no warnings/errors) | 269 | 64 | **76.2%** | 22 | 10 | **54.5%** |
| `dotnet build RtkSharp.slnx` (5-project solution, up-to-date/no-op) | 689 | 64 | **90.7%** | 34 | 10 | **70.6%** |

## Observations

- **`git log` is the single biggest win** in this set: 66.5% byte reduction by collapsing each
  commit's full author/date/message block down to `hash message`.
- **`find`/`wc` show a real gap between byte and word savings.** Both strip the common directory
  prefix from every path (`RtkSharp/Commands\Analytics\GainCommand.cs` → `Analytics\GainCommand.cs`),
  which cuts bytes substantially but barely changes word *count* (still one path = one word each;
  they just got shorter). `find`'s word count even went up slightly — `rtk find` prints a few extra
  summary words. This is exactly why byte count is the more trustworthy proxy for LLM token cost:
  a whitespace-word-count metric alone would make `find` look like it *didn't* help, when it cut
  total byte volume by nearly two-thirds.
- **`rtk read` with no `-l` flag makes zero changes** — this is expected, not a bug: the default
  filter level is a verbatim passthrough (only `-n`/line-numbering is applied at that tier). Real
  reduction requires an explicit level: `-l minimal` cut this file by more than half.
- **`-l aggressive` on `.cs` files was broken, then fixed, in this same testing pass.** The first
  measurement run found it produced near-empty/garbage output on real C# files (root cause: its
  signature/import regexes were ported verbatim from the Rust oracle and only recognize
  Rust/Python/JS-family keywords — `fn`/`def`/`func`, `use `/`import ` — none of which appear in
  C#, so `public static class Foo`/`using System;` matched nothing and almost the entire file was
  treated as disposable "body" content). Since `Language.CSharp` has no Rust oracle counterpart
  (an RtkSharp-only addition), C#-specific signature/import patterns were added
  (`RtkSharp.Filters/Core/SourceFilter.cs`'s `AggressiveFilter`, gated on `language ==
  Language.CSharp` so every other language's oracle-verified output is untouched) — see
  `RtkSharp.Filters.Tests/Core/SourceFilterTests.cs` for the new coverage. Re-measured after the
  fix: **91.8%/90.9%** byte savings on the same two files that previously produced near-nothing.
  One known remaining limitation, documented in the tests rather than silently left unexplained: a
  class-body-scope field/const declaration that isn't itself a recognized signature (e.g. `private
  const int MaxCount = 10;` sitting directly inside a class body) is still dropped — a
  pre-existing heuristic characteristic shared with every other language's brace-tracking, not
  something touched by this fix.
- **`dotnet build`'s 76%/54.5% savings come from an already-clean, warning-free build** — `rtk
  dotnet build` collapses that whole case down to a single `ok` summary line. This is the smallest
  possible raw log for that command; the relative savings on a build with real warnings/errors
  would be larger in absolute terms (more to group/dedupe) even if the percentage varies, since
  RtkSharp's dotnet filter groups by file/error-code rather than growing linearly with warning
  count. Not measured here — a real dirty/failing build would be a good follow-up sample.
- **The 5-project solution build (`RtkSharp.slnx`) row demonstrates a real bug fix, not just
  savings.** Before this session's fix, an up-to-date/no-op multi-project build's console text
  names every project only via a `<Project> -> <output>` completion line — no `.csproj` path
  appears anywhere — so the project-counting logic (which only matched `.csproj` paths) silently
  undercounted this exact repo's own solution as `1 projects` regardless of its true size. It now
  correctly reports `5 projects`. `dotnet restore` has the identical undercount for the same
  no-op case and was NOT fixed — an already-satisfied restore's console text carries no
  per-project signal at all (not even a completion line, unlike build), so there's nothing for a
  text-based fix to key off; accurate restore counting needs the deferred MSBuild binary log (see
  `docs/parity/compatibility-ledger.md`). `rtk dotnet restore RtkSharp.slnx` on this same
  already-restored repo still reports `0 projects`. Byte/word savings on the build row are still
  real and substantial
  (90.7%/70.6%), but the more important fix here was *correctness*, not compression — a wrong
  project count is a wrong project count no matter how compact the line around it is.
- **No command tested here made output larger** except word-count-only artifacts (`find`) noted
  above, which is a metric quirk, not a real regression — byte count for every single command
  tested was equal or smaller.

## Reproducing

```bash
# Build once
dotnet build RtkSharp/RtkSharp.csproj -c Release

RTK="dotnet RtkSharp/bin/Release/net10.0/RtkSharp.dll"

# Example: compare git log
git log -20 > raw.txt
$RTK git log -n 20 > rtk.txt
wc -c raw.txt rtk.txt   # bytes
wc -w raw.txt rtk.txt   # words
```

Swap in any command from the [README's Commands section](README.md#commands) and any repo/file to
reproduce against your own codebase — absolute numbers will vary with file counts, commit message
length, and directory depth, but the same shape of savings (grouping, path-prefix stripping,
truncation) should hold.
