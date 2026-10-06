namespace Pidge.Codegen;

public static partial class CodeGenerator
{
    private static partial string GenerateFor(Plan plan, CodeTarget target) => target switch
    {
        CodeTarget.Curl => Curl.Generate(plan),
        CodeTarget.PowerShell => PowerShell.Generate(plan),
        CodeTarget.Python => Python.Generate(plan),
        CodeTarget.CSharp => CSharp.Generate(plan),
        CodeTarget.RustBlocking => Rust.Generate(plan, Rust.Style.Blocking),
        CodeTarget.RustAsync => Rust.Generate(plan, Rust.Style.Async),
        CodeTarget.NodeFetch => Node.Generate(plan, Node.Library.Fetch),
        CodeTarget.NodeAxios => Node.Generate(plan, Node.Library.Axios),
        CodeTarget.Go => Go.Generate(plan),
        CodeTarget.JavaHttpClient => Java.Generate(plan, Java.Library.HttpClient),
        CodeTarget.JavaOkHttp => Java.Generate(plan, Java.Library.OkHttp),
        CodeTarget.PhpCurl => Php.Generate(plan, Php.Library.Curl),
        CodeTarget.PhpGuzzle => Php.Generate(plan, Php.Library.Guzzle),
        CodeTarget.Zig => Zig.Generate(plan),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };
}
