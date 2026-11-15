using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Tesserafin.Common.Net;
using Tesserafin.Model.Configuration;
using Tesserafin.Server.Configuration;
using Xunit;

namespace Tesserafin.Server.Tests.ServiceHost;

/// <summary>
/// The exit contract from <c>docs/distribution/W0-windows-server.md</c> §2.5 and §4: a fatal
/// startup must leave a non-zero process exit code, and an ordinary exit must keep leaving zero.
/// </summary>
/// <remarks>
/// <para>
/// These run the real published entry point as a child process, because the defect they guard is a
/// property of the process and not of a method. §2.5 measured the unmodified server logging
/// <c>FfmpegException: Failed to find valid ffmpeg</c> and then <b>exiting 0</b>, which a Service
/// Control Manager reads as "stopped normally": no failure action, no record that the server never
/// came up. An in-process assertion cannot see that, because the exit code is written by the host
/// on the way out.
/// </para>
/// <para>
/// No Service Control Manager is required or used. The SCM half of the contract — that the same
/// failure is visible as a non-zero service exit code — is hosted evidence and belongs to
/// <c>.github/workflows/w3-windows-service-host.yml</c>; what is proved here is the process-level
/// half, on whatever platform the suite runs.
/// </para>
/// </remarks>
public sealed class FatalStartupExitCodeTests
{
    /// <summary>
    /// A cold first start applies every database migration. W0 §2.3 allowed 600 s for that on a
    /// hosted Windows runner; this bound is generous rather than tight because a timeout here must
    /// mean "the server never finished", not "the runner was busy".
    /// </summary>
    private static readonly TimeSpan _startupBudget = TimeSpan.FromMinutes(6);

    /// <summary>
    /// How long the console is watched after it has logged the fatal startup, to see whether the
    /// process is still there.
    /// </summary>
    /// <remarks>
    /// Measured from the failure rather than from the launch, so the window says "still lingering
    /// twenty seconds after it died" on a fast runner and on a loaded one alike. The linger it is
    /// looking for is <see cref="Program.PreConfigurationFailureLinger"/> — ten minutes — so any
    /// window at all discriminates; twenty seconds is chosen to keep the suite cheap.
    /// </remarks>
    private static readonly TimeSpan _lingerObservationWindow = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task MissingEncoder_FatalStartup_ExitsNonZero()
    {
        using var sandbox = new ServerSandbox();

        // The port matters even though the failure does not involve it. The encoder check runs in
        // RunStartupTasksAsync, which is reached only after Kestrel has bound, so leaving the
        // default 8096 in place would let anything else already listening on this host turn a
        // proven encoder failure into an unexplained one.
        sandbox.PinHttpPort(FreeTcpPort());

        var result = await sandbox.RunAsync(Array.Empty<string>());

        Assert.False(
            result.TimedOut,
            $"The server never exited within {_startupBudget}. Output:\n{result.Output}");

        // Both halves are load-bearing. The exit code alone would also be satisfied by a Kestrel
        // bind failure or a bad path, and a test that accepts any failure would keep passing if the
        // encoder path stopped being fatal at all.
        Assert.Contains("FfmpegException", result.Output, StringComparison.Ordinal);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task OrdinaryExit_StillExitsZero()
    {
        using var sandbox = new ServerSandbox();

        // `--mode MigrateSystem` runs the migrations and shuts down without starting Kestrel and
        // without running the startup tasks, so it needs no encoder and binds no port: an exit that
        // is normal by construction, on the same tree and the same absent PATH as the test above.
        // It is the control that says the non-zero code is a property of the failure and not a new
        // property of exiting.
        var result = await sandbox.RunAsync(["--mode", "MigrateSystem"]);

        Assert.False(
            result.TimedOut,
            $"The server never exited within {_startupBudget}. Output:\n{result.Output}");
        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>
    /// W3-A1: the decision itself, both ways, on any platform.
    /// </summary>
    /// <remarks>
    /// <c>WindowsServiceHelpers.IsWindowsService()</c> answers <c>false</c> everywhere except
    /// inside a process the Service Control Manager actually started — including the hosted
    /// <c>dotnet test</c> step on <c>windows-latest</c> — so no test anywhere can reach the service
    /// branch of <see cref="Program.IsRunningAsWindowsService(StartupOptions)"/>. What is testable
    /// is the decision that branch feeds, and it is written to take the service fact as an argument
    /// for exactly that reason. The observation that the process is really gone under a real SCM is
    /// control F in <c>.github/workflows/w3-windows-service-host.yml</c>, and it is hosted-only.
    /// </remarks>
    [Fact]
    public void PreConfigurationFailure_UnderWindowsService_DoesNotLinger()
    {
        var mediaServer = new StartupOptions();

        Assert.True(Program.ShouldLingerAfterPreConfigurationFailure(mediaServer, runningAsWindowsService: false));
        Assert.False(Program.ShouldLingerAfterPreConfigurationFailure(mediaServer, runningAsWindowsService: true));

        // The startup-mode half of the condition is master's and is not this slice's to change: a
        // mode that is not the media server never had a setup server for an operator to read.
        var migrate = new StartupOptions { StartupMode = StartupMode.MigrateSystem };

        Assert.False(Program.ShouldLingerAfterPreConfigurationFailure(migrate, runningAsWindowsService: false));
        Assert.False(Program.ShouldLingerAfterPreConfigurationFailure(migrate, runningAsWindowsService: true));
    }

    /// <summary>
    /// W3-A1: a console keeps the linger, and the fault used to prove it is a real one.
    /// </summary>
    /// <remarks>
    /// Two things at once, deliberately. It is the control that says W3-A1 did not simply delete
    /// the wait — an operator whose first start fails before the setup server hands over still has
    /// the error page to read. It is also where the hook control F fires is exercised outside a
    /// service: an unreachable <c>TranscodingTempPath</c> makes
    /// <c>EncodingConfigurationExtensions.GetTranscodePath</c> throw at the first statement after
    /// the host is built, long before <c>configurationCompleted</c>, and with no encoder involved.
    /// </remarks>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task PreConfigurationFailure_InConsole_StillLingers()
    {
        using var sandbox = new ServerSandbox();

        // The setup server binds this port to serve the error page the linger exists for, so it is
        // pinned away from 8096 for the same reason the encoder test pins it.
        sandbox.PinHttpPort(FreeTcpPort());
        var blockedBy = sandbox.BreakTranscodePath();

        var result = await sandbox.RunUntilLoggedAsync("Error while starting server", _lingerObservationWindow);

        Assert.True(
            result.FailureLogged,
            $"The server never logged a fatal startup within {_startupBudget}. Output:\n{result.Output}");

        // The premise: this is the pre-configurationCompleted hook and not some other failure that
        // happens to be fatal. The path is in the exception message; the encoder is not involved,
        // and asserting its absence is what keeps this from silently becoming a second copy of
        // MissingEncoder_FatalStartup_ExitsNonZero.
        Assert.Contains(blockedBy, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("FfmpegException", result.Output, StringComparison.Ordinal);

        Assert.True(
            result.StillRunning,
            "The console exited instead of lingering after a pre-configurationCompleted failure. " +
            "That linger is how an operator reads a failed first start; only the service path may " +
            $"skip it. Output:\n{result.Output}");
    }

    private static int FreeTcpPort()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)listener.LocalEndPoint!).Port;
    }

    private sealed record RunResult(int ExitCode, string Output, bool TimedOut);

    private sealed record LingerResult(bool FailureLogged, bool StillRunning, string Output);

    /// <summary>
    /// Fresh state directories, a PATH that contains nothing, and the server assembly that this
    /// test project already builds against.
    /// </summary>
    private sealed class ServerSandbox : IDisposable
    {
        private readonly string _root;

        internal ServerSandbox()
        {
            _root = Path.Combine(Path.GetTempPath(), "tesserafin-w3a0-" + Guid.NewGuid().ToString("N"));
            foreach (var name in new[] { "config", "data", "cache", "log", "emptypath" })
            {
                Directory.CreateDirectory(Path.Combine(_root, name));
            }
        }

        private string ConfigDir => Path.Combine(_root, "config");

        internal void PinHttpPort(int port)
        {
            // Written through the real configuration type rather than as hand-shaped XML, so the
            // element names cannot drift away from what the server deserialises.
            var configuration = new NetworkConfiguration { InternalHttpPort = port };
            var path = Path.Combine(ConfigDir, NetworkConfigurationStore.StoreKey + ".xml");
            using var stream = File.Create(path);
            new XmlSerializer(typeof(NetworkConfiguration)).Serialize(stream, configuration);
        }

        /// <summary>
        /// Points <c>TranscodingTempPath</c> at a directory that cannot be created, because a file
        /// already occupies its parent's name.
        /// </summary>
        /// <returns>
        /// The path of the file that blocks it. That is the file, not the transcode directory: .NET
        /// names the unreachable path on Unix ("Could not find a part of the path '…/transcodes'")
        /// and the colliding entry on Windows ("Cannot create '…/not-a-directory' because a file or
        /// directory with the same name already exists"), and the file's path is the one substring
        /// both messages carry.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The hook is <c>EncodingConfigurationExtensions.GetTranscodePath</c>, called from
        /// <c>Program.StartServer</c> as the first statement after the host is built —
        /// <c>Directory.CreateDirectory</c> on a path under a regular file throws on every platform
        /// this server runs on. That is a real operator misconfiguration reached through the real
        /// <c>encoding.xml</c>, not a flag added to make a test fail: a transcode directory under a
        /// path that has become a file is the shape a moved or half-restored library takes.
        /// </para>
        /// <para>
        /// A nonexistent drive letter would also throw on Windows, but whether a given letter is
        /// unmapped is a property of the runner rather than of the tree. A file this sandbox
        /// created itself is true everywhere.
        /// </para>
        /// </remarks>
        internal string BreakTranscodePath()
        {
            var occupied = Path.Combine(_root, "not-a-directory");
            File.WriteAllText(occupied, string.Empty);
            var transcodePath = Path.Combine(occupied, "transcodes");

            // Through the real configuration type, for the same reason PinHttpPort is: hand-shaped
            // XML that failed to deserialise would leave the defaults in place, the server would
            // start normally, and the test would fail with a message about a linger rather than
            // about its own fixture.
            var configuration = new EncodingOptions { TranscodingTempPath = transcodePath };
            var path = Path.Combine(ConfigDir, "encoding.xml");
            using (var stream = File.Create(path))
            {
                new XmlSerializer(typeof(EncodingOptions)).Serialize(stream, configuration);
            }

            return occupied;
        }

        /// <summary>
        /// Starts the server, waits for <paramref name="marker"/> in its output, and then watches
        /// for <paramref name="observationWindow"/> to see whether the process is still there.
        /// </summary>
        /// <param name="marker">The line the fatal startup writes.</param>
        /// <param name="observationWindow">How long to watch after the marker appeared.</param>
        /// <returns>What was observed.</returns>
        internal async Task<LingerResult> RunUntilLoggedAsync(string marker, TimeSpan observationWindow)
        {
            using var process = new Process { StartInfo = CreateStartInfo(Array.Empty<string>()) };
            var output = new StringBuilder();
            process.OutputDataReceived += (_, e) => Append(output, e.Data);
            process.ErrorDataReceived += (_, e) => Append(output, e.Data);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                var failureLogged = false;
                var deadline = DateTime.UtcNow + _startupBudget;
                while (DateTime.UtcNow < deadline)
                {
                    if (Read(output).Contains(marker, StringComparison.Ordinal))
                    {
                        failureLogged = true;
                        break;
                    }

                    if (process.HasExited)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                }

                if (!failureLogged)
                {
                    return new LingerResult(false, !process.HasExited, Read(output));
                }

                // The window starts here, at the failure, so that a slow runner lengthens the wait
                // for the marker rather than shortening the observation.
                using var window = new CancellationTokenSource(observationWindow);
                try
                {
                    await process.WaitForExitAsync(window.Token).ConfigureAwait(false);
                    return new LingerResult(true, false, Read(output));
                }
                catch (OperationCanceledException)
                {
                    return new LingerResult(true, true, Read(output));
                }
            }
            finally
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone, which the result above has already recorded.
                }
            }
        }

        internal async Task<RunResult> RunAsync(IReadOnlyList<string> extraArguments)
        {
            using var process = new Process { StartInfo = CreateStartInfo(extraArguments) };
            var output = new StringBuilder();
            process.OutputDataReceived += (_, e) => Append(output, e.Data);
            process.ErrorDataReceived += (_, e) => Append(output, e.Data);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = new CancellationTokenSource(_startupBudget);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It exited between the timeout and the kill; the exit code below is still real.
                }

                return new RunResult(-1, Read(output), true);
            }

            return new RunResult(process.ExitCode, Read(output), false);
        }

        private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> extraArguments)
        {
            var serverAssembly = Path.Combine(AppContext.BaseDirectory, "tesserafin.dll");
            Assert.True(
                File.Exists(serverAssembly),
                $"The server assembly was not copied next to the tests: {serverAssembly}");

            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveDotnetHost(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = _root
            };

            startInfo.ArgumentList.Add(serverAssembly);
            startInfo.ArgumentList.Add("--nowebclient");
            startInfo.ArgumentList.Add("--configdir");
            startInfo.ArgumentList.Add(ConfigDir);
            startInfo.ArgumentList.Add("--datadir");
            startInfo.ArgumentList.Add(Path.Combine(_root, "data"));
            startInfo.ArgumentList.Add("--cachedir");
            startInfo.ArgumentList.Add(Path.Combine(_root, "cache"));
            startInfo.ArgumentList.Add("--logdir");
            startInfo.ArgumentList.Add(Path.Combine(_root, "log"));
            foreach (var argument in extraArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            // No `--ffmpeg`, no `TESSERAFIN_FFmpeg__path`, and a PATH holding one empty directory.
            // That is W0 §2.5's negative control: the three ways MediaEncoder.SetFFmpegPath can find
            // an encoder are all closed, so it must fail, and it must fail for that reason.
            foreach (var key in new List<string>(startInfo.Environment.Keys))
            {
                if (key.StartsWith("TESSERAFIN_", StringComparison.OrdinalIgnoreCase))
                {
                    startInfo.Environment.Remove(key);
                }
            }

            startInfo.Environment["PATH"] = Path.Combine(_root, "emptypath");
            // These children test startup/exit behavior, not network-change subscriptions.
            // A restricted host may deny the netlink socket before the intended failure is reached.
            // Set this explicitly after clearing inherited TESSERAFIN settings above.
            startInfo.Environment["TESSERAFIN_DetectNetworkChange"] = "false";
            startInfo.Environment["DOTNET_ROOT"] = DotnetRoot();
            startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            startInfo.Environment["DOTNET_NOLOGO"] = "1";

            return startInfo;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover sandbox in the temp directory is not worth failing a green run over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void Append(StringBuilder builder, string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (builder)
            {
                builder.AppendLine(line);
            }
        }

        /// <summary>
        /// Reads the accumulated output under the same lock the redirection callbacks append with.
        /// </summary>
        /// <param name="builder">The buffer the two redirection callbacks write into.</param>
        /// <returns>The output so far.</returns>
        /// <remarks>
        /// The linger observation polls this buffer while both callbacks are still writing to it,
        /// which an unsynchronised <c>ToString</c> is not safe against.
        /// </remarks>
        private static string Read(StringBuilder builder)
        {
            lock (builder)
            {
                return builder.ToString();
            }
        }

        private static string DotnetRoot()
        {
            // .../dotnet/shared/Microsoft.NETCore.App/<version>/ -> .../dotnet
            var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
            return Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
        }

        private static string ResolveDotnetHost()
        {
            // Never a bare "dotnet": the child is launched with an empty PATH on purpose, and the
            // parent's PATH is not consulted for an absolute file name.
            var fromMsBuild = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrEmpty(fromMsBuild) && File.Exists(fromMsBuild))
            {
                return fromMsBuild;
            }

            var candidate = Path.Combine(
                DotnetRoot(),
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            Assert.True(
                File.Exists(candidate),
                string.Create(CultureInfo.InvariantCulture, $"No dotnet host found at {candidate}"));
            return candidate;
        }
    }
}
