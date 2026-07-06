using RtkSharp.Ast;
using RtkSharp.Core;

namespace RtkSharp.Tests.Ast;

/// <summary>
/// Tests for <see cref="ShellAstAnalyzer"/>. Like the rest of the <c>--level ast</c> tier this is an
/// RtkSharp-only feature with no Rust oracle, so correctness is judged against the parsed structure
/// itself: every expectation below was derived by running the analyzer over a real shell fixture and
/// inspecting the genuinely-produced output, not assumed. The analyzer is deliberately conservative —
/// it collapses only recognized function bodies and keeps all other shell verbatim — so most tests
/// assert both "signature/verbatim content kept" and "body content dropped".
/// </summary>
public sealed class ShellAstAnalyzerTests
{
    private readonly ShellAstAnalyzer _analyzer = new();

    [Fact]
    public void Language_IsShell() => Assert.Equal(Language.Shell, _analyzer.Language);

    // Regression test for a real bug caught in independent review: header slicing originally
    // subtracted tree-sitter's native BYTE offsets and applied that delta as a .NET char count
    // (Text.Substring), which mis-slices for any signature preceded by a multi-byte UTF-8
    // character (e.g. a comment or an earlier function name containing one). Fixed via
    // tree.GetText(byteStart, byteEnd), which correctly decodes byte offsets to UTF-16 chars.
    [Fact]
    public void Filter_NonAsciiBeforeFunction_DoesNotCorruptHeader()
    {
        const string code = "# café notes\ngreet() {\n    echo hi\n}\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("greet() {\n    # ...\n}", result);
    }

    [Fact]
    public void Filter_KeepsShebangLine()
    {
        const string script = "#!/bin/bash\necho hi\n";
        var result = _analyzer.Filter(script);
        Assert.Contains("#!/bin/bash", result);
    }

    [Fact]
    public void Filter_KeepsTopLevelVariableAssignment_Verbatim()
    {
        const string script = "GREETING=\"hello world\"\nPATH_VAR=/usr/local/bin\n";
        var result = _analyzer.Filter(script);
        Assert.Contains("GREETING=\"hello world\"", result);
        Assert.Contains("PATH_VAR=/usr/local/bin", result);
    }

    [Fact]
    public void Filter_KeepsTopLevelDeclarationCommand_Verbatim()
    {
        const string script = "readonly COUNT=5\nexport DEBUG=1\n";
        var result = _analyzer.Filter(script);
        Assert.Contains("readonly COUNT=5", result);
        Assert.Contains("export DEBUG=1", result);
    }

    [Fact]
    public void Filter_KeepsTopLevelCommand_Verbatim()
    {
        const string script = "echo \"starting build\"\nset -euo pipefail\n";
        var result = _analyzer.Filter(script);
        Assert.Contains("echo \"starting build\"", result);
        Assert.Contains("set -euo pipefail", result);
    }

    [Fact]
    public void Filter_CollapsesFunctionBody_ParenForm_KeepsSignature()
    {
        const string script = """
            greet() {
                local name="$1"
                echo "Hi, $name"
            }
            """;
        var result = _analyzer.Filter(script);
        Assert.Contains("greet()", result);
        Assert.Contains("# ...", result);
        Assert.DoesNotContain("local name", result);
        Assert.DoesNotContain("echo \"Hi, $name\"", result);
    }

    [Fact]
    public void Filter_CollapsesFunctionBody_FunctionKeywordForm_KeepsSignature()
    {
        const string script = """
            function build {
                make all
                make install
            }
            """;
        var result = _analyzer.Filter(script);
        Assert.Contains("function build", result);
        Assert.Contains("# ...", result);
        Assert.DoesNotContain("make all", result);
        Assert.DoesNotContain("make install", result);
    }

    [Fact]
    public void Filter_KeepsDocCommentImmediatelyBeforeFunction()
    {
        const string script = """
            # Greets the user by name.
            greet() {
                echo "hi"
            }
            """;
        var result = _analyzer.Filter(script);
        Assert.Contains("# Greets the user by name.", result);
        Assert.Contains("greet()", result);
        Assert.DoesNotContain("echo \"hi\"", result);
    }

    [Fact]
    public void Filter_KeepsMultiLineDocCommentBlockBeforeFunction()
    {
        const string script = """
            # Builds the project.
            # Requires make on PATH.
            build() {
                make all
            }
            """;
        var result = _analyzer.Filter(script);
        Assert.Contains("# Builds the project.", result);
        Assert.Contains("# Requires make on PATH.", result);
        Assert.Contains("build()", result);
    }

    [Fact]
    public void Filter_DropsStandaloneCommentNotBeforeFunction()
    {
        const string script = """
            # this is just a note
            echo hello
            """;
        var result = _analyzer.Filter(script);
        Assert.DoesNotContain("this is just a note", result);
        Assert.Contains("echo hello", result);
    }

    [Fact]
    public void Filter_DropsCommentSeparatedFromFunctionByBlankLine()
    {
        const string script = """
            # not attached to the function

            greet() {
                echo hi
            }
            """;
        var result = _analyzer.Filter(script);
        Assert.DoesNotContain("not attached to the function", result);
        Assert.Contains("greet()", result);
    }

    [Fact]
    public void Filter_DropsStandaloneCommentInsideFunctionBody()
    {
        const string script = """
            run() {
                # this comment lives inside the body
                do_work
            }
            """;
        var result = _analyzer.Filter(script);
        Assert.DoesNotContain("this comment lives inside the body", result);
        Assert.DoesNotContain("do_work", result);
        Assert.Contains("run()", result);
    }

    [Fact]
    public void Filter_KeepsTopLevelIfStatement_Verbatim()
    {
        const string script = """
            if [ -f config ]; then
                echo yes
            else
                echo no
            fi
            """;
        var result = _analyzer.Filter(script);
        // Top-level control flow (not inside a function) is kept verbatim, not summarized.
        Assert.Contains("if [ -f config ]; then", result);
        Assert.Contains("echo yes", result);
        Assert.Contains("echo no", result);
        Assert.Contains("fi", result);
    }

    [Fact]
    public void Filter_KeepsTopLevelForLoop_Verbatim()
    {
        const string script = """
            for f in *.txt; do
                cat "$f"
            done
            """;
        var result = _analyzer.Filter(script);
        Assert.Contains("for f in *.txt; do", result);
        Assert.Contains("cat \"$f\"", result);
        Assert.Contains("done", result);
    }

    [Fact]
    public void Filter_MixedScript_KeepsShebangAndAssignmentsCollapsesFunctions()
    {
        const string script = """
            #!/bin/bash
            set -euo pipefail

            VERSION="1.0"

            # Prints usage.
            usage() {
                echo "usage: $0"
                exit 1
            }

            echo "done"
            """;
        var result = _analyzer.Filter(script);
        Assert.Contains("#!/bin/bash", result);
        Assert.Contains("set -euo pipefail", result);
        Assert.Contains("VERSION=\"1.0\"", result);
        Assert.Contains("# Prints usage.", result);
        Assert.Contains("usage()", result);
        Assert.Contains("echo \"done\"", result);
        Assert.DoesNotContain("exit 1", result);
        Assert.DoesNotContain("usage: $0", result);
    }

    [Fact]
    public void Filter_FunctionBody_ReplacedWithPlaceholderBlock()
    {
        const string script = """
            greet() {
                echo hi
            }
            """;
        var result = _analyzer.Filter(script);
        // The collapsed body is a valid, empty-ish shell block containing only a comment.
        Assert.Contains("{", result);
        Assert.Contains("# ...", result);
        Assert.Contains("}", result);
    }

    [Fact]
    public void Filter_ReducesTokenCountForBodyHeavyFunction()
    {
        const string script = """
            process() {
                local input="$1"
                local output="$2"
                grep -v '^#' "$input" | sort | uniq > "$output"
                echo "processed $input into $output"
                return 0
            }
            """;
        var result = _analyzer.Filter(script);
        // Conservative summarizer, but a body-heavy function must still shrink meaningfully.
        Assert.True(result.Length < script.Length,
            $"expected summary shorter than source; got {result.Length} vs {script.Length}");
        Assert.Contains("process()", result);
    }

    [Fact]
    public void Filter_MalformedInput_FallsBackToRawContentWithoutThrowing()
    {
        // Unclosed function brace: tree-sitter reports a syntax error, so we return content as-is.
        const string malformed = "greet() {\n    echo hi\n";
        var result = _analyzer.Filter(malformed);
        Assert.Equal(malformed, result);
    }

    [Fact]
    public void Filter_EmptyInput_ReturnsEmpty() => Assert.Equal(string.Empty, _analyzer.Filter(string.Empty));

    [Fact]
    public void Filter_OnlyComments_ProducesEmpty()
    {
        const string script = "# just a comment\n# and another\n";
        var result = _analyzer.Filter(script);
        Assert.Equal(string.Empty, result);
    }
}
