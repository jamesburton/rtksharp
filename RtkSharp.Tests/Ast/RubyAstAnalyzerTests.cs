using RtkSharp.Ast;
using RtkSharp.Core;

namespace RtkSharp.Tests.Ast;

/// <summary>
/// Tests for <see cref="RubyAstAnalyzer"/>. As with the C# analyzer there is no Rust oracle for
/// this RtkSharp-only <c>--level ast</c> tier, so correctness is judged against the parsed
/// structure itself: every expectation below was derived by running the analyzer over a real Ruby
/// snippet (via the native tree-sitter Ruby grammar) and inspecting the genuinely-observed output —
/// not assumed — per this project's "verify, don't guess" discipline.
/// </summary>
public sealed class RubyAstAnalyzerTests
{
    private readonly RubyAstAnalyzer _analyzer = new();

    [Fact]
    public void Language_IsRuby() => Assert.Equal(Language.Ruby, _analyzer.Language);

    [Fact]
    public void Filter_KeepsRequireStatements()
    {
        const string code = "require 'json'\nrequire_relative 'foo/bar'\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("require 'json'", result);
        Assert.Contains("require_relative 'foo/bar'", result);
    }

    [Fact]
    public void Filter_KeepsModuleHeaderAndNestedMembers()
    {
        const string code = "module Greetings\n  def hello\n    puts 'hi'\n  end\nend\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("module Greetings", result);
        Assert.Contains("def hello", result);
        Assert.Contains("end", result);
    }

    [Fact]
    public void Filter_KeepsClassHeaderWithSuperclass()
    {
        const string code = "class Widget < Base\n  def name\n    @name\n  end\nend\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("class Widget < Base", result);
    }

    [Fact]
    public void Filter_KeepsMethodSignature_CollapsesBody()
    {
        const string code = "class Foo\n  def add(a, b)\n    sum = a + b\n    sum\n  end\nend\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("def add(a, b)", result);
        Assert.DoesNotContain("sum = a + b", result);
        Assert.Contains("# ...", result);
    }

    [Fact]
    public void Filter_KeepsSingletonMethodSignature()
    {
        const string code = "class Factory\n  def self.build(config)\n    new(config)\n  end\nend\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("def self.build(config)", result);
        Assert.DoesNotContain("new(config)", result);
    }

    [Fact]
    public void Filter_KeepsMethodWithoutParentheses()
    {
        const string code = "class Foo\n  def bar\n    do_something\n  end\nend\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("def bar", result);
        Assert.DoesNotContain("do_something", result);
    }

    [Fact]
    public void Filter_KeepsAttrAccessorVerbatim()
    {
        const string code = "class Widget\n  attr_accessor :name, :size\n  attr_reader :id\n  attr_writer :secret\nend\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("attr_accessor :name, :size", result);
        Assert.Contains("attr_reader :id", result);
        Assert.Contains("attr_writer :secret", result);
    }

    [Fact]
    public void Filter_KeepsRdocCommentImmediatelyBeforeMethod()
    {
        const string code = "class Foo\n  # Adds two numbers\n  def add(a, b)\n    a + b\n  end\nend\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("# Adds two numbers", result);
        Assert.Contains("def add(a, b)", result);
    }

    [Fact]
    public void Filter_KeepsMultiLineRdocCommentBlockBeforeClass()
    {
        const string code = "# A widget.\n# It does things.\nclass Widget\n  def go\n    run\n  end\nend\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("# A widget.", result);
        Assert.Contains("# It does things.", result);
        Assert.Contains("class Widget", result);
    }

    [Fact]
    public void Filter_KeepsRdocCommentBeforeModule()
    {
        const string code = "# Top-level docs\nmodule Api\n  def call\n    1\n  end\nend\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("# Top-level docs", result);
        Assert.Contains("module Api", result);
    }

    [Fact]
    public void Filter_DropsStandaloneCommentSeparatedByBlankLine()
    {
        const string code = "# not attached to anything\n\ndef foo\n  x = 1\nend\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("not attached", result);
        Assert.Contains("def foo", result);
    }

    [Fact]
    public void Filter_DropsCommentInsideMethodBody()
    {
        const string code = "def foo\n  x = 1\n  # inline explanation\n  x + 1\nend\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("inline explanation", result);
        Assert.Contains("def foo", result);
    }

    [Fact]
    public void Filter_DropsCommentBeforeNonDeclaration()
    {
        const string code = "# describes the constant\nVERSION = '1.0'\n";
        var result = _analyzer.Filter(code);
        Assert.DoesNotContain("describes the constant", result);
        Assert.Contains("VERSION = '1.0'", result);
    }

    [Fact]
    public void Filter_CollapsesBodyWithNestedEndKeywords_KeepsCorrectMatchingEnd()
    {
        const string code = """
            def process(items)
              items.each do |i|
                case i
                when 1
                  puts 'one'
                end
              end
              done
            end
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("def process(items)", result);
        Assert.DoesNotContain("items.each", result);
        Assert.DoesNotContain("when 1", result);
        Assert.DoesNotContain("puts 'one'", result);

        // Exactly one `def` collapsed to exactly one placeholder + one `end`: the nested do/case
        // ends were inside the discarded body span and must not surface as stray `end`s.
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("def process(items)", lines[0].Trim());
        Assert.Equal("# ...", lines[1].Trim());
        Assert.Equal("end", lines[2].Trim());
    }

    [Fact]
    public void Filter_NestedClassInModule_CollapsesInnerMethodBodies()
    {
        const string code = """
            module Outer
              class Inner
                def method_one
                  compute
                end
              end
            end
            """;
        var result = _analyzer.Filter(code);
        Assert.Contains("module Outer", result);
        Assert.Contains("class Inner", result);
        Assert.Contains("def method_one", result);
        Assert.DoesNotContain("compute", result);
    }

    [Fact]
    public void Filter_KeepsUnicodeIdentifiersAndDocComments()
    {
        const string code = "# Café ☕ doc\ndef greet(名前)\n  puts 'x'\nend\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("# Café ☕ doc", result);
        Assert.Contains("def greet(名前)", result);
        Assert.DoesNotContain("puts", result);
    }

    [Fact]
    public void Filter_EndlessMethod_KeptVerbatim()
    {
        const string code = "class C\n  def square(x) = x * x\nend\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("def square(x) = x * x", result);
    }

    [Fact]
    public void Filter_TopLevelDef_CollapsesBody()
    {
        const string code = "def standalone\n  a = 1\n  b = 2\n  a + b\nend\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("def standalone", result);
        Assert.DoesNotContain("a = 1", result);
        Assert.Contains("# ...", result);
    }

    [Fact]
    public void Filter_UnhandledTopLevelStatement_KeptVerbatim()
    {
        const string code = "CONFIG = { timeout: 30 }\n";
        var result = _analyzer.Filter(code);
        Assert.Contains("CONFIG =", result);
        Assert.Contains("timeout: 30", result);
    }

    [Fact]
    public void Filter_MalformedInput_FallsBackToRawContentWithoutThrowing()
    {
        const string malformed = "def def def {{{ !!! not ruby\n";
        var result = _analyzer.Filter(malformed);
        Assert.Equal(malformed, result);
    }

    [Fact]
    public void Filter_EmptyInput_ReturnsEmpty() => Assert.Equal(string.Empty, _analyzer.Filter(string.Empty));

    [Fact]
    public void Filter_ExercisesFullFixture_ProducesExpectedShape()
    {
        const string code = """
            require 'json'

            # The widget module.
            module Widgets
              # A widget.
              class Widget < Base
                attr_accessor :name

                # Builds a widget.
                def initialize(name)
                  @name = name
                  # noise
                  validate!
                end

                def self.default
                  new('anon')
                end
              end
            end
            """;
        var result = _analyzer.Filter(code);

        Assert.Contains("require 'json'", result);
        Assert.Contains("# The widget module.", result);
        Assert.Contains("module Widgets", result);
        Assert.Contains("# A widget.", result);
        Assert.Contains("class Widget < Base", result);
        Assert.Contains("attr_accessor :name", result);
        Assert.Contains("# Builds a widget.", result);
        Assert.Contains("def initialize(name)", result);
        Assert.Contains("def self.default", result);

        // Bodies and body-internal noise gone.
        Assert.DoesNotContain("@name = name", result);
        Assert.DoesNotContain("validate!", result);
        Assert.DoesNotContain("# noise", result);
        Assert.DoesNotContain("new('anon')", result);
    }
}
