Imports System.IO
Imports System.Threading
Imports Newtonsoft.Json.Linq
Imports PSX_XMB_Manager.Structs

''' <summary>
''' Remote PSX HDD over Open PS2 Loader's NBD server, through WSL2:
''' wsl.exe -> selected distro -> nbdfuse -> raw file -> Linux hdl_dump / pfsshell / pfsfuse.
''' All Linux work happens in psx-xmb-helper.py; this class only talks JSON to it through <see cref="WSLProcessRunner"/>.
''' </summary>
Public Class WSL2NBDBackend
    Implements IPSXStorageBackend

    ''' <summary>Must equal PROTOCOL_VERSION in WSL/psx-xmb-helper.py.</summary>
    Public Const ExpectedHelperProtocolVersion As Integer = 1
    Public Const DefaultNbdPort As Integer = 10809

    Private ReadOnly _wsl As WSLProcessRunner
    Private ReadOnly _operationLock As New SemaphoreSlim(1, 1)
    Private ReadOnly _stateLock As New Object()
    Private ReadOnly _mounts As New Dictionary(Of String, PfsMountHandle)(StringComparer.Ordinal)
    Private _state As ConnectionState = ConnectionState.Unavailable
    Private _mountedDrive As MountedPSXDrive
    Private _keepAlive As ILongRunningProcess
    Private _distro As String = ""
    Private _helperReady As Boolean

    Public Event StateChanged As EventHandler Implements IPSXStorageBackend.StateChanged
    Public Event MountRemoved As EventHandler(Of PfsMountHandle) Implements IPSXStorageBackend.MountRemoved

    Public Sub New(distro As String, Optional runner As WSLProcessRunner = Nothing)
        _wsl = If(runner, New WSLProcessRunner())
        _distro = If(distro, "")
    End Sub

    ''' <summary>The selected WSL2 distribution. Can only change while disconnected.</summary>
    Public Property DistroName As String
        Get
            Return _distro
        End Get
        Set(value As String)
            If ConnectionStateRules.IsHddAvailable(State) OrElse State = ConnectionState.Connecting OrElse State = ConnectionState.Disconnecting Then
                Throw New StorageBackendException(BackendErrorCodes.BackendBusy, "The WSL distribution cannot be changed while the PSX is connected.")
            End If
            _distro = If(value, "")
            _helperReady = False
        End Set
    End Property

    Public ReadOnly Property Runner As WSLProcessRunner
        Get
            Return _wsl
        End Get
    End Property

    Public ReadOnly Property Kind As StorageBackendKind Implements IPSXStorageBackend.Kind
        Get
            Return StorageBackendKind.WSL2NBD
        End Get
    End Property

    Public ReadOnly Property DisplayName As String Implements IPSXStorageBackend.DisplayName
        Get
            Return "WSL2 NBD"
        End Get
    End Property

    Public ReadOnly Property MountedDrive As MountedPSXDrive Implements IPSXStorageBackend.MountedDrive
        Get
            SyncLock _stateLock
                Return _mountedDrive
            End SyncLock
        End Get
    End Property

    Public ReadOnly Property IsConnected As Boolean Implements IPSXStorageBackend.IsConnected
        Get
            Return ConnectionStateRules.IsHddAvailable(State)
        End Get
    End Property

    Public ReadOnly Property State As ConnectionState Implements IPSXStorageBackend.State
        Get
            SyncLock _stateLock
                Return _state
            End SyncLock
        End Get
    End Property

    Public ReadOnly Property ActiveMounts As IReadOnlyList(Of PfsMountHandle) Implements IPSXStorageBackend.ActiveMounts
        Get
            SyncLock _stateLock
                Return _mounts.Values.ToList()
            End SyncLock
        End Get
    End Property

    Private Sub SetState(newState As ConnectionState)
        Dim changed As Boolean
        SyncLock _stateLock
            If Not ConnectionStateRules.IsValidTransition(_state, newState) Then
                BackendLog.Note(Kind, _distro, "rejected state transition " + _state.ToString() + " -> " + newState.ToString())
                Throw New InvalidOperationException("Invalid backend state transition " + _state.ToString() + " -> " + newState.ToString())
            End If
            changed = _state <> newState
            _state = newState
        End SyncLock
        If changed Then
            BackendLog.Note(Kind, _distro, "state -> " + newState.ToString())
            RaiseEvent StateChanged(Me, EventArgs.Empty)
        End If
    End Sub

#Region "Probe"

    ' Fixed probe script: no user-controlled text, sent on stdin to /bin/sh.
    Private Const ProbeScript As String =
        "echo ""user=$(id -un 2>/dev/null)""" & vbLf &
        "echo ""home=$HOME""" & vbLf &
        "for t in python3 nbdfuse nbdinfo hdl_dump pfsshell pfsfuse fusermount3 fusermount; do" & vbLf &
        "  if command -v ""$t"" >/dev/null 2>&1; then echo ""tool.$t=$(command -v ""$t"")""; else echo ""tool.$t=""; fi" & vbLf &
        "done" & vbLf &
        "if [ -c /dev/fuse ]; then echo devfuse=1; else echo devfuse=0; fi" & vbLf &
        "if [ -f " & WSLProcessRunner.HelperLinuxPath & " ]; then echo helper=1; else echo helper=0; fi" & vbLf &
        "echo '--os-release--'" & vbLf &
        "cat /etc/os-release 2>/dev/null" & vbLf

    Public Async Function ProbeAsync(cancellationToken As CancellationToken) As Task(Of BackendProbeResult) Implements IPSXStorageBackend.ProbeAsync
        Dim result As New BackendProbeResult With {.BackendKind = Kind, .SelectedDistro = _distro, .WslExePath = _wsl.WslExePath}
        Try
            Await ProbeCoreAsync(result, cancellationToken).ConfigureAwait(False)
        Catch ex As StorageBackendException
            result.IsReady = False
            If String.IsNullOrEmpty(result.SetupErrorCode) Then
                result.SetupErrorCode = ex.Code
                result.SetupMessage = ex.Message
            End If
        End Try
        ApplyProbeToState(result)
        Return result
    End Function

    Private Async Function ProbeCoreAsync(result As BackendProbeResult, cancellationToken As CancellationToken) As Task
        ' 1. WSL itself
        If Not _wsl.IsWslInstalled Then
            result.SetupErrorCode = BackendErrorCodes.WslNotInstalled
            result.SetupMessage = "WSL is not installed (wsl.exe was not found)."
            Return
        End If
        result.WslInstalled = True

        ' 2. Distributions; only WSL2 is eligible
        result.AvailableDistros = Await _wsl.ListDistrosAsync(cancellationToken).ConfigureAwait(False)
        If Not result.AvailableDistros.Any(Function(d) d.Version = 2) Then
            result.SetupErrorCode = BackendErrorCodes.NoWsl2Distro
            result.SetupMessage = If(result.AvailableDistros.Count = 0,
                                     "No WSL distribution is installed.",
                                     "Only WSL1 distributions are installed: " + String.Join(", ", result.AvailableDistros.Select(Function(d) d.Name)) + ".")
            Return
        End If
        If String.IsNullOrEmpty(_distro) Then
            result.SetupErrorCode = BackendErrorCodes.WslDistroNotFound
            result.SetupMessage = "Several WSL2 distributions are installed. Select the one to use."
            Return
        End If
        Dim selected As WslDistroInfo = result.AvailableDistros.FirstOrDefault(Function(d) String.Equals(d.Name, _distro, StringComparison.OrdinalIgnoreCase))
        If selected Is Nothing Then
            result.SetupErrorCode = BackendErrorCodes.WslDistroNotFound
            result.SetupMessage = "The WSL distribution '" + _distro + "' is not installed."
            Return
        End If
        result.WslVersion = selected.Version
        If selected.Version <> 2 Then
            result.SetupErrorCode = BackendErrorCodes.NoWsl2Distro
            result.SetupMessage = "'" + _distro + "' runs as WSL" + selected.Version.ToString() + ". Convert it with 'wsl --set-version " + _distro + " 2'."
            Return
        End If

        ' 3. Start the distribution once with a generous budget, then probe within the normal 5 s limits.
        Dim start As ProcessResult = Await _wsl.RunFixedAsync(_distro, {"/bin/true"}, OperationTimeouts.WslDistroStart, cancellationToken).ConfigureAwait(False)
        If Not start.Succeeded Then
            result.SetupErrorCode = If(start.TimedOut, BackendErrorCodes.OperationTimeout, BackendErrorCodes.WslLaunchFailed)
            result.SetupMessage = "The WSL distribution '" + _distro + "' could not be started. " + (OutputText.DecodeWslText(start.StandardOutputBytes) + " " + OutputText.DecodeWslText(start.StandardErrorBytes)).Trim()
            Return
        End If

        Dim probe As ProcessResult = Await _wsl.RunFixedScriptAsync(_distro, ProbeScript, OperationTimeouts.WslProbe, cancellationToken).ConfigureAwait(False)
        If probe.TimedOut Then Throw New StorageBackendException(BackendErrorCodes.OperationTimeout, "The WSL distribution '" + _distro + "' did not answer the tool probe in time.")
        ParseProbeScript(probe.StandardOutput, result)

        ' 4. Helper (versions, existing connection, PFS mounts)
        If result.HelperInstalled AndAlso result.Python3Present Then
            Dim response As HelperResponse = Await _wsl.RunHelperAsync(_distro, "probe", New JObject(), OperationTimeouts.WithLaunchOverhead(OperationTimeouts.HelperStatus), cancellationToken).ConfigureAwait(False)
            If response.Ok Then
                result.HelperVersion = response.GetInt("protocol_version")
                result.NbdfuseVersion = response.GetString("nbdfuse_version")
                result.NbdinfoVersion = response.GetString("nbdinfo_version")
                If String.IsNullOrEmpty(result.LinuxHome) Then result.LinuxHome = response.GetString("home")
                ReadConnectionStatus(TryCast(response.Data("status"), JObject), result)
            Else
                result.HelperVersion = response.GetInt("protocol_version")
            End If
        End If

        ' 5. Readiness, first missing piece decides the code
        result.MissingDependencies.Clear()
        If Not result.DevFusePresent Then result.MissingDependencies.Add("/dev/fuse")
        If Not result.Python3Present Then result.MissingDependencies.Add("python3")
        If Not result.NbdinfoPresent Then result.MissingDependencies.Add("nbdinfo (libnbd-bin)")
        If Not result.NbdfusePresent Then result.MissingDependencies.Add("nbdfuse (libnbd-bin)")
        If Not result.Fusermount3Present Then result.MissingDependencies.Add("fusermount3 (fuse3)")
        If Not result.FusermountPresent Then result.MissingDependencies.Add("fusermount (FUSE 2 compatible)")
        If Not result.HdlDumpPresent Then result.MissingDependencies.Add("hdl_dump")
        If Not result.PfsShellPresent Then result.MissingDependencies.Add("pfsshell")
        If Not result.PfsFusePresent Then result.MissingDependencies.Add("pfsfuse")
        If Not result.HelperInstalled Then result.MissingDependencies.Add("psx-xmb-helper")

        If Not result.DevFusePresent Then
            SetSetupError(result, BackendErrorCodes.FuseUnavailable, "/dev/fuse is not available in '" + _distro + "'.")
        ElseIf Not result.NbdinfoPresent Then
            SetSetupError(result, BackendErrorCodes.NbdinfoMissing, "nbdinfo is not installed in '" + _distro + "'.")
        ElseIf Not result.NbdfusePresent Then
            SetSetupError(result, BackendErrorCodes.NbdfuseMissing, "nbdfuse is not installed in '" + _distro + "'.")
        ElseIf Not result.HdlDumpPresent Then
            SetSetupError(result, BackendErrorCodes.HdlDumpMissing, "hdl_dump is not installed in '" + _distro + "'.")
        ElseIf Not result.PfsShellPresent Then
            SetSetupError(result, BackendErrorCodes.PfsShellMissing, "pfsshell is not installed in '" + _distro + "'.")
        ElseIf Not result.PfsFusePresent Then
            SetSetupError(result, BackendErrorCodes.PfsFuseMissing, "pfsfuse is not installed in '" + _distro + "'.")
        ElseIf Not result.Fusermount3Present OrElse Not result.FusermountPresent OrElse Not result.Python3Present OrElse Not result.HelperInstalled Then
            SetSetupError(result, BackendErrorCodes.BackendSetupRequired, "The WSL backend is not fully installed in '" + _distro + "': missing " + String.Join(", ", result.MissingDependencies) + ".")
        ElseIf result.HelperVersion <> ExpectedHelperProtocolVersion Then
            SetSetupError(result, BackendErrorCodes.HelperVersionMismatch, BackendErrorCodes.HelperOutdatedMessage)
        Else
            result.IsReady = True
        End If

        If Not result.IsReady AndAlso Not result.DistroSupportsAutomaticSetup AndAlso result.SetupErrorCode <> BackendErrorCodes.HelperVersionMismatch Then
            result.SetupMessage += " Automatic setup supports Ubuntu and Debian only; this distribution is '" + If(result.DistroOsId = "", "unknown", result.DistroOsId) + "'."
        End If
        _helperReady = result.IsReady
    End Function

    Private Shared Sub SetSetupError(result As BackendProbeResult, code As String, message As String)
        result.IsReady = False
        result.SetupErrorCode = code
        result.SetupMessage = message
    End Sub

    Private Shared Sub ParseProbeScript(output As String, result As BackendProbeResult)
        Dim lines As String() = OutputText.SplitLines(output)
        Dim osStart As Integer = Array.IndexOf(lines, "--os-release--")
        Dim keyLines As IEnumerable(Of String) = If(osStart >= 0, lines.Take(osStart), lines)
        Dim values As New Dictionary(Of String, String)(StringComparer.Ordinal)
        For Each line As String In keyLines
            Dim equals As Integer = line.IndexOf("="c)
            If equals > 0 Then values(line.Substring(0, equals)) = line.Substring(equals + 1)
        Next
        Dim value As String = ""
        result.LinuxUser = If(values.TryGetValue("user", value), value, "")
        result.LinuxHome = If(values.TryGetValue("home", value), value, "")
        result.Python3Present = values.TryGetValue("tool.python3", value) AndAlso value.Length > 0
        result.NbdfusePresent = values.TryGetValue("tool.nbdfuse", value) AndAlso value.Length > 0
        result.NbdinfoPresent = values.TryGetValue("tool.nbdinfo", value) AndAlso value.Length > 0
        result.HdlDumpPresent = values.TryGetValue("tool.hdl_dump", value) AndAlso value.Length > 0
        result.PfsShellPresent = values.TryGetValue("tool.pfsshell", value) AndAlso value.Length > 0
        result.PfsFusePresent = values.TryGetValue("tool.pfsfuse", value) AndAlso value.Length > 0
        result.Fusermount3Present = values.TryGetValue("tool.fusermount3", value) AndAlso value.Length > 0
        result.FusermountPresent = values.TryGetValue("tool.fusermount", value) AndAlso value.Length > 0
        result.DevFusePresent = values.TryGetValue("devfuse", value) AndAlso value = "1"
        result.HelperInstalled = values.TryGetValue("helper", value) AndAlso value = "1"

        If osStart >= 0 Then
            Dim osRelease As Dictionary(Of String, String) = WslOutputParsers.ParseOsRelease(String.Join(vbLf, lines.Skip(osStart + 1)))
            result.DistroOsId = If(osRelease.TryGetValue("ID", value), value, "")
            result.DistroOsVersion = If(osRelease.TryGetValue("VERSION_ID", value), value, "")
            result.DistroOsName = If(osRelease.TryGetValue("PRETTY_NAME", value), value, result.DistroOsId)
            result.DistroSupportsAutomaticSetup = WslOutputParsers.SupportsAutomaticSetup(osRelease)
        End If
    End Sub

    Private Sub ReadConnectionStatus(status As JObject, result As BackendProbeResult)
        If status Is Nothing Then Return
        result.ConnectionStatus = If(status.Value(Of String)("state"), "")
        result.NbdMounted = If(status.Value(Of Boolean?)("nbd_mounted"), False)
        result.RawDevicePath = If(status.Value(Of String)("raw_path"), "")
        result.RecordedIp = If(status.Value(Of String)("ip"), "")
        result.RecordedPort = If(status.Value(Of Integer?)("port"), 0)
        result.RawDeviceSize = If(status.Value(Of Long?)("raw_size"), 0L)
        result.ActivePfsMounts = ReadMounts(TryCast(status("pfs_mounts"), JArray))
    End Sub

    Private Function ReadMounts(array As JArray) As List(Of PfsMountHandle)
        Dim mountHandles As New List(Of PfsMountHandle)
        If array Is Nothing Then Return mountHandles
        For Each item As JToken In array
            Dim entry = TryCast(item, JObject)
            If entry Is Nothing Then Continue For
            Dim mountId As String = If(entry.Value(Of String)("mount_id"), "")
            Dim linuxPath As String = If(entry.Value(Of String)("path"), "")
            If Not StorageValidation.IsSafeMountId(mountId) OrElse Not linuxPath.StartsWith("/") Then Continue For
            mountHandles.Add(New PfsMountHandle With {
                .BackendKind = Kind,
                .MountId = mountId,
                .PartitionName = If(entry.Value(Of String)("partition"), ""),
                .DisplayName = If(entry.Value(Of String)("display_name"), ""),
                .NativePath = linuxPath,
                .WindowsPath = StorageValidation.LinuxPathToWslUnc(_distro, linuxPath),
                .LegacyDriveLetter = ""
            })
        Next
        Return mountHandles
    End Function

    ''' <summary>
    ''' Startup stale-state recovery: a healthy existing mount is adopted as the current session (never mounted twice),
    ''' a broken one leaves the backend Faulted so the UI offers recovery. Never reconnects on its own.
    ''' </summary>
    Private Sub ApplyProbeToState(result As BackendProbeResult)
        Dim current As ConnectionState = State
        If current <> ConnectionState.Unavailable AndAlso current <> ConnectionState.NeedsSetup AndAlso
           current <> ConnectionState.Disconnected AndAlso current <> ConnectionState.Faulted Then
            Return ' A session is active; a probe never changes it.
        End If

        If Not result.WslInstalled OrElse result.SetupErrorCode = BackendErrorCodes.NoWsl2Distro OrElse
           result.SetupErrorCode = BackendErrorCodes.WslDistroNotFound OrElse result.SetupErrorCode = BackendErrorCodes.WslNotInstalled Then
            SetState(ConnectionState.Unavailable)
            Return
        End If
        If Not result.IsReady Then
            SetState(ConnectionState.NeedsSetup)
            Return
        End If

        Select Case result.ConnectionStatus
            Case "healthy"
                AdoptExistingSession(result)
            Case "", "disconnected"
                SetState(ConnectionState.Disconnected)
            Case Else
                ' stale-state-file, mounted-without-state, raw-file-missing, nbd-process-gone, ...
                SetState(ConnectionState.Faulted)
        End Select
    End Sub

    Private Sub AdoptExistingSession(result As BackendProbeResult)
        Dim drive As MountedPSXDrive = BuildMountedDrive(result.RecordedIp, result.RecordedPort, result.RawDevicePath)
        SyncLock _stateLock
            _mountedDrive = drive
            _mounts.Clear()
            For Each handle As PfsMountHandle In result.ActivePfsMounts
                _mounts(handle.MountId) = handle
            Next
        End SyncLock
        StartKeepAlive()
        SetState(ConnectionState.Connected)
        BackendLog.Note(Kind, _distro, "recovered existing NBD session to " + result.RecordedIp + ":" + result.RecordedPort.ToString())
    End Sub

#End Region

#Region "Connection lifecycle"

    Public Async Function ConnectAsync(ipAddress As String, port As Integer, cancellationToken As CancellationToken) As Task(Of MountedPSXDrive) Implements IPSXStorageBackend.ConnectAsync
        Dim ip As String = ""
        If Not StorageValidation.TryParseIPv4(ipAddress, ip) Then
            Throw New StorageBackendException(BackendErrorCodes.InvalidIp, "'" + ipAddress + "' is not a valid IPv4 address.")
        End If
        If Not StorageValidation.IsValidPort(port) Then
            Throw New StorageBackendException(BackendErrorCodes.InvalidPort, "Port " + port.ToString() + " is not between 1 and 65535.")
        End If

        Await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(False)
        Try
            Dim current As ConnectionState = State
            If ConnectionStateRules.IsHddAvailable(current) Then
                Dim existing As MountedPSXDrive = MountedDrive
                If existing.ConnectedOnIP = ip AndAlso existing.NBDPort = port Then Return existing
                Throw New StorageBackendException(BackendErrorCodes.NbdAlreadyConnectedOtherEndpoint,
                                                  "Already connected to " + existing.ConnectedOnIP + ":" + existing.NBDPort.ToString() + ".")
            End If
            If Not ConnectionStateRules.CanConnect(current) Then
                Throw New StorageBackendException(If(current = ConnectionState.Faulted, BackendErrorCodes.StaleMountState, BackendErrorCodes.BackendSetupRequired),
                                                  "Cannot connect while the WSL backend is '" + current.ToString() + "'.")
            End If

            SetState(ConnectionState.Connecting)
            Try
                Dim request As New JObject From {{"ip", ip}, {"port", port}}
                ' nbdinfo (5 s) + nbdfuse readiness (10 s) + hdl_dump toc (30 s) + unmount on failure (10 s)
                Dim helperTimeout As TimeSpan = OperationTimeouts.NbdInfo + OperationTimeouts.NbdfuseReadiness + OperationTimeouts.HdlDumpToc + OperationTimeouts.UnmountConfirmation
                ' Connect is never killed half way: a killed connect could leave nbdfuse mounted.
                Dim response As HelperResponse = Await _wsl.RunHelperAsync(_distro, "connect", request, OperationTimeouts.WithLaunchOverhead(helperTimeout),
                                                                         CancellationToken.None).ConfigureAwait(False)
                If Not response.Ok Then
                    Throw New StorageBackendException(response.Code, ConnectFailureMessage(response, ip, port), response.Stderr)
                End If

                Dim drive As MountedPSXDrive = BuildMountedDrive(ip, port, response.GetString("raw_path"))
                SyncLock _stateLock
                    _mountedDrive = drive
                    _mounts.Clear()
                End SyncLock
                StartKeepAlive()
                SetState(ConnectionState.Connected)
                Return drive
            Catch
                ' The helper guarantees that a failed connect leaves nothing mounted.
                If State = ConnectionState.Connecting Then SetState(ConnectionState.Disconnected)
                Throw
            End Try
        Finally
            _operationLock.Release()
        End Try
    End Function

    Private Shared Function ConnectFailureMessage(response As HelperResponse, ip As String, port As Integer) As String
        Dim endpoint As String = "nbd://" + ip + ":" + port.ToString()
        Select Case response.Code
            Case BackendErrorCodes.NbdServerUnreachable
                Return "The PSX NBD server at " + endpoint + " could not be reached."
            Case BackendErrorCodes.PsxHddValidationFailed
                Return "Connected to " + endpoint + ", but hdl_dump did not recognise a PS2/PSX HDD."
            Case Else
                Return If(String.IsNullOrEmpty(response.Message), "Could not connect to " + endpoint + ".", response.Message)
        End Select
    End Function

    Private Function BuildMountedDrive(ip As String, port As Integer, rawPath As String) As MountedPSXDrive
        If String.IsNullOrEmpty(rawPath) OrElse Not rawPath.StartsWith("/") Then
            Throw New StorageBackendException(BackendErrorCodes.HelperProtocolError, "The WSL helper did not report the NBD raw device path.")
        End If
        Dim mountRoot As String = rawPath.Substring(0, rawPath.LastIndexOf("/"c))
        Return New MountedPSXDrive With {
            .BackendKind = StorageBackendKind.WSL2NBD,
            .ConnectedOnIP = ip,
            .NBDPort = port,
            .NativeDevicePath = rawPath,
            .WindowsAccessibleDevicePath = StorageValidation.LinuxPathToWslUnc(_distro, rawPath),
            .WslDistro = _distro,
            .WslMountRoot = mountRoot,
            .IsReadOnly = False,
            .DriveID = rawPath,
            .HDLDriveName = rawPath,
            .NBDDriveName = "WSL2-NBD"
        }
    End Function

    Private Sub StartKeepAlive()
        StopKeepAlive()
        Try
            Dim keepAlive As ILongRunningProcess = _wsl.StartKeepAlive(_distro)
            AddHandler keepAlive.Exited, AddressOf KeepAlive_Exited
            _keepAlive = keepAlive
        Catch ex As Exception When TypeOf ex Is StorageBackendException OrElse TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is InvalidOperationException
            BackendLog.Note(Kind, _distro, "keepalive could not be started: " + ex.Message)
        End Try
    End Sub

    Private Sub StopKeepAlive()
        Dim keepAlive As ILongRunningProcess = _keepAlive
        _keepAlive = Nothing
        If keepAlive IsNot Nothing Then
            RemoveHandler keepAlive.Exited, AddressOf KeepAlive_Exited
            keepAlive.Dispose()
        End If
    End Sub

    ''' <summary>The distribution was stopped (for example "wsl --shutdown"): the NBD mount is gone with it.</summary>
    Private Sub KeepAlive_Exited(sender As Object, e As EventArgs)
        If sender IsNot _keepAlive Then Return
        BackendLog.Note(Kind, _distro, "keepalive exited while connected; marking the connection as faulted")
        SyncLock _stateLock
            If _state <> ConnectionState.Connected AndAlso _state <> ConnectionState.BusyRead AndAlso _state <> ConnectionState.BusyWrite Then Return
        End SyncLock
        Try
            SetState(ConnectionState.Faulted)
        Catch ex As InvalidOperationException
        End Try
    End Sub

    Public Async Function DisconnectAsync(cancellationToken As CancellationToken) As Task Implements IPSXStorageBackend.DisconnectAsync
        ' Never wait behind a running operation: a write in progress refuses the disconnect.
        If Not Await _operationLock.WaitAsync(0).ConfigureAwait(False) Then
            Throw New StorageBackendException(BackendErrorCodes.BackendBusy,
                                              "A PSX HDD operation is still running (" + State.ToString() + "). Disconnect after it has finished.")
        End If
        Try
            Dim current As ConnectionState = State
            If current = ConnectionState.Disconnected Then Return
            If Not ConnectionStateRules.CanDisconnect(current) Then
                Throw New StorageBackendException(BackendErrorCodes.BackendBusy, "Cannot disconnect while the WSL backend is '" + current.ToString() + "'.")
            End If

            SetState(ConnectionState.Disconnecting)
            Dim response As HelperResponse
            Try
                ' PFS unmounts (10 s each) + sync + NBD unmount confirmation (10 s)
                Dim mountCount As Integer = Math.Max(1, ActiveMounts.Count)
                Dim helperTimeout As TimeSpan = TimeSpan.FromTicks(OperationTimeouts.UnmountConfirmation.Ticks * (mountCount + 2))
                response = Await _wsl.RunHelperAsync(_distro, "disconnect", New JObject(), OperationTimeouts.WithLaunchOverhead(helperTimeout), CancellationToken.None).ConfigureAwait(False)
            Catch ex As StorageBackendException
                SetState(ConnectionState.Faulted)
                Throw New StorageBackendException(BackendErrorCodes.DisconnectFailed, "Disconnecting from the PSX failed: " + ex.Message, ex.ToolOutput, ex)
            End Try

            If Not response.Ok Then
                If response.Code = BackendErrorCodes.BackendBusy Then
                    SetState(If(current = ConnectionState.Faulted, ConnectionState.Faulted, ConnectionState.Connected))
                Else
                    SetState(ConnectionState.Faulted)
                End If
                Throw New StorageBackendException(If(response.Code = BackendErrorCodes.BackendBusy, response.Code, BackendErrorCodes.DisconnectFailed),
                                                  If(String.IsNullOrEmpty(response.Message), "Disconnecting from the PSX failed.", response.Message), response.Stderr)
            End If

            ClearSession()
            SetState(ConnectionState.Disconnected)
        Finally
            _operationLock.Release()
        End Try
    End Function

    ''' <summary>
    ''' Forced recovery for stale or broken mounts (crash, "wsl --shutdown", network loss). This is never the normal
    ''' disconnect: it lazily unmounts PFS and NBD mounts and only then stops a hung nbdfuse with SIGTERM.
    ''' </summary>
    Public Async Function RecoverAsync(cancellationToken As CancellationToken) As Task
        If Not Await _operationLock.WaitAsync(0).ConfigureAwait(False) Then
            Throw New StorageBackendException(BackendErrorCodes.BackendBusy, "A PSX HDD operation is still running. Recover after it has finished.")
        End If
        Try
            Dim current As ConnectionState = State
            If current = ConnectionState.Connected OrElse current = ConnectionState.Faulted Then SetState(ConnectionState.Disconnecting)
            Dim response As HelperResponse = Await _wsl.RunHelperAsync(_distro, "recover", New JObject(),
                                                                     OperationTimeouts.WithLaunchOverhead(TimeSpan.FromSeconds(60)), CancellationToken.None).ConfigureAwait(False)
            If Not response.Ok Then
                If State = ConnectionState.Disconnecting Then SetState(ConnectionState.Faulted)
                Throw New StorageBackendException(If(String.IsNullOrEmpty(response.Code), BackendErrorCodes.StaleMountState, response.Code),
                                                  If(String.IsNullOrEmpty(response.Message), "Recovery did not finish.", response.Message), response.Stderr)
            End If
            ClearSession()
            If State <> ConnectionState.Disconnected Then SetState(ConnectionState.Disconnected)
        Finally
            _operationLock.Release()
        End Try
    End Function

    Private Sub ClearSession()
        Dim removed As List(Of PfsMountHandle)
        SyncLock _stateLock
            removed = _mounts.Values.ToList()
            _mounts.Clear()
            _mountedDrive = New MountedPSXDrive()
        End SyncLock
        StopKeepAlive()
        For Each handle As PfsMountHandle In removed
            RaiseEvent MountRemoved(Me, handle)
        Next
    End Sub

    ''' <summary>Called when the application closes while idle and connected.</summary>
    Public Sub ReleaseKeepAlive()
        StopKeepAlive()
    End Sub

#End Region

#Region "Serialized HDD operations"

    ''' <summary>
    ''' Every raw-HDD operation runs through here: one at a time (SemaphoreSlim(1,1)), only from Connected, with the
    ''' state showing BusyRead/BusyWrite while it runs.
    ''' </summary>
    Private Async Function RunExclusiveAsync(Of T)(isWrite As Boolean, operation As Func(Of Task(Of T)), cancellationToken As CancellationToken) As Task(Of T)
        Await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(False)
        Try
            Dim current As ConnectionState = State
            If Not ConnectionStateRules.CanStartOperation(current) Then
                If current = ConnectionState.Faulted Then
                    Throw New StorageBackendException(BackendErrorCodes.StaleMountState, "The WSL connection to the PSX is in a faulted state. Disconnect or recover it before continuing.")
                End If
                Throw New StorageBackendException(BackendErrorCodes.NotConnected, "The PSX is not connected through WSL2.")
            End If
            SetState(If(isWrite, ConnectionState.BusyWrite, ConnectionState.BusyRead))
            Dim faulted As Boolean = False
            Try
                Return Await operation().ConfigureAwait(False)
            Catch ex As StorageBackendException When ex.Code = BackendErrorCodes.StaleMountState OrElse ex.Code = BackendErrorCodes.NbdMountNotReady
                faulted = True
                Throw
            Finally
                If faulted Then
                    SetState(ConnectionState.Faulted)
                ElseIf State = ConnectionState.BusyRead OrElse State = ConnectionState.BusyWrite Then
                    SetState(ConnectionState.Connected)
                End If
            End Try
        Finally
            _operationLock.Release()
        End Try
    End Function

    ''' <summary>Starts the helper's "cancel" verb when the token fires; the running wsl.exe is left to finish.</summary>
    Private Function RegisterHelperCancel(cancellationToken As CancellationToken) As CancellationTokenRegistration
        Return cancellationToken.Register(
            Sub()
                Task.Run(Async Function()
                             Try
                                 Await _wsl.RunHelperAsync(_distro, "cancel", New JObject(), OperationTimeouts.WithLaunchOverhead(OperationTimeouts.HelperStatus), CancellationToken.None).ConfigureAwait(False)
                             Catch ex As Exception
                                 BackendLog.Note(Kind, _distro, "cancel request failed: " + ex.Message)
                             End Try
                         End Function)
            End Sub)
    End Function

    Private Function MountedPartitionNames() As HashSet(Of String)
        SyncLock _stateLock
            Return New HashSet(Of String)(_mounts.Values.Select(Function(m) m.PartitionName), StringComparer.Ordinal)
        End SyncLock
    End Function

    Public Function RunHdlDumpAsync(arguments As IList(Of String), windowsWorkingDirectory As String, cancellationToken As CancellationToken,
                                    Optional progress As IProgress(Of String) = Nothing) As Task(Of ProcessResult) Implements IPSXStorageBackend.RunHdlDumpAsync
        If arguments Is Nothing OrElse arguments.Count = 0 Then Throw New ArgumentException("hdl_dump needs a command.", NameOf(arguments))
        Dim verb As String = arguments(0)
        Dim isWrite As Boolean = Not HdlDumpCommands.IsReadOnly(verb)

        Return RunExclusiveAsync(isWrite,
            Async Function()
                ' The partition being changed must not be open through pfsfuse at the same time.
                If isWrite AndAlso arguments.Count > 2 AndAlso MountedPartitionNames().Contains(arguments(2)) Then
                    Throw New StorageBackendException(BackendErrorCodes.BackendBusy,
                                                      "The partition '" + arguments(2) + "' is mounted. Close it before changing it.")
                End If
                Dim linuxCwd As String = Await TranslateWorkingDirectoryAsync(windowsWorkingDirectory, cancellationToken).ConfigureAwait(False)
                Dim timeout As TimeSpan = OperationTimeouts.ForHdlDump(verb)
                Dim request As New JObject From {
                    {"args", New JArray(arguments.ToArray())},
                    {"cwd", linuxCwd},
                    {"timeout", OperationTimeouts.ToHelperSeconds(timeout)}
                }
                Dim cancellable As Boolean = HdlDumpCommands.IsCancellable(verb)
                Using If(cancellable, RegisterHelperCancel(cancellationToken), Nothing)
                    Dim response As HelperResponse = Await _wsl.RunHelperAsync(_distro, "hdl", request, OperationTimeouts.WithLaunchOverhead(timeout),
                                                                             If(cancellable, CancellationToken.None, cancellationToken),
                                                                             If(progress Is Nothing, Nothing, New Action(Of String)(AddressOf progress.Report)),
                                                                             cancelViaHelper:=cancellable).ConfigureAwait(False)
                    Return ToolResult(response, BackendErrorCodes.HdlDumpFailed)
                End Using
            End Function, cancellationToken)
    End Function

    Public Function RunPfsShellAsync(commands As IList(Of String), windowsWorkingDirectory As String, cancellationToken As CancellationToken) As Task(Of ProcessResult) Implements IPSXStorageBackend.RunPfsShellAsync
        If commands Is Nothing OrElse commands.Count = 0 Then Throw New ArgumentException("pfsshell needs commands.", NameOf(commands))
        If PfsShellCommands.ContainsLineBreak(commands) Then
            Throw New StorageBackendException(BackendErrorCodes.InvalidRequest, "A pfsshell command contains a line break.")
        End If
        Dim isWrite As Boolean = Not PfsShellCommands.IsReadOnly(commands)

        Return RunExclusiveAsync(isWrite,
            Async Function()
                If isWrite Then
                    Dim mounted As HashSet(Of String) = MountedPartitionNames()
                    For Each command As String In commands
                        Dim verb As String = PfsShellCommands.VerbOf(command)
                        If (verb = "mount" OrElse verb = "rmpart") AndAlso mounted.Contains(command.Trim().Substring(verb.Length).Trim()) Then
                            Throw New StorageBackendException(BackendErrorCodes.BackendBusy,
                                                              "The partition '" + command.Trim().Substring(verb.Length).Trim() + "' is mounted. Close it before changing it.")
                        End If
                    Next
                End If
                Dim linuxCwd As String = Await TranslateWorkingDirectoryAsync(windowsWorkingDirectory, cancellationToken).ConfigureAwait(False)
                Dim timeout As TimeSpan = OperationTimeouts.ForPfsShell(commands)
                Dim request As New JObject From {
                    {"commands", New JArray(commands.ToArray())},
                    {"cwd", linuxCwd},
                    {"timeout", OperationTimeouts.ToHelperSeconds(timeout)}
                }
                ' A pfsshell write session is not interrupted half way; cancellation only applies before it starts.
                Dim response As HelperResponse = Await _wsl.RunHelperAsync(_distro, "pfsshell", request, OperationTimeouts.WithLaunchOverhead(timeout),
                                                                         If(isWrite, CancellationToken.None, cancellationToken)).ConfigureAwait(False)
                Return ToolResult(response, BackendErrorCodes.PfsShellFailed)
            End Function, cancellationToken)
    End Function

    ''' <summary>
    ''' A tool that ran returns its exit code and output so callers keep their existing output checks; failures before
    ''' the tool could run (validation, busy, stale mount) are thrown.
    ''' </summary>
    Private Shared Function ToolResult(response As HelperResponse, toolFailureCode As String) As ProcessResult
        If response.Ok OrElse response.Code = toolFailureCode OrElse response.Code = BackendErrorCodes.OperationTimeout OrElse
           response.Code = BackendErrorCodes.OperationCancelled Then
            Return response.ToProcessResult()
        End If
        Throw New StorageBackendException(response.Code, If(String.IsNullOrEmpty(response.Message), "The WSL helper refused the request.", response.Message), response.Stderr)
    End Function

    Private Async Function TranslateWorkingDirectoryAsync(windowsWorkingDirectory As String, cancellationToken As CancellationToken) As Task(Of String)
        If String.IsNullOrEmpty(windowsWorkingDirectory) Then Return ""
        Return Await TranslatePathAsync(windowsWorkingDirectory, True, cancellationToken).ConfigureAwait(False)
    End Function

    Public Async Function MountPfsPartitionAsync(partitionName As String, displayName As String, cancellationToken As CancellationToken) As Task(Of PfsMountHandle) Implements IPSXStorageBackend.MountPfsPartitionAsync
        If Not StorageValidation.IsValidPartitionName(partitionName) Then
            Throw New StorageBackendException(BackendErrorCodes.InvalidPartitionName, "'" + partitionName + "' is not a valid PS2 partition name.")
        End If

        ' Mounting is a transition owned by the backend, so it takes the exclusive lock like a write.
        Dim handle As PfsMountHandle = Await RunExclusiveAsync(True,
            Async Function()
                SyncLock _stateLock
                    Dim existing As PfsMountHandle = _mounts.Values.FirstOrDefault(Function(m) m.PartitionName = partitionName)
                    If existing IsNot Nothing Then Return existing
                End SyncLock

                Dim request As New JObject From {{"partition", partitionName}, {"display_name", If(displayName, "")}}
                Dim response As HelperResponse = Await _wsl.RunHelperAsync(_distro, "mount-pfs", request,
                                                                         OperationTimeouts.WithLaunchOverhead(OperationTimeouts.NbdfuseReadiness + OperationTimeouts.UnmountConfirmation),
                                                                         CancellationToken.None).ConfigureAwait(False)
                If Not response.Ok Then
                    Throw New StorageBackendException(If(String.IsNullOrEmpty(response.Code), BackendErrorCodes.PfsMountFailed, response.Code),
                                                      If(String.IsNullOrEmpty(response.Message), "Mounting '" + partitionName + "' failed.", response.Message), response.Stderr)
                End If

                Dim mounted As List(Of PfsMountHandle) = ReadMounts(New JArray(response.Data))
                If mounted.Count <> 1 Then
                    Throw New StorageBackendException(BackendErrorCodes.HelperProtocolError, "The WSL helper returned an invalid mount description.")
                End If
                Dim newHandle As PfsMountHandle = mounted(0)
                If String.IsNullOrEmpty(newHandle.DisplayName) Then newHandle.DisplayName = If(displayName, partitionName)

                ' Windows must actually see the mount before any window uses it.
                Dim visiblePath As String = Await WaitForUncVisibilityAsync(newHandle.NativePath, cancellationToken).ConfigureAwait(False)
                If visiblePath Is Nothing Then
                    Await UnmountThroughHelperAsync(newHandle).ConfigureAwait(False)
                    Throw New StorageBackendException(BackendErrorCodes.WindowsUncNotVisible,
                                                      "'" + partitionName + "' was mounted in WSL at " + newHandle.NativePath + " but Windows could not open " + newHandle.WindowsPath + " within " +
                                                      OperationTimeouts.MountVisibility.TotalSeconds.ToString() + " seconds. The partition was unmounted again.")
                End If
                newHandle.WindowsPath = visiblePath

                SyncLock _stateLock
                    _mounts(newHandle.MountId) = newHandle
                End SyncLock
                Return newHandle
            End Function, cancellationToken).ConfigureAwait(False)
        Return handle
    End Function

    ''' <summary>
    ''' Polls Directory.Exists for up to 5 s every 100 ms. \\wsl.localhost is tried first; \\wsl$ is the name older
    ''' Windows 10 builds use for the same share.
    ''' </summary>
    Private Async Function WaitForUncVisibilityAsync(linuxPath As String, cancellationToken As CancellationToken) As Task(Of String)
        Dim candidates As String() = {
            StorageValidation.LinuxPathToWslUnc(_distro, linuxPath, "wsl.localhost"),
            StorageValidation.LinuxPathToWslUnc(_distro, linuxPath, "wsl$")
        }
        Dim deadline As DateTime = DateTime.UtcNow + OperationTimeouts.MountVisibility
        Do
            For Each candidate As String In candidates
                If Await Task.Run(Function() Directory.Exists(candidate)).ConfigureAwait(False) Then Return candidate
            Next
            If DateTime.UtcNow >= deadline OrElse cancellationToken.IsCancellationRequested Then Return Nothing
            Await Task.Delay(OperationTimeouts.MountVisibilityPoll).ConfigureAwait(False)
        Loop
    End Function

    Public Function UnmountPfsPartitionAsync(mount As PfsMountHandle, cancellationToken As CancellationToken) As Task Implements IPSXStorageBackend.UnmountPfsPartitionAsync
        If mount Is Nothing Then Return Task.CompletedTask
        If Not StorageValidation.IsSafeMountId(mount.MountId) Then
            Throw New StorageBackendException(BackendErrorCodes.InvalidRequest, "Invalid mount id.")
        End If
        Return RunExclusiveAsync(True,
            Async Function()
                Await UnmountThroughHelperAsync(mount).ConfigureAwait(False)
                Dim removed As PfsMountHandle = Nothing
                SyncLock _stateLock
                    If _mounts.TryGetValue(mount.MountId, removed) Then _mounts.Remove(mount.MountId)
                End SyncLock
                RaiseEvent MountRemoved(Me, If(removed, mount))
                Return True
            End Function, cancellationToken)
    End Function

    Private Async Function UnmountThroughHelperAsync(mount As PfsMountHandle) As Task
        Dim response As HelperResponse = Await _wsl.RunHelperAsync(_distro, "unmount-pfs", New JObject From {{"mount_id", mount.MountId}},
                                                                 OperationTimeouts.WithLaunchOverhead(OperationTimeouts.UnmountConfirmation),
                                                                 CancellationToken.None).ConfigureAwait(False)
        If Not response.Ok Then
            Throw New StorageBackendException(If(String.IsNullOrEmpty(response.Code), BackendErrorCodes.PfsUnmountFailed, response.Code),
                                              If(String.IsNullOrEmpty(response.Message), "Unmounting '" + mount.PartitionName + "' failed.", response.Message), response.Stderr)
        End If
    End Function

    Public Function ConvertWindowsPathAsync(windowsPath As String, cancellationToken As CancellationToken, Optional mustExist As Boolean = False) As Task(Of String) Implements IPSXStorageBackend.ConvertWindowsPathAsync
        Return TranslatePathAsync(windowsPath, mustExist, cancellationToken)
    End Function

    ''' <summary>wslpath -a -u inside the distro; never a hand-made /mnt/x mapping.</summary>
    Private Async Function TranslatePathAsync(windowsPath As String, mustExist As Boolean, cancellationToken As CancellationToken) As Task(Of String)
        If String.IsNullOrWhiteSpace(windowsPath) Then
            Throw New StorageBackendException(BackendErrorCodes.PathTranslationFailed, "No Windows path was given.")
        End If
        Dim request As New JObject From {{"windows_path", windowsPath}, {"must_exist", mustExist}}
        Dim response As HelperResponse = Await _wsl.RunHelperAsync(_distro, "translate-path", request,
                                                                 OperationTimeouts.WithLaunchOverhead(OperationTimeouts.HelperStatus), cancellationToken).ConfigureAwait(False)
        If Not response.Ok Then
            Throw New StorageBackendException(If(String.IsNullOrEmpty(response.Code), BackendErrorCodes.PathTranslationFailed, response.Code),
                                              If(String.IsNullOrEmpty(response.Message), "'" + windowsPath + "' could not be translated to a WSL path.", response.Message), response.Stderr)
        End If
        Return response.GetString("linux_path")
    End Function

    Public Function GetRawDeviceSizeAsync(cancellationToken As CancellationToken) As Task(Of Long) Implements IPSXStorageBackend.GetRawDeviceSizeAsync
        Return RunExclusiveAsync(False,
            Async Function()
                Dim response As HelperResponse = Await _wsl.RunHelperAsync(_distro, "raw-size", New JObject(),
                                                                         OperationTimeouts.WithLaunchOverhead(OperationTimeouts.HelperStatus), cancellationToken).ConfigureAwait(False)
                If Not response.Ok Then
                    Throw New StorageBackendException(response.Code, If(String.IsNullOrEmpty(response.Message), "The NBD raw device size could not be read.", response.Message), response.Stderr)
                End If
                Return response.GetLong("size")
            End Function, cancellationToken)
    End Function

    Public Function BackupRawDeviceAsync(windowsDestinationFile As String, blockSize As String, progress As IProgress(Of String), cancellationToken As CancellationToken) As Task(Of ProcessResult) Implements IPSXStorageBackend.BackupRawDeviceAsync
        RequireBlockSize(blockSize)
        Return RunExclusiveAsync(False,
            Async Function()
                ' The destination does not exist yet; the helper checks its folder.
                Dim linuxDestination As String = Await TranslatePathAsync(windowsDestinationFile, False, cancellationToken).ConfigureAwait(False)
                Dim request As New JObject From {{"destination", linuxDestination}, {"block_size", blockSize}}
                Using RegisterHelperCancel(cancellationToken)
                    Dim response As HelperResponse = Await _wsl.RunHelperAsync(_distro, "backup", request, OperationTimeouts.Unlimited, CancellationToken.None,
                                                                             If(progress Is Nothing, Nothing, New Action(Of String)(AddressOf progress.Report)),
                                                                             cancelViaHelper:=True).ConfigureAwait(False)
                    Return ToolResult(response, BackendErrorCodes.BackupFailed)
                End Using
            End Function, cancellationToken)
    End Function

    Public Function RestoreRawDeviceAsync(windowsSourceFile As String, blockSize As String, progress As IProgress(Of String), cancellationToken As CancellationToken) As Task(Of ProcessResult) Implements IPSXStorageBackend.RestoreRawDeviceAsync
        RequireBlockSize(blockSize)
        Return RunExclusiveAsync(True,
            Async Function()
                If ActiveMounts.Count > 0 Then
                    Throw New StorageBackendException(BackendErrorCodes.BackendBusy, "Close all mounted PSX partitions before restoring the HDD.")
                End If
                Dim linuxSource As String = Await TranslatePathAsync(windowsSourceFile, True, cancellationToken).ConfigureAwait(False)
                cancellationToken.ThrowIfCancellationRequested()
                ' The UI has shown the destructive confirmation; once dd starts it is not interrupted.
                Dim request As New JObject From {{"source", linuxSource}, {"block_size", blockSize}, {"confirmed", True}}
                Dim response As HelperResponse = Await _wsl.RunHelperAsync(_distro, "restore", request, OperationTimeouts.Unlimited, CancellationToken.None,
                                                                         If(progress Is Nothing, Nothing, New Action(Of String)(AddressOf progress.Report))).ConfigureAwait(False)
                If Not response.Ok AndAlso response.Code <> BackendErrorCodes.RestoreFailed Then
                    Throw New StorageBackendException(response.Code, If(String.IsNullOrEmpty(response.Message), "The restore was refused.", response.Message), response.Stderr)
                End If
                Return response.ToProcessResult()
            End Function, cancellationToken)
    End Function

    Private Shared Sub RequireBlockSize(blockSize As String)
        If blockSize <> "1M" AndAlso blockSize <> "4M" Then
            Throw New StorageBackendException(BackendErrorCodes.InvalidRequest, "Block size must be 1M or 4M.")
        End If
    End Sub

#End Region

#Region "Setup"

    ''' <summary>Runs the embedded bootstrap as WSL root. Only ever called after the user clicked Install / Repair.</summary>
    Public Async Function InstallOrRepairAsync(fullSetup As Boolean, progress As Action(Of String), cancellationToken As CancellationToken) As Task(Of ProcessResult)
        Dim current As ConnectionState = State
        If ConnectionStateRules.IsHddAvailable(current) OrElse current = ConnectionState.Connecting OrElse current = ConnectionState.Disconnecting Then
            Throw New StorageBackendException(BackendErrorCodes.BackendBusy, "Disconnect from the PSX before installing or repairing the WSL backend.")
        End If
        Dim result As ProcessResult = Await _wsl.RunBootstrapAsync(_distro, If(fullSetup, "full", "helper-only"), ExpectedHelperProtocolVersion, progress, cancellationToken).ConfigureAwait(False)
        _helperReady = False
        Return result
    End Function

#End Region

End Class
