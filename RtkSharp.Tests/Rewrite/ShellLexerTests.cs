using System.Linq;
using RtkSharp.Rewrite;
using Xunit;

namespace RtkSharp.Tests.Rewrite;

public class ShellLexerTests
{
    [Fact]
    public void Test_Simple_Command()
    {
        var tokens = ShellLexer.Tokenize("git status");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.Arg, tokens[0].Kind);
        Assert.Equal("git", tokens[0].Value);
        Assert.Equal("status", tokens[1].Value);
    }

    [Fact]
    public void Test_Command_With_Args()
    {
        var tokens = ShellLexer.Tokenize("git commit -m message");
        Assert.Equal(4, tokens.Count);
        Assert.Equal("git", tokens[0].Value);
        Assert.Equal("commit", tokens[1].Value);
        Assert.Equal("-m", tokens[2].Value);
        Assert.Equal("message", tokens[3].Value);
    }

    [Fact]
    public void Test_Quoted_Operator_Not_Split()
    {
        var tokens = ShellLexer.Tokenize("git commit -m \"Fix && Bug\"");
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Operator && t.Value == "&&");
        Assert.Contains(tokens, t => t.Value.Contains("Fix && Bug"));
    }

    [Fact]
    public void Test_Single_Quoted_String()
    {
        var tokens = ShellLexer.Tokenize("echo 'hello world'");
        Assert.Contains(tokens, t => t.Value == "'hello world'");
    }

    [Fact]
    public void Test_Double_Quoted_String()
    {
        var tokens = ShellLexer.Tokenize("echo \"hello world\"");
        Assert.Contains(tokens, t => t.Value == "\"hello world\"");
    }

    [Fact]
    public void Test_Empty_Quoted_String()
    {
        var tokens = ShellLexer.Tokenize("echo \"\"");
        Assert.Contains(tokens, t => t.Value == "\"\"");
    }

    [Fact]
    public void Test_Nested_Quotes()
    {
        var tokens = ShellLexer.Tokenize("echo \"outer 'inner' outer\"");
        Assert.Contains(tokens, t => t.Value.Contains("'inner'"));
    }

    [Fact]
    public void Test_Escaped_Space()
    {
        var tokens = ShellLexer.Tokenize("echo hello\\ world");
        Assert.Contains(tokens, t => t.Value.Contains("hello"));
    }

    [Fact]
    public void Test_Backslash_In_Single_Quotes()
    {
        var tokens = ShellLexer.Tokenize("echo 'hello\\nworld'");
        Assert.Contains(tokens, t => t.Value.Contains("\\n"));
    }

    [Fact]
    public void Test_Escaped_Quote_In_Double()
    {
        var tokens = ShellLexer.Tokenize("echo \"hello\\\"world\"");
        Assert.Contains(tokens, t => t.Value.Contains("hello"));
    }

    [Fact]
    public void Test_Empty_Input()
    {
        Assert.Empty(ShellLexer.Tokenize(""));
    }

    [Fact]
    public void Test_Whitespace_Only()
    {
        Assert.Empty(ShellLexer.Tokenize("   "));
    }

    [Fact]
    public void Test_Unclosed_Single_Quote()
    {
        var tokens = ShellLexer.Tokenize("'unclosed");
        Assert.NotEmpty(tokens);
    }

    [Fact]
    public void Test_Unclosed_Double_Quote()
    {
        var tokens = ShellLexer.Tokenize("\"unclosed");
        Assert.NotEmpty(tokens);
    }

    [Fact]
    public void Test_Unicode_Preservation()
    {
        var tokens = ShellLexer.Tokenize("echo \"héllo wörld\"");
        Assert.Contains(tokens, t => t.Value.Contains("héllo"));
    }

    [Fact]
    public void Test_Multiple_Spaces()
    {
        var tokens = ShellLexer.Tokenize("git   status");
        Assert.Equal(2, tokens.Count);
    }

    [Fact]
    public void Test_Leading_Trailing_Spaces()
    {
        var tokens = ShellLexer.Tokenize("  git status  ");
        Assert.Equal(2, tokens.Count);
    }

    [Fact]
    public void Test_And_Operator()
    {
        var tokens = ShellLexer.Tokenize("cmd1 && cmd2");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Operator && t.Value == "&&");
    }

    [Fact]
    public void Test_Or_Operator()
    {
        var tokens = ShellLexer.Tokenize("cmd1 || cmd2");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Operator && t.Value == "||");
    }

    [Fact]
    public void Test_Semicolon()
    {
        var tokens = ShellLexer.Tokenize("cmd1 ; cmd2");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Operator && t.Value == ";");
    }

    [Fact]
    public void Test_Multiple_And()
    {
        var tokens = ShellLexer.Tokenize("a && b && c");
        var ops = tokens.Where(t => t.Kind == TokenKind.Operator).ToList();
        Assert.Equal(2, ops.Count);
    }

    [Fact]
    public void Test_Mixed_Operators()
    {
        var tokens = ShellLexer.Tokenize("a && b || c");
        var ops = tokens.Where(t => t.Kind == TokenKind.Operator).ToList();
        Assert.Equal(2, ops.Count);
    }

    [Fact]
    public void Test_Operator_At_Start()
    {
        var tokens = ShellLexer.Tokenize("&& cmd");
        Assert.Contains(tokens, t => t.Value == "&&");
    }

    [Fact]
    public void Test_Operator_At_End()
    {
        var tokens = ShellLexer.Tokenize("cmd &&");
        Assert.Contains(tokens, t => t.Value == "&&");
    }

    [Fact]
    public void Test_Pipe_Detection()
    {
        var tokens = ShellLexer.Tokenize("cat file | grep pattern");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Pipe);
    }

    [Fact]
    public void Test_Quoted_Pipe_Not_Pipe()
    {
        var tokens = ShellLexer.Tokenize("\"a|b\"");
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Pipe);
    }

    [Fact]
    public void Test_Multiple_Pipes()
    {
        var tokens = ShellLexer.Tokenize("a | b | c");
        var pipes = tokens.Where(t => t.Kind == TokenKind.Pipe).ToList();
        Assert.Equal(2, pipes.Count);
    }

    [Fact]
    public void Test_Glob_Detection()
    {
        var tokens = ShellLexer.Tokenize("ls *.rs");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Shellism);
    }

    [Fact]
    public void Test_Quoted_Glob_Not_Shellism()
    {
        var tokens = ShellLexer.Tokenize("echo \"*.txt\"");
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Shellism);
    }

    [Fact]
    public void Test_Simple_Var_Is_Arg()
    {
        var tokens = ShellLexer.Tokenize("echo $HOME");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Arg && t.Value == "$HOME");
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Shellism);
    }

    [Fact]
    public void Test_Simple_Var_Enables_Native_Routing()
    {
        var tokens = ShellLexer.Tokenize("git log $BRANCH");
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Shellism);
    }

    [Fact]
    public void Test_Dollar_Subshell_Stays_Shellism()
    {
        var tokens = ShellLexer.Tokenize("echo $(date)");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Shellism);
    }

    [Fact]
    public void Test_Dollar_Brace_Stays_Shellism()
    {
        var tokens = ShellLexer.Tokenize("echo ${HOME}");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Shellism);
    }

    [Fact]
    public void Test_Dollar_Special_Vars_Stay_Shellism()
    {
        foreach (var s in new[] { "echo $?", "echo $$", "echo $!" })
        {
            var tokens = ShellLexer.Tokenize(s);
            Assert.Contains(tokens, t => t.Kind == TokenKind.Shellism);
        }
    }

    [Fact]
    public void Test_Dollar_Digit_Stays_Shellism()
    {
        var tokens = ShellLexer.Tokenize("echo $1");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Shellism);
    }

    [Fact]
    public void Test_Quoted_Variable_Not_Shellism()
    {
        var tokens = ShellLexer.Tokenize("echo \"$HOME\"");
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Shellism);
    }

    [Fact]
    public void Test_Backtick_Substitution()
    {
        var tokens = ShellLexer.Tokenize("echo `date`");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Shellism);
    }

    [Fact]
    public void Test_Subshell_Detection()
    {
        var tokens = ShellLexer.Tokenize("echo $(date)");
        var shellisms = tokens.Where(t => t.Kind == TokenKind.Shellism).ToList();
        Assert.NotEmpty(shellisms);
    }

    [Fact]
    public void Test_Brace_Expansion()
    {
        var tokens = ShellLexer.Tokenize("echo {a,b}.txt");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Shellism);
    }

    [Fact]
    public void Test_Escaped_Glob()
    {
        var tokens = ShellLexer.Tokenize("echo \\*.txt");
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Shellism && t.Value == "*");
    }

    [Fact]
    public void Test_Redirect_Out()
    {
        var tokens = ShellLexer.Tokenize("cmd > file");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect);
    }

    [Fact]
    public void Test_Redirect_Append()
    {
        var tokens = ShellLexer.Tokenize("cmd >> file");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect && t.Value == ">>");
    }

    [Fact]
    public void Test_Redirect_In()
    {
        var tokens = ShellLexer.Tokenize("cmd < file");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect);
    }

    [Fact]
    public void Test_Redirect_Stderr()
    {
        var tokens = ShellLexer.Tokenize("cmd 2> file");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect && t.Value.StartsWith("2>"));
    }

    [Fact]
    public void Test_Redirect_Stderr_No_Space()
    {
        var tokens = ShellLexer.Tokenize("cmd 2>/dev/null");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect && t.Value == "2>");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Arg && t.Value == "/dev/null");
    }

    [Fact]
    public void Test_Redirect_Dev_Null()
    {
        var tokens = ShellLexer.Tokenize("cmd > /dev/null");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect && t.Value == ">");
    }

    [Fact]
    public void Test_Redirect_2_To_1_Single_Token()
    {
        var tokens = ShellLexer.Tokenize("cmd 2>&1");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.Redirect, tokens[1].Kind);
        Assert.Equal("2>&1", tokens[1].Value);
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Shellism && t.Value == "&");
    }

    [Fact]
    public void Test_Redirect_1_To_2_Single_Token()
    {
        var tokens = ShellLexer.Tokenize("cmd 1>&2");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect && t.Value == "1>&2");
    }

    [Fact]
    public void Test_Redirect_Fd_Close()
    {
        var tokens = ShellLexer.Tokenize("cmd 2>&-");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect && t.Value == "2>&-");
    }

    [Fact]
    public void Test_Redirect_Shorthand_Dup()
    {
        var tokens = ShellLexer.Tokenize("cmd >&2");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect && t.Value == ">&2");
    }

    [Fact]
    public void Test_Redirect_Amp_Gt()
    {
        var tokens = ShellLexer.Tokenize("cmd &>/dev/null");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect && t.Value == "&>");
    }

    [Fact]
    public void Test_Redirect_Amp_Gt_Gt()
    {
        var tokens = ShellLexer.Tokenize("cmd &>>/dev/null");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect && t.Value == "&>>");
    }

    [Fact]
    public void Test_Combined_Redirect_Chain()
    {
        var tokens = ShellLexer.Tokenize("cmd > /dev/null 2>&1");
        var redirects = tokens.Where(t => t.Kind == TokenKind.Redirect).ToList();
        Assert.Equal(2, redirects.Count);
        Assert.Equal(">", redirects[0].Value);
        Assert.Equal("2>&1", redirects[1].Value);
    }

    [Fact]
    public void Test_Redirect_Append_To_File()
    {
        var tokens = ShellLexer.Tokenize("echo hello >> /tmp/output.txt");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect && t.Value == ">>");
    }

    [Fact]
    public void Test_Redirect_Heredoc_Marker()
    {
        var tokens = ShellLexer.Tokenize("cat <<EOF");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect && t.Value == "<<");
    }

    [Fact]
    public void Test_Redirect_2_To_1_With_Pipe()
    {
        var tokens = ShellLexer.Tokenize("cargo test 2>&1 | head");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect && t.Value == "2>&1");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Pipe);
    }

    [Fact]
    public void Test_Redirect_2_To_1_With_And()
    {
        var tokens = ShellLexer.Tokenize("cargo test 2>&1 && echo done");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect && t.Value == "2>&1");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Operator && t.Value == "&&");
    }

    [Fact]
    public void Test_Exclamation_Is_Shellism()
    {
        var tokens = ShellLexer.Tokenize("if ! grep -q pattern file; then echo missing; fi");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Shellism && t.Value == "!");
    }

    [Fact]
    public void Test_Background_Job_Is_Shellism()
    {
        var tokens = ShellLexer.Tokenize("sleep 10 &");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Shellism && t.Value == "&");
    }

    [Fact]
    public void Test_Background_Not_Confused_With_Amp_Redirect()
    {
        var tokens = ShellLexer.Tokenize("cargo test &>/dev/null");
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Shellism && t.Value == "&");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect);
    }

    [Fact]
    public void Test_Semicolon_No_Space()
    {
        var tokens = ShellLexer.Tokenize("git status;cargo test");
        Assert.Equal(1, tokens.Count(t => t.Kind == TokenKind.Operator));
        Assert.Equal(4, tokens.Count(t => t.Kind == TokenKind.Arg));
    }

    [Fact]
    public void Test_Offset_Tracking()
    {
        var tokens = ShellLexer.Tokenize("a && b");
        Assert.Equal(0, tokens[0].Offset);
        Assert.Equal(2, tokens[1].Offset);
        Assert.Equal(5, tokens[2].Offset);
    }

    [Fact]
    public void Test_Offset_Segment_Extraction()
    {
        var cmd = "git add . && cargo test";
        var tokens = ShellLexer.Tokenize(cmd);
        var op = tokens.Find(t => t.Kind == TokenKind.Operator);
        Assert.NotNull(op);
        var left = cmd.Substring(0, op.Offset).Trim();
        var rightStart = op.Offset + op.Value.Length;
        var right = cmd.Substring(rightStart).Trim();
        Assert.Equal("git add .", left);
        Assert.Equal("cargo test", right);
    }

    [Fact]
    public void Test_Env_Prefix_Is_Arg()
    {
        var tokens = ShellLexer.Tokenize("GIT_SSH_COMMAND=ssh git push");
        Assert.Equal(TokenKind.Arg, tokens[0].Kind);
        Assert.Equal("GIT_SSH_COMMAND=ssh", tokens[0].Value);
    }

    [Fact]
    public void Test_Complex_Compound()
    {
        var tokens = ShellLexer.Tokenize("cargo fmt --all && cargo clippy --all-targets && cargo test");
        var operators = tokens.Where(t => t.Kind == TokenKind.Operator).ToList();
        Assert.Equal(2, operators.Count);
        Assert.True(operators.All(t => t.Value == "&&"));
    }

    [Fact]
    public void Test_Find_Pipe_Xargs()
    {
        var tokens = ShellLexer.Tokenize("find . -name '*.rs' | xargs grep 'fn run'");
        var pipeIdx = tokens.FindIndex(t => t.Kind == TokenKind.Pipe);
        Assert.True(pipeIdx > 0);
        var beforePipe = tokens.Take(pipeIdx).Where(t => t.Kind == TokenKind.Arg).ToList();
        Assert.Contains(beforePipe, t => t.Value == "find");
    }

    [Fact]
    public void Test_Fd_Redirect_Needs_Adjacent_Digit()
    {
        var tokens = ShellLexer.Tokenize("echo 2 > file");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Arg && t.Value == "2");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect && t.Value == ">");
    }

    [Fact]
    public void Test_Fd_Redirect_No_Space()
    {
        var tokens = ShellLexer.Tokenize("echo 2>file");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect && t.Value == "2>");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Arg && t.Value == "file");
    }

    [Fact]
    public void Test_Shell_Split_Simple()
    {
        var expected = new[] { "head", "-50", "file.php" };
        Assert.Equal(expected, ShellLexer.ShellSplit("head -50 file.php"));
    }

    [Fact]
    public void Test_Shell_Split_Double_Quotes()
    {
        var expected = new[] { "git", "log", "--format=%H %s" };
        Assert.Equal(expected, ShellLexer.ShellSplit("git log --format=\"%H %s\""));
    }

    [Fact]
    public void Test_Shell_Split_Single_Quotes()
    {
        var expected = new[] { "grep", "-r", "hello world", "." };
        Assert.Equal(expected, ShellLexer.ShellSplit("grep -r 'hello world' ."));
    }

    [Fact]
    public void Test_Shell_Split_Single_Word()
    {
        Assert.Equal(new[] { "ls" }, ShellLexer.ShellSplit("ls"));
    }

    [Fact]
    public void Test_Shell_Split_Empty()
    {
        Assert.Empty(ShellLexer.ShellSplit(""));
    }

    [Fact]
    public void Test_Shell_Split_Backslash_Escape()
    {
        var expected = new[] { "echo", "hello world" };
        Assert.Equal(expected, ShellLexer.ShellSplit("echo hello\\ world"));
    }

    [Fact]
    public void Test_Shell_Split_Unclosed_Quote()
    {
        var expected = new[] { "echo", "hello" };
        Assert.Equal(expected, ShellLexer.ShellSplit("echo 'hello"));
    }

    [Fact]
    public void Test_Shell_Split_Mixed_Quotes()
    {
        var expected = new[] { "echo", "it's", "a \"test\"" };
        Assert.Equal(expected, ShellLexer.ShellSplit("echo \"it's\" 'a \"test\"'"));
    }

    [Fact]
    public void Test_Shell_Split_Tabs()
    {
        Assert.Equal(new[] { "a", "b", "c" }, ShellLexer.ShellSplit("a\tb\tc"));
    }

    [Fact]
    public void Test_Shell_Split_Multiple_Spaces()
    {
        Assert.Equal(new[] { "a", "b", "c" }, ShellLexer.ShellSplit("a   b   c"));
    }

    [Fact]
    public void Test_Strip_Quotes_Double()
    {
        Assert.Equal("hello", ShellLexer.StripQuotes("\"hello\""));
    }

    [Fact]
    public void Test_Strip_Quotes_Single()
    {
        Assert.Equal("hello", ShellLexer.StripQuotes("'hello'"));
    }

    [Fact]
    public void Test_Strip_Quotes_None()
    {
        Assert.Equal("hello", ShellLexer.StripQuotes("hello"));
    }

    [Fact]
    public void Test_Strip_Quotes_Mismatched()
    {
        Assert.Equal("\"hello'", ShellLexer.StripQuotes("\"hello'"));
    }

    [Fact]
    public void Test_Split_On_Operators_Stop_At_Pipe()
    {
        Assert.Equal(new[] { "a" }, ShellLexer.SplitOnOperators("a | b | c", true));
        Assert.Equal(new[] { "a", "b" }, ShellLexer.SplitOnOperators("a && b | c", true));
    }

    [Fact]
    public void Test_Split_On_Operators_Through_Pipes()
    {
        Assert.Equal(new[] { "a", "b", "c" }, ShellLexer.SplitOnOperators("a | b | c", false));
        Assert.Equal(new[] { "a", "b", "c", "d" }, ShellLexer.SplitOnOperators("a && b | c ; d", false));
    }

    [Fact]
    public void Test_Split_On_Operators_Quoted()
    {
        Assert.Equal(new[] { "echo \"a && b\"", "cargo test" }, ShellLexer.SplitOnOperators("echo \"a && b\" && cargo test", false));
    }

    [Fact]
    public void Test_Split_On_Operators_Empty()
    {
        Assert.Empty(ShellLexer.SplitOnOperators("", false));
        Assert.Empty(ShellLexer.SplitOnOperators("  ", true));
    }

    [Fact]
    public void Test_Unattestable_Backtick()
    {
        Assert.True(ShellLexer.ContainsUnattestableConstruct("git status `whoami`"));
    }

    [Fact]
    public void Test_Unattestable_Command_Substitution()
    {
        Assert.True(ShellLexer.ContainsUnattestableConstruct("git log --pretty=$(rm -rf ~)"));
    }

    [Fact]
    public void Test_Unattestable_Process_Substitution()
    {
        Assert.True(ShellLexer.ContainsUnattestableConstruct("diff <(secret) <(other)"));
        Assert.True(ShellLexer.ContainsUnattestableConstruct("tee >(cat)"));
    }

    [Fact]
    public void Test_Unattestable_Substitution_Inside_Double_Quotes()
    {
        Assert.True(ShellLexer.ContainsUnattestableConstruct("git log --pretty=\"$(rm -rf ~)\""));
        Assert.True(ShellLexer.ContainsUnattestableConstruct("git log --pretty=\"`rm -rf ~`\""));
        Assert.True(ShellLexer.ContainsUnattestableConstruct("git -c x=\"$(whoami)\" status"));
    }

    [Fact]
    public void Test_Attestable_Substitution_Inside_Single_Quotes()
    {
        Assert.False(ShellLexer.ContainsUnattestableConstruct("echo '$(rm -rf ~)'"));
        Assert.False(ShellLexer.ContainsUnattestableConstruct("echo '`whoami`'"));
        Assert.False(ShellLexer.ContainsUnattestableConstruct("echo \"\\$(rm -rf ~)\""));
    }

    [Fact]
    public void Test_Unattestable_File_Redirects()
    {
        Assert.True(ShellLexer.ContainsUnattestableConstruct("git log > /tmp/x"));
        Assert.True(ShellLexer.ContainsUnattestableConstruct("echo evil >> ~/.bashrc"));
        Assert.True(ShellLexer.ContainsUnattestableConstruct("cmd &> /tmp/x"));
        Assert.True(ShellLexer.ContainsUnattestableConstruct("cat < /etc/passwd"));
        Assert.True(ShellLexer.ContainsUnattestableConstruct("cat << EOF"));
    }

    [Fact]
    public void Test_Unattestable_Ampersand_File_Redirect()
    {
        Assert.True(ShellLexer.ContainsUnattestableConstruct("git status >& /tmp/evil"));
        Assert.True(ShellLexer.ContainsUnattestableConstruct("cat x >&~/.bashrc"));
        Assert.True(ShellLexer.ContainsUnattestableConstruct("echo hi 2>& /tmp/evil"));
    }

    [Fact]
    public void Test_Attestable_Fd_Dup_And_Devnull_Redirects()
    {
        Assert.False(ShellLexer.ContainsUnattestableConstruct("git status 2>&1"));
        Assert.False(ShellLexer.ContainsUnattestableConstruct("cmd >&2"));
        Assert.False(ShellLexer.ContainsUnattestableConstruct("cmd 2>&-"));
        Assert.False(ShellLexer.ContainsUnattestableConstruct("cmd 2>/dev/null"));
        Assert.False(ShellLexer.ContainsUnattestableConstruct("cmd > /dev/null"));
        Assert.False(ShellLexer.ContainsUnattestableConstruct("cmd &> /dev/null"));
        Assert.False(ShellLexer.ContainsUnattestableConstruct("cmd >& /dev/null"));
    }

    [Fact]
    public void Test_Attestable_Subshell_And_Separators()
    {
        Assert.False(ShellLexer.ContainsUnattestableConstruct("(git status; cargo build)"));
        Assert.False(ShellLexer.ContainsUnattestableConstruct("git status && cargo build"));
        Assert.False(ShellLexer.ContainsUnattestableConstruct("git status; cargo build"));
        Assert.False(ShellLexer.ContainsUnattestableConstruct("git log | head"));
        Assert.False(ShellLexer.ContainsUnattestableConstruct("sleep 1 &"));
        Assert.False(ShellLexer.ContainsUnattestableConstruct("git status\ncargo build"));
    }

    [Fact]
    public void Test_Attestable_Variable_Expansion()
    {
        Assert.False(ShellLexer.ContainsUnattestableConstruct("echo $HOME"));
        Assert.False(ShellLexer.ContainsUnattestableConstruct("echo ${HOME}"));
        Assert.False(ShellLexer.ContainsUnattestableConstruct("git status"));
        Assert.False(ShellLexer.ContainsUnattestableConstruct(""));
    }

    [Fact]
    public void Test_Split_Perms_Operators()
    {
        Assert.Equal(new[] { "git status", "cargo build" }, ShellLexer.SplitForPermissions("git status && cargo build"));
        Assert.Equal(new[] { "git status", "cargo build" }, ShellLexer.SplitForPermissions("git status; cargo build"));
        Assert.Equal(new[] { "git log", "head" }, ShellLexer.SplitForPermissions("git log | head"));
    }

    [Fact]
    public void Test_Split_Perms_Newline()
    {
        Assert.Equal(new[] { "git status", "cargo build" }, ShellLexer.SplitForPermissions("git status\ncargo build"));
    }

    [Fact]
    public void Test_Split_Perms_Background_Ampersand()
    {
        Assert.Equal(new[] { "git status", "rm -rf ~" }, ShellLexer.SplitForPermissions("git status & rm -rf ~"));
        Assert.Equal(new[] { "sleep 1" }, ShellLexer.SplitForPermissions("sleep 1 &"));
    }

    [Fact]
    public void Test_Split_Perms_Subshell()
    {
        Assert.Equal(new[] { "git status", "cargo build" }, ShellLexer.SplitForPermissions("(git status; cargo build)"));
        Assert.Equal(new[] { "a", "b", "c" }, ShellLexer.SplitForPermissions("((a; b); c)"));
    }

    [Fact]
    public void Test_Split_Perms_Truncates_At_Redirect()
    {
        Assert.Equal(new[] { "git status" }, ShellLexer.SplitForPermissions("git status 2>&1"));
        Assert.Equal(new[] { "git log" }, ShellLexer.SplitForPermissions("git log > /tmp/x"));
        Assert.Equal(new[] { "git push --force" }, ShellLexer.SplitForPermissions("git push --force 2>&1"));
    }

    [Fact]
    public void Test_Split_Perms_Newline_Inside_Quotes_Not_Split()
    {
        var segments = ShellLexer.SplitForPermissions("echo 'line1\nline2'");
        Assert.Single(segments);
        Assert.StartsWith("echo", segments[0]);
    }

    [Fact]
    public void Test_Split_Perms_Empty()
    {
        Assert.Empty(ShellLexer.SplitForPermissions(""));
        Assert.Empty(ShellLexer.SplitForPermissions("   "));
    }
}
