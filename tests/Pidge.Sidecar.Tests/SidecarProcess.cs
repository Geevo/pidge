using System.Diagnostics;
using System.Text;
using Pidge.Protocol;

namespace Pidge.Sidecar.Tests;

/// <summary>Drives the real binary over stdin/stdout, the way the extension host does.</summary>
internal sealed class SidecarProcess : IDisposable
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly TempDir _stateDir;

    private SidecarProcess(Process child, TempDir stateDir)
    {
        Child = child;
        _stateDir = stateDir;
    }

    public Process Child { get; }

    public static SidecarProcess Start()
    {
        var stateDir = new TempDir();
        var child = Process.Start(StartInfo("--state-dir", stateDir.Path))
            ?? throw new InvalidOperationException("sidecar should start");
        // Read and dropped, so a chatty log can never fill the pipe and stall it.
        child.ErrorDataReceived += (_, _) => { };
        child.BeginErrorReadLine();
        return new SidecarProcess(child, stateDir);
    }

    /// <summary>
    /// Starts the sidecar this test project was built beside: the apphost
    /// where there is one, the managed assembly through <c>dotnet</c> where not.
    /// </summary>
    public static ProcessStartInfo StartInfo(params string[] args)
    {
        var dir = AppContext.BaseDirectory;
        var host = Path.Combine(dir, OperatingSystem.IsWindows() ? "api-client-sidecar.exe" : "api-client-sidecar");
        ProcessStartInfo info;
        if (File.Exists(host))
        {
            info = new ProcessStartInfo(host);
        }
        else
        {
            info = new ProcessStartInfo("dotnet");
            info.ArgumentList.Add(Path.Combine(dir, "api-client-sidecar.dll"));
        }
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        info.UseShellExecute = false;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardInputEncoding = Utf8;
        info.StandardOutputEncoding = Utf8;
        info.StandardErrorEncoding = Utf8;
        // Keeps the suite out of the user's keyring, and the same whether or
        // not the machine running it has one.
        info.Environment["PIDGE_KEYRING"] = "off";
        return info;
    }

    public void Send(string? id, ClientMessage message) =>
        SendRaw(WireFormat.EncodeLine(new ClientEnvelope(id, message)).TrimEnd('\n'));

    /// <summary>Sends a raw line, for the malformed-input cases.</summary>
    public void SendRaw(string line)
    {
        Child.StandardInput.Write(line + "\n");
        Child.StandardInput.Flush();
    }

    public ServerEnvelope Recv()
    {
        var read = Child.StandardOutput.ReadLineAsync();
        if (!read.Wait(ReplyTimeout))
        {
            throw new TimeoutException("the sidecar did not reply");
        }
        var line = read.Result ?? throw new InvalidOperationException("sidecar closed stdout unexpectedly");
        return WireFormat.DecodeServerLine(line);
    }

    public void Handshake()
    {
        Send("hs", new ClientMessage.Handshake { ClientName = "test", ClientVersion = "0.0.0" });
        var envelope = Recv();
        Assert.Equal("hs", envelope.Id);
        var ok = Assert.IsType<ServerMessage.HandshakeOk>(envelope.Msg);
        Assert.Equal(WireFormat.ProtocolVersion, ok.ProtocolVersion);
    }

    public void Dispose()
    {
        try
        {
            if (!Child.HasExited)
            {
                Child.Kill(entireProcessTree: true);
            }
            Child.WaitForExit();
        }
        catch (InvalidOperationException)
        {
        }
        Child.Dispose();
        _stateDir.Dispose();
    }
}

/// <summary>A directory that lives as long as the test.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pidge-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
