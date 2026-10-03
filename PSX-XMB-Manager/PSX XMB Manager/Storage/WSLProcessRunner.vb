Imports System.IO
Imports System.Reflection
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

''' <summary>One process launch. Arguments are passed separately and quoted by the runner.</summary>
Public Class ProcessRequest
    Public Property FileName As String = ""
    Public Property Arguments As IList(Of String) = New List(Of String)
    Public Property WorkingDirectory As String = ""

    ''' <summary>Bytes written to stdin before it is closed. Nothing closes stdin immediately.</summary>
    Public Property StandardInput As Byte()

    Public Property Timeout As TimeSpan = Threading.Timeout.InfiniteTimeSpan

    ''' <summary>Encoding of the process output. Nothing auto-detects UTF-16LE (wsl.exe) or UTF-8.</summary>
    Public Property OutputEncoding As Encoding

    Public Property EnvironmentVariables As New Dictionary(Of String, String)

    ''' <summary>Called for every stdout/stderr segment ended by CR or LF (progress output uses CR).</summary>
    Public Property StandardOutputLineCallback As Action(Of String)
    Public Property StandardErrorLineCallback As Action(Of String)

    ''' <summary>
    ''' When False, cancellation does not kill the process: the caller cancels it another way (the WSL helper's
    ''' "cancel" verb) and the runner keeps waiting for it to exit.
    ''' </summary>
    Public Property KillOnCancel As Boolean = True

    ''' <summary>Short, safe description for the log (no file contents).</summary>
    Public Property LogDescription As String = ""
End Class

''' <summary>Process execution behind an interface so tests can return fake stdout/stderr/exit codes.</summary>
Public Interface IProcessRunner
    Function RunAsync(request As ProcessRequest, cancellationToken As CancellationToken) As Task(Of ProcessResult)
    Function StartLongRunning(request As ProcessRequest) As ILongRunningProcess
End Interface

''' <summary>A process kept alive with stdin open (the WSL keepalive). Disposing closes stdin and waits for it.</summary>
Public Interface ILongRunningProcess
    Inherits IDisposable
    ReadOnly Property HasExited As Boolean
    Event Exited As EventHandler
End Interface

''' <summary>The real process runner: no shell, no window, both streams captured, timeout and cancellation.</summary>
Public Class SystemProcessRunner
    Implements IProcessRunner

    Public Async Function RunAsync(request As ProcessRequest, cancellationToken As CancellationToken) As Task(Of ProcessResult) Implements IProcessRunner.RunAsync
        cancellationToken.ThrowIfCancellationRequested()

        Dim startInfo As New ProcessStartInfo(request.FileName, CommandLineQuoting.Join(request.Arguments)) With {
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .RedirectStandardInput = True,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True
        }
        If Not String.IsNullOrEmpty(request.WorkingDirectory) Then startInfo.WorkingDirectory = request.WorkingDirectory
        For Each variable In request.EnvironmentVariables
            startInfo.EnvironmentVariables(variable.Key) = variable.Value
        Next

        Dim stopwatch As Stopwatch = Stopwatch.StartNew()
        Using process As New Process With {.StartInfo = startInfo, .EnableRaisingEvents = True}
            Dim exited As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            AddHandler process.Exited, Sub() exited.TrySetResult(True)

            process.Start()

            Dim stdoutTask As Task(Of Byte()) = ReadStreamAsync(process.StandardOutput.BaseStream, request.OutputEncoding, request.StandardOutputLineCallback)
            Dim stderrTask As Task(Of Byte()) = ReadStreamAsync(process.StandardError.BaseStream, request.OutputEncoding, request.StandardErrorLineCallback)

            Try
                If request.StandardInput IsNot Nothing AndAlso request.StandardInput.Length > 0 Then
                    Await process.StandardInput.BaseStream.WriteAsync(request.StandardInput, 0, request.StandardInput.Length).ConfigureAwait(False)
                    Await process.StandardInput.BaseStream.FlushAsync().ConfigureAwait(False)
                End If
                process.StandardInput.Close()
            Catch ex As IOException
                ' The process exited before reading its input; its exit code and output tell why.
            End Try

            Dim timedOut As Boolean = False
            Dim cancelled As Boolean = False
            Using timeoutSource As New CancellationTokenSource()
                Dim cancelSignal As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                Using registration As CancellationTokenRegistration = cancellationToken.Register(Sub() cancelSignal.TrySetResult(True))
                    Dim timeoutTask As Task = Task.Delay(request.Timeout, timeoutSource.Token)
                    Do
                        Dim finished As Task = Await Task.WhenAny(exited.Task, timeoutTask, cancelSignal.Task).ConfigureAwait(False)
                        If finished Is exited.Task Then Exit Do
                        If finished Is timeoutTask Then
                            timedOut = True
                            KillQuietly(process)
                            Exit Do
                        End If
                        ' Cancellation requested
                        cancelled = True
                        If request.KillOnCancel Then
                            KillQuietly(process)
                            Exit Do
                        End If
                        ' The caller stops the work another way; keep waiting for the process (timeout still applies).
                        cancelSignal = New TaskCompletionSource(Of Boolean)()
                    Loop
                    timeoutSource.Cancel()
                End Using
            End Using

            ' Exited fires once the process is gone; the pipes close shortly after.
            Await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(False)
            Await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(False)

            Dim stdoutBytes As Byte() = If(stdoutTask.Status = TaskStatus.RanToCompletion, stdoutTask.Result, New Byte() {})
            Dim stderrBytes As Byte() = If(stderrTask.Status = TaskStatus.RanToCompletion, stderrTask.Result, New Byte() {})

            Dim result As New ProcessResult With {
                .ExitCode = If(process.HasExited, process.ExitCode, -1),
                .TimedOut = timedOut,
                .Cancelled = cancelled AndAlso Not timedOut,
                .StandardOutputBytes = stdoutBytes,
                .StandardErrorBytes = stderrBytes,
                .StandardOutput = Decode(stdoutBytes, request.OutputEncoding),
                .StandardError = Decode(stderrBytes, request.OutputEncoding)
            }
            If timedOut Then
                result.ErrorCode = BackendErrorCodes.OperationTimeout
            ElseIf result.Cancelled AndAlso request.KillOnCancel Then
                result.ErrorCode = BackendErrorCodes.OperationCancelled
            End If
            Return result
        End Using
    End Function

    Public Function StartLongRunning(request As ProcessRequest) As ILongRunningProcess Implements IProcessRunner.StartLongRunning
        Dim startInfo As New ProcessStartInfo(request.FileName, CommandLineQuoting.Join(request.Arguments)) With {
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .RedirectStandardInput = True,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True
        }
        For Each variable In request.EnvironmentVariables
            startInfo.EnvironmentVariables(variable.Key) = variable.Value
        Next
        Dim process As New Process With {.StartInfo = startInfo, .EnableRaisingEvents = True}
        process.Start()
        Return New LongRunningProcess(process)
    End Function

    Public Shared Function Decode(bytes As Byte(), encoding As Encoding) As String
        If bytes Is Nothing OrElse bytes.Length = 0 Then Return ""
        If encoding Is Nothing Then Return OutputText.DecodeWslText(bytes)
        Return encoding.GetString(bytes)
    End Function

    Private Shared Sub KillQuietly(process As Process)
        Try
            If Not process.HasExited Then process.Kill()
        Catch ex As InvalidOperationException
        Catch ex As ComponentModel.Win32Exception
        End Try
    End Sub

    Private Shared Async Function ReadStreamAsync(stream As Stream, encoding As Encoding, lineCallback As Action(Of String)) As Task(Of Byte())
        Dim captured As New MemoryStream()
        Dim buffer(8191) As Byte
        Dim decoder As Decoder = If(encoding, Encoding.UTF8).GetDecoder()
        Dim pending As New StringBuilder()
        Do
            Dim read As Integer = Await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(False)
            If read <= 0 Then Exit Do
            captured.Write(buffer, 0, read)
            If lineCallback IsNot Nothing Then
                Dim chars(decoder.GetCharCount(buffer, 0, read) - 1) As Char
                decoder.GetChars(buffer, 0, read, chars, 0)
                For Each c As Char In chars
                    If c = ControlChars.Cr OrElse c = ControlChars.Lf Then
                        If pending.Length > 0 Then
                            InvokeQuietly(lineCallback, pending.ToString())
                            pending.Clear()
                        End If
                    Else
                        pending.Append(c)
                    End If
                Next
            End If
        Loop
        If lineCallback IsNot Nothing AndAlso pending.Length > 0 Then InvokeQuietly(lineCallback, pending.ToString())
        Return captured.ToArray()
    End Function

    Private Shared Sub InvokeQuietly(callback As Action(Of String), line As String)
        Try
            callback(line)
        Catch
            ' A progress display problem must never break the running HDD operation.
        End Try
    End Sub

    Private NotInheritable Class LongRunningProcess
        Implements ILongRunningProcess

        Private ReadOnly _process As Process
        Private _disposed As Boolean

        Public Event Exited As EventHandler Implements ILongRunningProcess.Exited

        Public Sub New(process As Process)
            _process = process
            AddHandler _process.Exited, Sub(sender, e) RaiseEvent Exited(Me, EventArgs.Empty)
            ' Drain the pipes so the child can never block on a full buffer.
            _process.StandardOutput.BaseStream.CopyToAsync(Stream.Null)
            _process.StandardError.BaseStream.CopyToAsync(Stream.Null)
        End Sub

        Public ReadOnly Property HasExited As Boolean Implements ILongRunningProcess.HasExited
            Get
                Try
                    Return _process.HasExited
                Catch ex As InvalidOperationException
                    Return True
                End Try
            End Get
        End Property

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            Try
                _process.StandardInput.Close()
                If Not _process.WaitForExit(5000) Then KillQuietly(_process)
            Catch ex As InvalidOperationException
            Catch ex As IOException
            End Try
            _process.Dispose()
        End Sub
    End Class
End Class

''' <summary>
''' The only place in the application that launches wsl.exe. Fixed commands, the JSON helper protocol and the
''' root bootstrap all go through here; user strings only ever travel as JSON on stdin.
''' </summary>
Public Class WSLProcessRunner

    Public Const PythonPath As String = "/usr/bin/python3"
    Public Const HelperLinuxPath As String = "/usr/local/lib/psx-xmb-manager/psx-xmb-helper.py"
    Public Const BootstrapResourceName As String = "PSX_XMB_Manager.WSL.bootstrap-wsl.sh"
    Public Const HelperResourceName As String = "PSX_XMB_Manager.WSL.psx-xmb-helper.py"

    Private ReadOnly _runner As IProcessRunner
    Private ReadOnly _wslExePath As String

    Public Sub New(Optional runner As IProcessRunner = Nothing, Optional wslExePath As String = Nothing)
        _runner = If(runner, New SystemProcessRunner())
        _wslExePath = If(wslExePath, LocateWslExe())
    End Sub

    Public ReadOnly Property WslExePath As String
        Get
            Return _wslExePath
        End Get
    End Property

    Public ReadOnly Property IsWslInstalled As Boolean
        Get
            Return Not String.IsNullOrEmpty(_wslExePath)
        End Get
    End Property

    ''' <summary>%SystemRoot%\System32\wsl.exe, then wsl.exe on PATH.</summary>
    Public Shared Function LocateWslExe() As String
        Dim systemRoot As String = Environment.GetEnvironmentVariable("SystemRoot")
        If String.IsNullOrEmpty(systemRoot) Then systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        If Not String.IsNullOrEmpty(systemRoot) Then
            Dim system32 As String = Path.Combine(systemRoot, "System32", "wsl.exe")
            If File.Exists(system32) Then Return system32
            ' A 32-bit process sees SysWOW64 as System32, which has no wsl.exe.
            Dim sysnative As String = Path.Combine(systemRoot, "Sysnative", "wsl.exe")
            If Not Environment.Is64BitProcess AndAlso File.Exists(sysnative) Then Return sysnative
        End If
        Dim searchPath As String = If(Environment.GetEnvironmentVariable("PATH"), "")
        For Each folder As String In searchPath.Split(Path.PathSeparator)
            Try
                If folder.Trim().Length = 0 Then Continue For
                Dim candidate As String = Path.Combine(folder.Trim(), "wsl.exe")
                If File.Exists(candidate) Then Return candidate
            Catch ex As ArgumentException
            End Try
        Next
        Return ""
    End Function

    Private Function NewWslRequest(arguments As IEnumerable(Of String), timeout As TimeSpan, description As String) As ProcessRequest
        Dim request As New ProcessRequest With {
            .FileName = _wslExePath,
            .Arguments = arguments.ToList(),
            .Timeout = timeout,
            .LogDescription = description
        }
        ' Asks wsl.exe for UTF-8 output; older builds ignore it and the decoder detects UTF-16LE.
        request.EnvironmentVariables("WSL_UTF8") = "1"
        Return request
    End Function

    Private Async Function RunLoggedAsync(request As ProcessRequest, distro As String, operation As String, cancellationToken As CancellationToken) As Task(Of ProcessResult)
        If Not IsWslInstalled Then
            Throw New StorageBackendException(BackendErrorCodes.WslNotInstalled, "WSL is not installed: wsl.exe was not found in System32 or on PATH.")
        End If
        Dim stopwatch As Stopwatch = Stopwatch.StartNew()
        Dim result As ProcessResult
        Try
            result = Await _runner.RunAsync(request, cancellationToken).ConfigureAwait(False)
        Catch ex As ComponentModel.Win32Exception
            BackendLog.Write(StorageBackendKind.WSL2NBD, distro, operation, request.LogDescription, Nothing, stopwatch.Elapsed, BackendErrorCodes.WslLaunchFailed, "", ex.Message)
            Throw New StorageBackendException(BackendErrorCodes.WslLaunchFailed, "wsl.exe could not be started: " + ex.Message, "", ex)
        End Try
        BackendLog.Write(StorageBackendKind.WSL2NBD, distro, operation, request.LogDescription, result.ExitCode, stopwatch.Elapsed,
                         result.ErrorCode, result.StandardOutput, result.StandardError)
        Return result
    End Function

    ''' <summary>"wsl --list --quiet" plus "wsl --list --verbose", merged.</summary>
    Public Async Function ListDistrosAsync(cancellationToken As CancellationToken) As Task(Of List(Of WslDistroInfo))
        Dim quiet As ProcessResult = Await RunLoggedAsync(NewWslRequest({"--list", "--quiet"}, OperationTimeouts.WslProbe, "wsl --list --quiet"), "", "list-distros", cancellationToken).ConfigureAwait(False)
        Dim verbose As ProcessResult = Await RunLoggedAsync(NewWslRequest({"--list", "--verbose"}, OperationTimeouts.WslProbe, "wsl --list --verbose"), "", "list-distros", cancellationToken).ConfigureAwait(False)

        If quiet.TimedOut OrElse verbose.TimedOut Then
            Throw New StorageBackendException(BackendErrorCodes.OperationTimeout, "wsl.exe did not answer within " + OperationTimeouts.WslProbe.TotalSeconds.ToString() + " seconds while listing distributions.")
        End If

        Dim names As List(Of String) = If(quiet.ExitCode = 0, WslOutputParsers.ParseQuietList(quiet.StandardOutput), New List(Of String))
        Dim versions As List(Of WslDistroInfo) = If(verbose.ExitCode = 0, WslOutputParsers.ParseVerboseList(verbose.StandardOutput), New List(Of WslDistroInfo))

        If quiet.ExitCode <> 0 AndAlso verbose.ExitCode <> 0 Then
            Dim said As String = (verbose.StandardOutput + " " + verbose.StandardError).Trim()
            Throw New StorageBackendException(BackendErrorCodes.WslNotInstalled,
                                              "WSL did not list any distributions." + If(said.Length > 0, " wsl.exe said: " + said, ""),
                                              said)
        End If

        Return WslOutputParsers.MergeLists(names, versions)
    End Function

    ''' <summary>Runs a fixed command (no user-controlled text) as the distro's default user.</summary>
    Public Function RunFixedAsync(distro As String, command As IList(Of String), timeout As TimeSpan, cancellationToken As CancellationToken) As Task(Of ProcessResult)
        RequireDistro(distro)
        Dim arguments As New List(Of String) From {"-d", distro, "--exec"}
        arguments.AddRange(command)
        Dim request As ProcessRequest = NewWslRequest(arguments, timeout, "wsl -d " + distro + " --exec " + String.Join(" ", command))
        request.OutputEncoding = New UTF8Encoding(False)
        Return RunLoggedAsync(request, distro, "fixed", cancellationToken)
    End Function

    ''' <summary>Runs a trusted, embedded shell script (fed on stdin, never on the command line) as the default user.</summary>
    Public Function RunFixedScriptAsync(distro As String, script As String, timeout As TimeSpan, cancellationToken As CancellationToken) As Task(Of ProcessResult)
        RequireDistro(distro)
        Dim request As ProcessRequest = NewWslRequest({"-d", distro, "--exec", "/bin/sh", "-s"}, timeout, "wsl -d " + distro + " --exec /bin/sh -s <fixed probe script>")
        request.OutputEncoding = New UTF8Encoding(False)
        request.StandardInput = New UTF8Encoding(False).GetBytes(script.Replace(vbCrLf, vbLf))
        Return RunLoggedAsync(request, distro, "probe-script", cancellationToken)
    End Function

    ''' <summary>
    ''' Calls the Linux helper: wsl.exe -d DISTRO --exec /usr/bin/python3 HELPER VERB, with the JSON request on stdin.
    ''' </summary>
    ''' <param name="cancelViaHelper">
    ''' True for long operations that are stopped through the helper's "cancel" verb; the wsl.exe process is then
    ''' never killed so the Linux side can finish cleanly.
    ''' </param>
    Public Async Function RunHelperAsync(distro As String, verb As String, request As JObject, timeout As TimeSpan,
                                         cancellationToken As CancellationToken,
                                         Optional progress As Action(Of String) = Nothing,
                                         Optional cancelViaHelper As Boolean = False) As Task(Of HelperResponse)
        RequireDistro(distro)
        Dim json As String = JsonConvert.SerializeObject(If(request, New JObject()), New JsonSerializerSettings With {.StringEscapeHandling = StringEscapeHandling.EscapeNonAscii})
        Dim processRequest As ProcessRequest = NewWslRequest({"-d", distro, "--exec", PythonPath, HelperLinuxPath, verb}, timeout,
                                                             "psx-xmb-helper " + verb + " " + DescribeRequest(request))
        processRequest.OutputEncoding = New UTF8Encoding(False)
        processRequest.StandardInput = Encoding.ASCII.GetBytes(json)
        processRequest.StandardErrorLineCallback = progress
        processRequest.KillOnCancel = Not cancelViaHelper

        Dim result As ProcessResult = Await RunLoggedAsync(processRequest, distro, "helper:" + verb, cancellationToken).ConfigureAwait(False)

        If result.TimedOut Then
            Throw New StorageBackendException(BackendErrorCodes.OperationTimeout,
                                              "The WSL helper did not finish '" + verb + "' within " + timeout.TotalSeconds.ToString() + " seconds.",
                                              result.StandardError)
        End If
        If result.Cancelled AndAlso Not cancelViaHelper Then
            Throw New OperationCanceledException(cancellationToken)
        End If

        Dim response As HelperResponse = HelperResponse.TryParse(result.StandardOutput)
        If response IsNot Nothing Then Return response

        ' No JSON: wsl.exe itself failed, python3 or the helper is missing, or the helper crashed.
        Dim raw As String = (OutputText.DecodeWslText(result.StandardOutputBytes) + vbLf + OutputText.DecodeWslText(result.StandardErrorBytes)).Trim()
        If raw.Contains(HelperLinuxPath) OrElse raw.Contains(PythonPath) OrElse raw.Contains("No such file or directory") Then
            Throw New StorageBackendException(BackendErrorCodes.BackendSetupRequired,
                                              "The PSX XMB Manager helper is not installed in WSL distribution '" + distro + "'.", raw)
        End If
        Throw New StorageBackendException(BackendErrorCodes.HelperProtocolError,
                                          "The WSL helper returned no valid response for '" + verb + "' (exit code " + result.ExitCode.ToString() + ").", raw)
    End Function

    ''' <summary>
    ''' Runs the embedded bootstrap script as WSL root: wsl.exe -d DISTRO -u root --exec /bin/bash -s, script on stdin.
    ''' </summary>
    ''' <param name="mode">"full" (Ubuntu/Debian packages, tool builds, helper) or "helper-only".</param>
    ''' <param name="helperProtocolVersion">The bootstrap verifies the installed helper reports this version.</param>
    Public Function RunBootstrapAsync(distro As String, mode As String, helperProtocolVersion As Integer, progress As Action(Of String), cancellationToken As CancellationToken) As Task(Of ProcessResult)
        RequireDistro(distro)
        If mode <> "full" AndAlso mode <> "helper-only" Then Throw New ArgumentException("Unknown bootstrap mode: " + mode, NameOf(mode))
        Dim helperBytes As Byte() = New UTF8Encoding(False).GetBytes(LoadEmbeddedText(HelperResourceName).Replace(vbCrLf, vbLf))
        Dim script As String = LoadEmbeddedText(BootstrapResourceName).Replace(vbCrLf, vbLf) _
            .Replace("__PSX_XMB_MODE__", mode) _
            .Replace("__PSX_XMB_HELPER_B64__", Convert.ToBase64String(helperBytes)) _
            .Replace("__PSX_XMB_PROTOCOL_VERSION__", helperProtocolVersion.ToString(Globalization.CultureInfo.InvariantCulture))

        Dim request As ProcessRequest = NewWslRequest({"-d", distro, "-u", "root", "--exec", "/bin/bash", "-s"}, OperationTimeouts.Bootstrap,
                                                      "wsl -d " + distro + " -u root --exec /bin/bash -s <embedded bootstrap-wsl.sh mode=" + mode + ">")
        request.OutputEncoding = New UTF8Encoding(False)
        request.StandardInput = New UTF8Encoding(False).GetBytes(script)
        request.StandardOutputLineCallback = progress
        request.StandardErrorLineCallback = progress
        Return RunLoggedAsync(request, distro, "bootstrap", cancellationToken)
    End Function

    ''' <summary>
    ''' Keeps the distribution (and therefore nbdfuse) running while the PSX is connected. WSL stops an idle
    ''' distribution shortly after its last wsl.exe client exits, which would silently drop the NBD mount.
    ''' The helper's keepalive verb blocks until its stdin is closed.
    ''' </summary>
    Public Function StartKeepAlive(distro As String) As ILongRunningProcess
        RequireDistro(distro)
        Dim request As ProcessRequest = NewWslRequest({"-d", distro, "--exec", PythonPath, HelperLinuxPath, "keepalive"}, OperationTimeouts.Unlimited, "psx-xmb-helper keepalive")
        BackendLog.Note(StorageBackendKind.WSL2NBD, distro, "starting keepalive")
        Return _runner.StartLongRunning(request)
    End Function

    Public Shared Function LoadEmbeddedText(resourceName As String) As String
        Using stream As Stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            If stream Is Nothing Then Throw New StorageBackendException(BackendErrorCodes.BackendSetupRequired, "Embedded resource missing from the application: " + resourceName)
            Using reader As New StreamReader(stream, New UTF8Encoding(False))
                Return reader.ReadToEnd()
            End Using
        End Using
    End Function

    Private Shared Sub RequireDistro(distro As String)
        If Not StorageValidation.IsValidDistroName(distro) Then
            Throw New StorageBackendException(BackendErrorCodes.WslDistroNotFound, "No valid WSL2 distribution is selected.")
        End If
    End Sub

    ''' <summary>Request keys for the log; values are included because they are paths, titles and partition names, never file data.</summary>
    Private Shared Function DescribeRequest(request As JObject) As String
        If request Is Nothing Then Return "{}"
        Return request.ToString(Formatting.None)
    End Function
End Class
