Imports System.IO
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports Xunit

''' <summary>
''' The WSL2 backend against <see cref="FakeProcessRunner"/>: probe results, connection lifecycle, serialization of HDD
''' operations, mount tracking and cancellation. No WSL, Windows or PSX is involved.
''' </summary>
Public Class WSL2NBDBackendTests

    Private Const Ip As String = "192.168.1.50"
    Private Const Port As Integer = 10809

    Public Sub New()
        TestLog.UseTempLogDirectory()
    End Sub

    Private Shared Function NewBackend(fake As FakeProcessRunner, Optional wslExe As String = "C:\Windows\System32\wsl.exe") As WSL2NBDBackend
        Return New WSL2NBDBackend(FakeProcessRunner.Distro, New WSLProcessRunner(fake, wslExe))
    End Function

    Private Shared Async Function ConnectedBackendAsync(fake As FakeProcessRunner) As Task(Of WSL2NBDBackend)
        Dim backend As WSL2NBDBackend = NewBackend(fake)
        Dim probe As BackendProbeResult = Await backend.ProbeAsync(CancellationToken.None)
        Assert.True(probe.IsReady, probe.SetupErrorCode + " " + probe.SetupMessage)
        Assert.Equal(ConnectionState.Disconnected, backend.State)
        Await backend.ConnectAsync(Ip, Port, CancellationToken.None)
        Assert.Equal(ConnectionState.Connected, backend.State)
        Return backend
    End Function

    Private Shared Async Function WaitForStateAsync(backend As WSL2NBDBackend, state As ConnectionState) As Task
        Dim deadline As DateTime = DateTime.UtcNow.AddSeconds(5)
        While backend.State <> state
            If DateTime.UtcNow > deadline Then Throw New TimeoutException("State stayed " + backend.State.ToString() + ", expected " + state.ToString())
            Await Task.Delay(10)
        End While
    End Function

    Private Shared Function MountJson(partition As String, mountId As String) As JObject
        Return New JObject From {
            {"mount_id", mountId},
            {"partition", partition},
            {"display_name", partition},
            {"path", "/home/tester/.local/share/psx-xmb-manager/pfs/" + mountId}
        }
    End Function

#Region "Probe"

    <Fact>
    Public Async Function Probe_WithoutWsl_ReportsWslNotInstalled() As Task
        Dim fake As New FakeProcessRunner()
        Dim backend As WSL2NBDBackend = NewBackend(fake, wslExe:="")
        Dim probe As BackendProbeResult = Await backend.ProbeAsync(CancellationToken.None)

        Assert.False(probe.IsReady)
        Assert.Equal(BackendErrorCodes.WslNotInstalled, probe.SetupErrorCode)
        Assert.Equal(ConnectionState.Unavailable, backend.State)
        Assert.Empty(fake.Calls)
    End Function

    <Fact>
    Public Async Function Probe_OnlyWsl1_ReportsNoWsl2Distro() As Task
        Dim fake As New FakeProcessRunner() With {
            .VerboseList = "  NAME      STATE     VERSION" & vbLf & "* Ubuntu-24.04  Stopped   1" & vbLf
        }
        Dim backend As WSL2NBDBackend = NewBackend(fake)
        Dim probe As BackendProbeResult = Await backend.ProbeAsync(CancellationToken.None)

        Assert.Equal(BackendErrorCodes.NoWsl2Distro, probe.SetupErrorCode)
        Assert.Equal(ConnectionState.Unavailable, backend.State)
    End Function

    <Theory>
    <InlineData("nbdfuse", "NBDFUSE_MISSING")>
    <InlineData("nbdinfo", "NBDINFO_MISSING")>
    <InlineData("hdl_dump", "HDL_DUMP_MISSING")>
    <InlineData("pfsshell", "PFSSHELL_MISSING")>
    <InlineData("pfsfuse", "PFSFUSE_MISSING")>
    <InlineData("fusermount3", "BACKEND_SETUP_REQUIRED")>
    Public Async Function Probe_MissingTool_NeedsSetup(tool As String, code As String) As Task
        Dim fake As New FakeProcessRunner() With {.ProbeScriptOutput = FakeProcessRunner.BuildProbeOutput(missingTool:=tool)}
        Dim backend As WSL2NBDBackend = NewBackend(fake)
        Dim probe As BackendProbeResult = Await backend.ProbeAsync(CancellationToken.None)

        Assert.False(probe.IsReady)
        Assert.Equal(code, probe.SetupErrorCode)
        Assert.Contains(probe.MissingDependencies, Function(d) d.StartsWith(tool))
        Assert.Equal(ConnectionState.NeedsSetup, backend.State)
    End Function

    <Fact>
    Public Async Function Probe_WithoutDevFuse_ReportsFuseUnavailable() As Task
        Dim fake As New FakeProcessRunner() With {.ProbeScriptOutput = FakeProcessRunner.BuildProbeOutput(devFuse:=False)}
        Dim probe As BackendProbeResult = Await NewBackend(fake).ProbeAsync(CancellationToken.None)
        Assert.Equal(BackendErrorCodes.FuseUnavailable, probe.SetupErrorCode)
    End Function

    <Fact>
    Public Async Function Probe_OutdatedHelper_ReportsVersionMismatch() As Task
        Dim fake As New FakeProcessRunner()
        fake.HelperHandlers("probe") = Function(r, p) Task.FromResult(FakeProcessRunner.Ok(New JObject From {{"protocol_version", 0}}))
        Dim probe As BackendProbeResult = Await NewBackend(fake).ProbeAsync(CancellationToken.None)
        Assert.Equal(BackendErrorCodes.HelperVersionMismatch, probe.SetupErrorCode)
        Assert.Equal(BackendErrorCodes.HelperOutdatedMessage, probe.SetupMessage)
    End Function

    <Fact>
    Public Async Function Probe_UnsupportedDistro_SaysSetupIsUbuntuDebianOnly() As Task
        Dim fake As New FakeProcessRunner() With {.ProbeScriptOutput = FakeProcessRunner.BuildProbeOutput(missingTool:="nbdfuse", osId:="fedora")}
        Dim probe As BackendProbeResult = Await NewBackend(fake).ProbeAsync(CancellationToken.None)
        Assert.False(probe.DistroSupportsAutomaticSetup)
        Assert.Contains("Ubuntu and Debian only", probe.SetupMessage)
    End Function

    <Fact>
    Public Async Function Probe_HealthyExistingSession_IsAdoptedNotRemounted() As Task
        Dim fake As New FakeProcessRunner()
        fake.HelperHandlers("probe") = Function(r, p) Task.FromResult(FakeProcessRunner.Ok(New JObject From {
            {"protocol_version", 1},
            {"status", New JObject From {
                {"state", "healthy"}, {"nbd_mounted", True}, {"raw_path", FakeProcessRunner.RawPath},
                {"ip", Ip}, {"port", Port}, {"raw_size", 1073741824L},
                {"pfs_mounts", New JArray(MountJson("PP.GAME", "PP.GAME-0123abcd"))}
            }}
        }))
        Dim backend As WSL2NBDBackend = NewBackend(fake)
        Await backend.ProbeAsync(CancellationToken.None)

        Assert.Equal(ConnectionState.Connected, backend.State)
        Assert.Equal(Ip, backend.MountedDrive.ConnectedOnIP)
        Assert.Equal(FakeProcessRunner.RawPath, backend.MountedDrive.HDLDriveName)
        Assert.Single(backend.ActiveMounts)
        Assert.Equal("\\wsl.localhost\Ubuntu-24.04\home\tester\.local\share\psx-xmb-manager\pfs\PP.GAME-0123abcd", backend.ActiveMounts(0).WindowsPath)
        Assert.Empty(fake.CallsOf("connect"))
        Assert.Single(fake.KeepAlives)
    End Function

    <Fact>
    Public Async Function Probe_StaleSession_IsFaultedAndCannotConnect() As Task
        Dim fake As New FakeProcessRunner()
        fake.HelperHandlers("probe") = Function(r, p) Task.FromResult(FakeProcessRunner.Ok(New JObject From {
            {"protocol_version", 1}, {"status", New JObject From {{"state", "nbd-process-gone"}}}}))
        Dim backend As WSL2NBDBackend = NewBackend(fake)
        Await backend.ProbeAsync(CancellationToken.None)
        Assert.Equal(ConnectionState.Faulted, backend.State)

        Dim ex = Await Assert.ThrowsAsync(Of StorageBackendException)(Function() backend.ConnectAsync(Ip, Port, CancellationToken.None))
        Assert.Equal(BackendErrorCodes.StaleMountState, ex.Code)

        Await backend.RecoverAsync(CancellationToken.None)
        Assert.Equal(ConnectionState.Disconnected, backend.State)
        Assert.Single(fake.CallsOf("recover"))
    End Function

#End Region

#Region "Connection lifecycle"

    <Fact>
    Public Async Function Connect_BuildsTheWslDriveAndStartsTheKeepAlive() As Task
        Dim fake As New FakeProcessRunner()
        Dim backend As WSL2NBDBackend = Await ConnectedBackendAsync(fake)

        Dim drive = backend.MountedDrive
        Assert.Equal(StorageBackendKind.WSL2NBD, drive.BackendKind)
        Assert.Equal(Ip, drive.ConnectedOnIP)
        Assert.Equal(Port, drive.NBDPort)
        Assert.Equal(FakeProcessRunner.RawPath, drive.HDLDriveName)
        Assert.Equal(FakeProcessRunner.RawPath, drive.DriveID)
        Assert.Equal(FakeProcessRunner.Distro, drive.WslDistro)
        Assert.Single(fake.KeepAlives)

        Dim connect As HelperCall = fake.CallsOf("connect").Single()
        Assert.Equal(Ip, connect.Request.Value(Of String)("ip"))
        Assert.Equal(Port, connect.Request.Value(Of Integer)("port"))
        ' wsl.exe -d DISTRO --exec /usr/bin/python3 HELPER connect, the request on stdin, never on the command line
        Assert.Equal({"-d", FakeProcessRunner.Distro, "--exec", WSLProcessRunner.PythonPath, WSLProcessRunner.HelperLinuxPath, "connect"}, connect.ProcessRequest.Arguments)
    End Function

    <Fact>
    Public Async Function Connect_InvalidInput_FailsBeforeWslIsCalled() As Task
        Dim fake As New FakeProcessRunner()
        Dim backend As WSL2NBDBackend = NewBackend(fake)
        Await backend.ProbeAsync(CancellationToken.None)

        Dim badIp = Await Assert.ThrowsAsync(Of StorageBackendException)(Function() backend.ConnectAsync("192.168.1", Port, CancellationToken.None))
        Assert.Equal(BackendErrorCodes.InvalidIp, badIp.Code)
        Dim badPort = Await Assert.ThrowsAsync(Of StorageBackendException)(Function() backend.ConnectAsync(Ip, 70000, CancellationToken.None))
        Assert.Equal(BackendErrorCodes.InvalidPort, badPort.Code)
        Assert.Empty(fake.CallsOf("connect"))
    End Function

    <Fact>
    Public Async Function Connect_ServerOff_ReportsUnreachableAndStaysDisconnected() As Task
        Dim fake As New FakeProcessRunner()
        fake.HelperHandlers("connect") = Function(r, p) Task.FromResult(FakeProcessRunner.Fail("NBD_SERVER_UNREACHABLE", "nbdinfo failed"))
        Dim backend As WSL2NBDBackend = NewBackend(fake)
        Await backend.ProbeAsync(CancellationToken.None)

        Dim ex = Await Assert.ThrowsAsync(Of StorageBackendException)(Function() backend.ConnectAsync(Ip, Port, CancellationToken.None))
        Assert.Equal(BackendErrorCodes.NbdServerUnreachable, ex.Code)
        Assert.Contains("nbd://" + Ip + ":10809", ex.Message)
        Assert.Equal(ConnectionState.Disconnected, backend.State)
        Assert.Empty(fake.KeepAlives)
    End Function

    <Fact>
    Public Async Function Connect_Twice_SameEndpointIsReused_OtherEndpointIsRefused() As Task
        Dim fake As New FakeProcessRunner()
        Dim backend As WSL2NBDBackend = Await ConnectedBackendAsync(fake)

        Dim again = Await backend.ConnectAsync(Ip, Port, CancellationToken.None)
        Assert.Equal(FakeProcessRunner.RawPath, again.HDLDriveName)
        Assert.Single(fake.CallsOf("connect"))

        Dim ex = Await Assert.ThrowsAsync(Of StorageBackendException)(Function() backend.ConnectAsync("192.168.1.51", Port, CancellationToken.None))
        Assert.Equal(BackendErrorCodes.NbdAlreadyConnectedOtherEndpoint, ex.Code)
        Assert.Equal(ConnectionState.Connected, backend.State)
    End Function

    <Fact>
    Public Async Function Disconnect_RemovesMountsAndStopsTheKeepAlive() As Task
        Dim fake As New FakeProcessRunner()
        fake.HelperHandlers("mount-pfs") = Function(r, p) Task.FromResult(FakeProcessRunner.Ok(MountJson(r.Value(Of String)("partition"), "PP.A-00000001")))
        Dim backend As WSL2NBDBackend = Await ConnectedBackendAsync(fake)
        Dim removed As New List(Of PfsMountHandle)
        AddHandler backend.MountRemoved, Sub(s, m) removed.Add(m)

        ' The mount is not visible to Windows here, so the backend unmounts it again and reports it.
        Dim ex = Await Assert.ThrowsAsync(Of StorageBackendException)(Function() backend.MountPfsPartitionAsync("PP.A", "Game A", CancellationToken.None))
        Assert.Equal(BackendErrorCodes.WindowsUncNotVisible, ex.Code)
        Assert.Single(fake.CallsOf("unmount-pfs"))
        Assert.Empty(backend.ActiveMounts)

        Await backend.DisconnectAsync(CancellationToken.None)
        Assert.Equal(ConnectionState.Disconnected, backend.State)
        Assert.True(fake.KeepAlives.Single().Disposed)
        Assert.Single(fake.CallsOf("disconnect"))
        Assert.Equal("", backend.MountedDrive.HDLDriveName & "")
    End Function

    <Fact>
    Public Async Function Disconnect_WhileAnOperationRuns_IsRefused() As Task
        Dim fake As New FakeProcessRunner()
        Dim gate As New TaskCompletionSource(Of String)(TaskCreationOptions.RunContinuationsAsynchronously)
        fake.HelperHandlers("hdl") = Function(r, p) gate.Task
        Dim backend As WSL2NBDBackend = Await ConnectedBackendAsync(fake)

        Dim running As Task(Of ProcessResult) = backend.RunHdlDumpAsync({"modify", FakeProcessRunner.RawPath, "Game", "New Title"}, Nothing, CancellationToken.None)
        Await WaitForStateAsync(backend, ConnectionState.BusyWrite)

        Dim ex = Await Assert.ThrowsAsync(Of StorageBackendException)(Function() backend.DisconnectAsync(CancellationToken.None))
        Assert.Equal(BackendErrorCodes.BackendBusy, ex.Code)
        Assert.Empty(fake.CallsOf("disconnect"))
        Assert.False(ConnectionStateRules.CanCloseApplication(backend.State))

        gate.SetResult(FakeProcessRunner.Ok(New JObject From {{"exit_code", 0}}))
        Assert.True((Await running).Succeeded)
        Assert.Equal(ConnectionState.Connected, backend.State)

        Await backend.DisconnectAsync(CancellationToken.None)
        Assert.Equal(ConnectionState.Disconnected, backend.State)
    End Function

    <Fact>
    Public Async Function Disconnect_HelperFailure_LeavesTheBackendFaulted() As Task
        Dim fake As New FakeProcessRunner()
        fake.HelperHandlers("disconnect") = Function(r, p) Task.FromResult(FakeProcessRunner.Fail("DISCONNECT_FAILED", "fusermount3 -u failed"))
        Dim backend As WSL2NBDBackend = Await ConnectedBackendAsync(fake)

        Dim ex = Await Assert.ThrowsAsync(Of StorageBackendException)(Function() backend.DisconnectAsync(CancellationToken.None))
        Assert.Equal(BackendErrorCodes.DisconnectFailed, ex.Code)
        Assert.Equal(ConnectionState.Faulted, backend.State)
    End Function

    <Fact>
    Public Async Function KeepAliveExit_MarksTheConnectionFaulted() As Task
        Dim fake As New FakeProcessRunner()
        Dim backend As WSL2NBDBackend = Await ConnectedBackendAsync(fake)

        fake.KeepAlives.Single().SimulateExit()
        Assert.Equal(ConnectionState.Faulted, backend.State)

        Dim ex = Await Assert.ThrowsAsync(Of StorageBackendException)(Function() backend.RunHdlDumpAsync({"toc", FakeProcessRunner.RawPath}, Nothing, CancellationToken.None))
        Assert.Equal(BackendErrorCodes.StaleMountState, ex.Code)
    End Function

#End Region

#Region "Tool routing"

    <Fact>
    Public Async Function HdlDump_TranslatesTheWorkingDirectoryAndPassesArgumentsAsAList() As Task
        Dim fake As New FakeProcessRunner()
        Dim backend As WSL2NBDBackend = Await ConnectedBackendAsync(fake)

        Dim result As ProcessResult = Await backend.RunHdlDumpAsync({"modify_header", FakeProcessRunner.RawPath, "PP.GAME"}, "C:\Projects\My Game", CancellationToken.None)
        Assert.True(result.Succeeded)
        Assert.Equal("hdl output", result.StandardOutput)

        Dim translate As HelperCall = fake.CallsOf("translate-path").Single()
        Assert.Equal("C:\Projects\My Game", translate.Request.Value(Of String)("windows_path"))
        Assert.True(translate.Request.Value(Of Boolean)("must_exist"))

        Dim hdl As HelperCall = fake.CallsOf("hdl").Single()
        Assert.Equal("/mnt/c/Projects/My Game", hdl.Request.Value(Of String)("cwd"))
        Assert.Equal({"modify_header", FakeProcessRunner.RawPath, "PP.GAME"}, hdl.Request("args").Values(Of String)().ToList())
        Assert.Equal(60, hdl.Request.Value(Of Integer)("timeout"))
        Assert.Equal(ConnectionState.Connected, backend.State)
    End Function

    <Fact>
    Public Async Function ToolFailure_IsReturnedWithItsOutput_RefusalIsThrown() As Task
        Dim fake As New FakeProcessRunner()
        fake.HelperHandlers("hdl") = Function(r, p) Task.FromResult(FakeProcessRunner.Fail("HDL_DUMP_FAILED", "hdl_dump failed", New JObject From {{"exit_code", 114}}))
        fake.HelperHandlers("pfsshell") = Function(r, p) Task.FromResult(FakeProcessRunner.Fail("NBD_MOUNT_NOT_READY", "raw file vanished"))
        Dim backend As WSL2NBDBackend = Await ConnectedBackendAsync(fake)

        Dim failed As ProcessResult = Await backend.RunHdlDumpAsync({"inject_cd", FakeProcessRunner.RawPath, "T", "/mnt/c/x.iso", "SLUS_123.45", "*u4"}, Nothing, CancellationToken.None)
        Assert.False(failed.Succeeded)
        Assert.Equal(114, failed.ExitCode)
        Assert.Contains("tool said no", failed.CombinedOutput)
        Assert.Equal(ConnectionState.Connected, backend.State)

        Dim ex = Await Assert.ThrowsAsync(Of StorageBackendException)(Function() backend.RunPfsShellAsync({"device " + FakeProcessRunner.RawPath, "ls", "exit"}, Nothing, CancellationToken.None))
        Assert.Equal(BackendErrorCodes.NbdMountNotReady, ex.Code)
        Assert.Equal(ConnectionState.Faulted, backend.State)
    End Function

    <Fact>
    Public Async Function PfsShell_RejectsInjectedLineBreaks() As Task
        Dim fake As New FakeProcessRunner()
        Dim backend As WSL2NBDBackend = Await ConnectedBackendAsync(fake)
        Dim ex = Await Assert.ThrowsAsync(Of StorageBackendException)(Function() backend.RunPfsShellAsync({"device " + FakeProcessRunner.RawPath, "mount PP.A" & vbLf & "rmpart __system"}, Nothing, CancellationToken.None))
        Assert.Equal(BackendErrorCodes.InvalidRequest, ex.Code)
        Assert.Empty(fake.CallsOf("pfsshell"))
    End Function

    <Fact>
    Public Async Function Operations_RunOneAtATime() As Task
        Dim fake As New FakeProcessRunner()
        Dim gate As New TaskCompletionSource(Of String)(TaskCreationOptions.RunContinuationsAsynchronously)
        fake.HelperHandlers("hdl") = Function(r, p) gate.Task
        Dim backend As WSL2NBDBackend = Await ConnectedBackendAsync(fake)

        Dim first As Task(Of ProcessResult) = backend.RunHdlDumpAsync({"toc", FakeProcessRunner.RawPath}, Nothing, CancellationToken.None)
        Await WaitForStateAsync(backend, ConnectionState.BusyRead)
        Dim second As Task(Of ProcessResult) = backend.RunPfsShellAsync({"device " + FakeProcessRunner.RawPath, "ls", "exit"}, Nothing, CancellationToken.None)
        Await Task.Delay(200)
        Assert.False(second.IsCompleted)
        Assert.Empty(fake.CallsOf("pfsshell"))

        gate.SetResult(FakeProcessRunner.Ok(New JObject From {{"exit_code", 0}}))
        Await first
        Await second
        Assert.Single(fake.CallsOf("pfsshell"))
    End Function

    <Fact>
    Public Async Function Inject_Cancellation_GoesThroughTheHelperCancelVerb() As Task
        Dim fake As New FakeProcessRunner()
        Dim cancelReceived As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        fake.HelperHandlers("cancel") = Function(r, p)
                                            cancelReceived.TrySetResult(True)
                                            Return Task.FromResult(FakeProcessRunner.Ok(New JObject()))
                                        End Function
        fake.HelperHandlers("hdl") = Async Function(r, p)
                                         p.StandardErrorLineCallback?.Invoke(" 12%")
                                         Await cancelReceived.Task
                                         Return FakeProcessRunner.Fail("OPERATION_CANCELLED", "cancelled", New JObject From {{"exit_code", 130}})
                                     End Function
        Dim backend As WSL2NBDBackend = Await ConnectedBackendAsync(fake)
        Dim progress As New ListProgress()

        Using cts As New CancellationTokenSource()
            Dim inject As Task(Of ProcessResult) = backend.RunHdlDumpAsync({"inject_dvd", FakeProcessRunner.RawPath, "Title", "/mnt/c/g.iso", "SLUS_123.45", "*u4", "-hide"}, Nothing, cts.Token, progress)
            Await WaitForStateAsync(backend, ConnectionState.BusyWrite)
            cts.Cancel()
            Dim result As ProcessResult = Await inject

            Assert.True(result.Cancelled)
            Assert.False(result.Succeeded)
        End Using
        Assert.Single(fake.CallsOf("cancel"))
        Assert.False(fake.CallsOf("hdl").Single().ProcessRequest.KillOnCancel) ' wsl.exe is never killed mid-injection
        Assert.Contains(" 12%", progress.Lines)
        Assert.Equal(ConnectionState.Connected, backend.State)
    End Function

    <Fact>
    Public Async Function Restore_WithMountedPartitions_IsRefused() As Task
        Dim fake As New FakeProcessRunner()
        fake.HelperHandlers("probe") = Function(r, p) Task.FromResult(FakeProcessRunner.Ok(New JObject From {
            {"protocol_version", 1},
            {"status", New JObject From {{"state", "healthy"}, {"raw_path", FakeProcessRunner.RawPath}, {"ip", Ip}, {"port", Port},
                                         {"pfs_mounts", New JArray(MountJson("PP.A", "PP.A-00000001"))}}}
        }))
        Dim backend As WSL2NBDBackend = NewBackend(fake)
        Await backend.ProbeAsync(CancellationToken.None)

        Dim ex = Await Assert.ThrowsAsync(Of StorageBackendException)(Function() backend.RestoreRawDeviceAsync("C:\b.img", "1M", Nothing, CancellationToken.None))
        Assert.Equal(BackendErrorCodes.BackendBusy, ex.Code)
        Assert.Empty(fake.CallsOf("restore"))

        Dim badBlock = Assert.Throws(Of StorageBackendException)(Sub() backend.BackupRawDeviceAsync("C:\b.img", "2M", Nothing, CancellationToken.None))
        Assert.Equal(BackendErrorCodes.InvalidRequest, badBlock.Code)
    End Function

#End Region

#Region "Mount tracking"

    <Fact>
    Public Async Function MountedPartition_BlocksWritesUntilUnmounted() As Task
        Dim fake As New FakeProcessRunner()
        fake.HelperHandlers("probe") = Function(r, p) Task.FromResult(FakeProcessRunner.Ok(New JObject From {
            {"protocol_version", 1},
            {"status", New JObject From {{"state", "healthy"}, {"raw_path", FakeProcessRunner.RawPath}, {"ip", Ip}, {"port", Port},
                                         {"pfs_mounts", New JArray(MountJson("PP.GAME", "PP.GAME-0123abcd"))}}}
        }))
        Dim backend As WSL2NBDBackend = NewBackend(fake)
        Await backend.ProbeAsync(CancellationToken.None)
        Dim mount As PfsMountHandle = backend.ActiveMounts.Single()

        ' Mounting the same partition again returns the existing handle without asking WSL.
        Dim same As PfsMountHandle = Await backend.MountPfsPartitionAsync("PP.GAME", "Game", CancellationToken.None)
        Assert.Same(mount, same)
        Assert.Empty(fake.CallsOf("mount-pfs"))

        Dim pfsWrite = Await Assert.ThrowsAsync(Of StorageBackendException)(Function() backend.RunPfsShellAsync({"device " + FakeProcessRunner.RawPath, "rmpart PP.GAME", "exit"}, Nothing, CancellationToken.None))
        Assert.Equal(BackendErrorCodes.BackendBusy, pfsWrite.Code)
        Dim hdlWrite = Await Assert.ThrowsAsync(Of StorageBackendException)(Function() backend.RunHdlDumpAsync({"modify_header", FakeProcessRunner.RawPath, "PP.GAME"}, Nothing, CancellationToken.None))
        Assert.Equal(BackendErrorCodes.BackendBusy, hdlWrite.Code)
        Assert.Empty(fake.CallsOf("pfsshell"))
        Assert.Empty(fake.CallsOf("hdl"))

        ' Reading the mounted partition through pfsshell is allowed.
        Assert.True((Await backend.RunPfsShellAsync({"device " + FakeProcessRunner.RawPath, "mount PP.GAME", "ls", "umount", "exit"}, Nothing, CancellationToken.None)).Succeeded)

        Dim removed As PfsMountHandle = Nothing
        AddHandler backend.MountRemoved, Sub(s, m) removed = m
        Await backend.UnmountPfsPartitionAsync(mount, CancellationToken.None)
        Assert.Same(mount, removed)
        Assert.Empty(backend.ActiveMounts)
        Assert.Equal("PP.GAME-0123abcd", fake.CallsOf("unmount-pfs").Single().Request.Value(Of String)("mount_id"))

        Assert.True((Await backend.RunPfsShellAsync({"device " + FakeProcessRunner.RawPath, "rmpart PP.GAME", "exit"}, Nothing, CancellationToken.None)).Succeeded)
    End Function

    <Fact>
    Public Async Function Mount_VisibleThroughUnc_IsTracked() As Task
        ' On Linux "\\wsl.localhost\..." is an ordinary relative file name, so creating it simulates Windows seeing the
        ' mount. On Windows the share would have to exist, which a unit test cannot arrange.
        If Path.DirectorySeparatorChar = "\"c Then Return

        Dim fake As New FakeProcessRunner()
        Dim mountId As String = "PP.VISIBLE-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        fake.HelperHandlers("mount-pfs") = Function(r, p) Task.FromResult(FakeProcessRunner.Ok(MountJson(r.Value(Of String)("partition"), mountId)))
        Dim backend As WSL2NBDBackend = Await ConnectedBackendAsync(fake)
        Dim unc As String = StorageValidation.LinuxPathToWslUnc(FakeProcessRunner.Distro, "/home/tester/.local/share/psx-xmb-manager/pfs/" + mountId)
        Directory.CreateDirectory(unc)
        Try
            Dim handle As PfsMountHandle = Await backend.MountPfsPartitionAsync("PP.VISIBLE", "Visible Game", CancellationToken.None)
            Assert.Equal(unc, handle.WindowsPath)
            Assert.Equal("", handle.LegacyDriveLetter)
            Assert.Equal("Visible Game", fake.CallsOf("mount-pfs").Single().Request.Value(Of String)("display_name"))
            Assert.Single(backend.ActiveMounts)

            Dim removed As New List(Of PfsMountHandle)
            AddHandler backend.MountRemoved, Sub(s, m) removed.Add(m)
            Await backend.DisconnectAsync(CancellationToken.None)
            Assert.Single(removed)
            Assert.Equal(mountId, removed(0).MountId)
        Finally
            Directory.Delete(unc)
        End Try
    End Function

    <Fact>
    Public Async Function Mount_RejectsInvalidPartitionNames() As Task
        Dim fake As New FakeProcessRunner()
        Dim backend As WSL2NBDBackend = Await ConnectedBackendAsync(fake)
        Dim ex = Await Assert.ThrowsAsync(Of StorageBackendException)(Function() backend.MountPfsPartitionAsync("PP.A" & vbLf & "x", "x", CancellationToken.None))
        Assert.Equal(BackendErrorCodes.InvalidPartitionName, ex.Code)
        Assert.Empty(fake.CallsOf("mount-pfs"))
    End Function

#End Region

End Class
