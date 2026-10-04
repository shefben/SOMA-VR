Imports System.IO
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq

''' <summary>A keepalive process that only exits when the test says so.</summary>
Public Class FakeLongRunningProcess
    Implements ILongRunningProcess

    Private _exited As Boolean

    Public Property Disposed As Boolean

    Public ReadOnly Property HasExited As Boolean Implements ILongRunningProcess.HasExited
        Get
            Return _exited
        End Get
    End Property

    Public Event Exited As EventHandler Implements ILongRunningProcess.Exited

    ''' <summary>Simulates the distribution stopping ("wsl --shutdown").</summary>
    Public Sub SimulateExit()
        _exited = True
        RaiseEvent Exited(Me, EventArgs.Empty)
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Disposed = True
    End Sub
End Class

''' <summary>One call of the WSL helper as the fake runner received it.</summary>
Public Class HelperCall
    Public Property Verb As String = ""
    Public Property Request As JObject
    Public Property ProcessRequest As ProcessRequest
End Class

''' <summary>
''' Stands in for wsl.exe: answers "--list", the distro start, the tool probe script and every helper verb with canned
''' output, so the backend can be tested without Windows, WSL or a PSX.
''' </summary>
Public Class FakeProcessRunner
    Implements IProcessRunner

    Public Const Distro As String = "Ubuntu-24.04"
    Public Const RawPath As String = "/home/tester/.local/share/psx-xmb-manager/nbd/nbd"

    Private ReadOnly _sync As New Object()
    Private ReadOnly _calls As New List(Of HelperCall)

    Public ReadOnly KeepAlives As New List(Of FakeLongRunningProcess)

    Public Property QuietList As String = Distro & vbLf
    Public Property VerboseList As String = "  NAME            STATE           VERSION" & vbLf & "* " & Distro & "    Running         2" & vbLf
    Public Property ProbeScriptOutput As String = BuildProbeOutput()

    ''' <summary>Helper verb -> handler(request, processRequest) returning the helper's stdout (one JSON object).</summary>
    Public ReadOnly HelperHandlers As New Dictionary(Of String, Func(Of JObject, ProcessRequest, Task(Of String)))(StringComparer.Ordinal)

    Public Sub New()
        HelperHandlers("probe") = Function(r, p) Task.FromResult(Ok(New JObject From {
            {"protocol_version", WSL2NBDBackend.ExpectedHelperProtocolVersion},
            {"nbdfuse_version", "nbdfuse 1.18.1"},
            {"nbdinfo_version", "nbdinfo 1.18.1"},
            {"home", "/home/tester"},
            {"status", New JObject From {{"state", "disconnected"}, {"pfs_mounts", New JArray()}}}
        }))
        HelperHandlers("connect") = Function(r, p) Task.FromResult(Ok(New JObject From {{"raw_path", RawPath}}))
        HelperHandlers("disconnect") = Function(r, p) Task.FromResult(Ok(New JObject()))
        HelperHandlers("recover") = Function(r, p) Task.FromResult(Ok(New JObject()))
        HelperHandlers("translate-path") = Function(r, p) Task.FromResult(Ok(New JObject From {{"linux_path", TranslateForTest(r.Value(Of String)("windows_path"))}}))
        HelperHandlers("hdl") = Function(r, p) Task.FromResult(Ok(New JObject From {{"exit_code", 0}}, "hdl output"))
        HelperHandlers("pfsshell") = Function(r, p) Task.FromResult(Ok(New JObject From {{"exit_code", 0}}, "pfsshell output"))
        HelperHandlers("unmount-pfs") = Function(r, p) Task.FromResult(Ok(New JObject()))
        HelperHandlers("raw-size") = Function(r, p) Task.FromResult(Ok(New JObject From {{"size", 1073741824L}}))
        HelperHandlers("cancel") = Function(r, p) Task.FromResult(Ok(New JObject()))
    End Sub

    Public Shared Function BuildProbeOutput(Optional missingTool As String = "", Optional devFuse As Boolean = True, Optional osId As String = "ubuntu") As String
        Dim text As New StringBuilder()
        text.Append("user=tester").Append(vbLf)
        text.Append("home=/home/tester").Append(vbLf)
        For Each tool As String In {"python3", "nbdfuse", "nbdinfo", "hdl_dump", "pfsshell", "pfsfuse", "fusermount3", "fusermount"}
            text.Append("tool.").Append(tool).Append("=").Append(If(tool = missingTool, "", "/usr/bin/" + tool)).Append(vbLf)
        Next
        text.Append("devfuse=").Append(If(devFuse, "1", "0")).Append(vbLf)
        text.Append("helper=1").Append(vbLf)
        text.Append("--os-release--").Append(vbLf)
        text.Append("PRETTY_NAME=""Test Linux""").Append(vbLf)
        text.Append("ID=").Append(osId).Append(vbLf)
        text.Append("VERSION_ID=""24.04""").Append(vbLf)
        Return text.ToString()
    End Function

    Public Shared Function TranslateForTest(windowsPath As String) As String
        ' C:\Users\me\x -> /mnt/c/Users/me/x, only for the fake; the real helper always asks wslpath.
        Return "/mnt/" + windowsPath.Substring(0, 1).ToLowerInvariant() + windowsPath.Substring(2).Replace("\", "/")
    End Function

    Public Shared Function Ok(data As JObject, Optional stdout As String = "") As String
        Return New JObject From {{"ok", True}, {"code", "OK"}, {"message", ""}, {"stdout", stdout}, {"stderr", ""}, {"data", data}}.ToString()
    End Function

    Public Shared Function Fail(code As String, message As String, Optional data As JObject = Nothing) As String
        Return New JObject From {{"ok", False}, {"code", code}, {"message", message}, {"stdout", ""}, {"stderr", "tool said no"}, {"data", If(data, New JObject())}}.ToString()
    End Function

    Public ReadOnly Property Calls As List(Of HelperCall)
        Get
            SyncLock _sync
                Return _calls.ToList()
            End SyncLock
        End Get
    End Property

    Public Function CallsOf(verb As String) As List(Of HelperCall)
        Return Calls.Where(Function(c) c.Verb = verb).ToList()
    End Function

    Public Async Function RunAsync(request As ProcessRequest, cancellationToken As CancellationToken) As Task(Of ProcessResult) Implements IProcessRunner.RunAsync
        Dim args As IList(Of String) = request.Arguments

        If args.Contains("--list") Then
            Return Output(If(args.Contains("--quiet"), QuietList, VerboseList))
        End If

        If args.Contains(WSLProcessRunner.HelperLinuxPath) Then
            Dim verb As String = args(args.Count - 1)
            Dim json As String = If(request.StandardInput Is Nothing, "{}", Encoding.ASCII.GetString(request.StandardInput))
            Dim helperRequest As JObject = JObject.Parse(json)
            SyncLock _sync
                _calls.Add(New HelperCall With {.Verb = verb, .Request = helperRequest, .ProcessRequest = request})
            End SyncLock
            Dim handler As Func(Of JObject, ProcessRequest, Task(Of String)) = Nothing
            If Not HelperHandlers.TryGetValue(verb, handler) Then
                Return Output(Fail("INVALID_REQUEST", "unknown verb " + verb), 2)
            End If
            Dim stdout As String = Await handler(helperRequest, request).ConfigureAwait(False)
            Dim parsed As JObject = JObject.Parse(stdout)
            Return Output(stdout, If(parsed.Value(Of Boolean)("ok"), 0, 1))
        End If

        If args.Contains("/bin/true") Then Return Output("")
        If args.Contains("/bin/sh") Then Return Output(ProbeScriptOutput)
        Return Output("unexpected command", 1)
    End Function

    Public Function StartLongRunning(request As ProcessRequest) As ILongRunningProcess Implements IProcessRunner.StartLongRunning
        Dim keepAlive As New FakeLongRunningProcess()
        SyncLock _sync
            KeepAlives.Add(keepAlive)
        End SyncLock
        Return keepAlive
    End Function

    Private Shared Function Output(stdout As String, Optional exitCode As Integer = 0) As ProcessResult
        Dim bytes As Byte() = New UTF8Encoding(False).GetBytes(stdout)
        Return New ProcessResult With {.ExitCode = exitCode, .StandardOutput = stdout, .StandardOutputBytes = bytes}
    End Function
End Class

''' <summary>Collects progress lines synchronously (Progress(Of T) would post them to a synchronization context).</summary>
Public Class ListProgress
    Implements IProgress(Of String)

    Public ReadOnly Lines As New List(Of String)

    Public Sub Report(value As String) Implements IProgress(Of String).Report
        SyncLock Lines
            Lines.Add(value)
        End SyncLock
    End Sub
End Class

''' <summary>Keeps the application log of the tests out of the user's profile.</summary>
Public Module TestLog
    Public Sub UseTempLogDirectory()
        BackendLog.LogDirectory = Path.Combine(Path.GetTempPath(), "psx-xmb-manager-tests", "Logs")
    End Sub
End Module
