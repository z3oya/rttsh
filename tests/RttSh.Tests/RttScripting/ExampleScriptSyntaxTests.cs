using System.Text;
using Toolbox.Tools.RttCli.Scripting;

namespace Toolbox.Tests.RttScripting;

/// <summary>Compiles every example test script through the real Lua 5.4 engine
/// (rttsh_lua_native.dll) without running it: load() parses the embedded source
/// and a syntax error surfaces with the script's own line numbers. The example
/// suite needs a target board to RUN - this pins that the checked-in scripts
/// stay loadable without one.</summary>
public class ExampleScriptSyntaxTests
{
    public static IEnumerable<object[]> ExampleScripts()
    {
        foreach (string path in Directory.EnumerateFiles(FindTestsDir(), "t*.lua").OrderBy(p => p))
            yield return new object[] { path };
    }

    [Theory]
    [MemberData(nameof(ExampleScripts))]
    public void Example_script_compiles_under_lua_54(string path)
    {
        string src = File.ReadAllText(path);
        // the compile-only embedding below breaks on a same-level long bracket;
        // these guards turn a silent mis-parse into a named failure
        Assert.DoesNotContain("[===[", src);
        Assert.DoesNotContain("]===]", src);

        var runtime = new ScriptRuntime(new TestRttTransport(), Encoding.UTF8, [(byte)'\n'], _ => { }, 0);
        using var session = LuaNativeSession.Start(runtime);
        var (kind, _, message) = session.DoString(
            $"local f, err = load([===[{src}]===])\nif not f then error(err, 0) end",
            "@" + Path.GetFileName(path));
        Assert.True(kind == LuaNative.KindOk, $"{Path.GetFileName(path)}: {message}");
    }

    private static string FindTestsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "examples", "stm32", "Tests")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "examples", "stm32", "Tests");
    }
}
