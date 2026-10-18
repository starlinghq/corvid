using Corvid.Approvals.Shell;

namespace Corvid.Approvals.Tests;

public sealed class ParserTests
{
    private static ShellScript Parsed(string text) => Assert.IsType<ShellParsed>(ShellParser.Parse(text)).Script;

    private static string Unsupported(string text) => Assert.IsType<ShellUnsupported>(ShellParser.Parse(text)).Reason;

    private static ShellCommand Single(string text) => Assert.Single(Assert.Single(Parsed(text).Items).Pipeline.Commands);

    [Fact]
    public void Lists_pipelines_and_connectors()
    {
        var script = Parsed("cd build && make -j4 || echo failed; ls | wc -l & sleep 1\necho done");
        Assert.Equal([ShellConnector.None, ShellConnector.And, ShellConnector.Or, ShellConnector.Sequence, ShellConnector.Sequence, ShellConnector.Sequence],
            script.Items.Select(item => item.Connector));
        Assert.Equal(2, script.Items[3].Pipeline.Commands.Length);
        Assert.True(script.Items[3].Background);
    }

    [Fact]
    public void Quotes_and_escapes_are_removed_and_tracked()
    {
        var command = Single("""r''m "-r"f \/ 'a b' "x $HOME y" $'\x72\155\n'""");
        Assert.Equal(["rm", "-rf", "/", "a b"], command.Words.Take(4).Select(word => word.Literal));
        Assert.All(command.Words[2].Parts, part => Assert.True(part.Quoted));
        var mixed = command.Words[4];
        Assert.Null(mixed.Literal);
        Assert.Contains(mixed.Parts, part => part is ParameterPart { Name: "HOME", Quoted: true });
        Assert.Equal("rm\n", command.Words[5].Literal);
    }

    [Fact]
    public void Assignments_tilde_and_substitutions()
    {
        var command = Single("FOO=bar X=~/y dd of=~/disk if=$(cat `echo src`)");
        Assert.Equal(["FOO", "X"], command.Assignments.Select(a => a.Name));
        Assert.IsType<TildePart>(command.Assignments[1].Value.Parts[0]);
        Assert.IsType<TildePart>(command.Words[1].Parts[1]);
        var substitution = Assert.IsType<SubstitutionPart>(command.Words[2].Parts[1]);
        var inner = Assert.Single(Assert.Single(substitution.Script.Items).Pipeline.Commands);
        Assert.IsType<SubstitutionPart>(inner.Words[1].Parts[0]);
    }

    [Fact]
    public void Redirections_here_strings_and_here_documents()
    {
        var command = Single("cat <<'EOF' > out.txt 2>&1 <<< \"word\"\nline $HOME\nEOF\n");
        Assert.Equal([RedirectKind.HereDocument, RedirectKind.Output, RedirectKind.Duplicate, RedirectKind.HereString],
            command.Redirects.Select(r => r.Kind));
        Assert.Equal("line $HOME\n", command.Redirects[0].Target.Literal);
        Assert.Equal(2, command.Redirects[2].Descriptor);

        var expanding = Single("cat <<EOF\nhome is $HOME\nEOF");
        Assert.Contains(expanding.Redirects[0].Target.Parts, part => part is ParameterPart { Name: "HOME" });
        Assert.Equal(RedirectKind.OutputAndError, Single("make &> log.txt").Redirects[0].Kind);
        Assert.Equal(RedirectKind.OutputAndError, Single("make >& log.txt").Redirects[0].Kind);
    }

    [Theory]
    [InlineData("if true; then rm -rf /; fi", "'if'")]
    [InlineData("for f in *; do rm $f; done", "'for'")]
    [InlineData("while true; do :; done", "'while'")]
    [InlineData("case $x in a) rm -rf /;; esac", "'case'")]
    [InlineData("f() { rm -rf /; }; f", "function definition")]
    [InlineData("function f { rm -rf /; }", "'function'")]
    [InlineData("(cd / && rm -rf *)", "subshell")]
    [InlineData("{ rm -rf /; }", "'{'")]
    [InlineData("((x = 1))", "subshell")]
    [InlineData("echo $((1 + 2))", "arithmetic")]
    [InlineData("rm -rf /{bin,etc}", "brace expansion")]
    [InlineData("rm -rf {a..z}", "brace expansion")]
    [InlineData("diff <(ls a) <(ls b)", "process substitution")]
    [InlineData("tee >(rm -rf /)", "process substitution")]
    [InlineData("rm -rf ${HOME:-/}", "operators")]
    [InlineData("rm -rf ${#x}", "operators")]
    [InlineData("rm -rf ~root", "tilde")]
    [InlineData("echo 'unterminated", "unterminated single quote")]
    [InlineData("echo \"unterminated", "unterminated double quote")]
    [InlineData("echo $(ls", "unterminated command substitution")]
    [InlineData("echo `ls", "unterminated backquote")]
    [InlineData("rm -rf / &&", "missing command")]
    [InlineData("cat <<EOF\nno end", "not terminated")]
    [InlineData("arr=(a b); rm ${arr[0]}", "subshell")]
    [InlineData("echo $'\\cA'", "control-character")]
    [InlineData("[[ -f x ]] && rm x", "'[['")]
    [InlineData("echo $\"hello\"", "locale")]
    public void Refuses_what_is_outside_the_subset(string text, string reason)
    {
        Assert.Contains(reason, Unsupported(text));
    }

    [Fact]
    public void Nesting_is_bounded()
    {
        var deep = string.Concat(Enumerable.Repeat("$(echo ", ShellParser.MaxDepth + 2)) + "x" + new string(')', ShellParser.MaxDepth + 2);
        Assert.Contains("nested deeper", Unsupported(deep));
        Assert.Contains("longer than", Unsupported(new string('a', ShellParser.MaxLength + 1)));
        Assert.Contains("NUL", Unsupported("echo a\0b"));
    }

    [Fact]
    public void Comments_and_continuations()
    {
        var script = Parsed("ls # rm -rf /\necho a\\\nb");
        Assert.Equal(2, script.Items.Length);
        Assert.Single(script.Items[0].Pipeline.Commands[0].Words);
        Assert.Equal("ab", script.Items[1].Pipeline.Commands[0].Words[1].Literal);
        Assert.Equal("a#b", Single("echo a#b").Words[1].Literal);
    }
}
