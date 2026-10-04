Imports System.IO
Imports System.Text
Imports System.Threading
Imports PSX_XMB_Manager.Structs

''' <summary>
''' A PS2/PSX HDD physically attached to this PC. Wraps the existing Windows tools (hdl_dump.exe, pfsshell.exe,
''' pfsfuse.exe + Dokan) unchanged so that no window launches raw-storage tools itself.
''' Never used for network PSX connections.
''' </summary>
Public Class LocalWindowsBackend
    Implements IPSXStorageBackend

    Private ReadOnly _runner As IProcessRunner
    Private ReadOnly _operationLock As New SemaphoreSlim(1, 1)
    Private ReadOnly _stateLock As New Object()
    Private ReadOnly _mounts As New Dictionary(Of String, PfsMountHandle)(StringComparer.Ordinal)
    Private _state As ConnectionState = ConnectionState.Disconnected
    Private _mountedDrive As MountedPSXDrive

    Public Event StateChanged As EventHandler Implements IPSXStorageBackend.StateChanged
    Public Event MountRemoved As EventHandler(Of PfsMountHandle) Implements IPSXStorageBackend.MountRemoved

    Public Sub New(Optional runner As IProcessRunner = Nothing)
        _runner = If(runner, New SystemProcessRunner())
    End Sub

    Public ReadOnly Property ToolsDirectory As String
        Get
            Return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools")
        End Get
    End Property

    Public ReadOnly Property HdlDumpPath As String
        Get
            Return Path.Combine(ToolsDirectory, "hdl_dump.exe")
        End Get
    End Property

    Public ReadOnly Property PfsShellPath As String
        Get
            Return Path.Combine(ToolsDirectory, "pfsshell.exe")
        End Get
    End Property

    Public ReadOnly Property PfsFusePath As String
        Get
            Return Path.Combine(ToolsDirectory, "pfsfuse.exe")
        End Get
    End Property

    ''' <summary>dokanctl.exe of an installed Dokan Library, or "" (Dokan is optional and only used here).</summary>
    Public Shared Function FindDokanCtl() As String
        Dim dokanRoot As String = Path.Combine(My.Computer.FileSystem.SpecialDirectories.ProgramFiles, "Dokan")
        If Not Directory.Exists(dokanRoot) Then Return ""
        For Each folder In Directory.GetDirectories(dokanRoot)
            Dim folderInfo As New DirectoryInfo(folder)
            If folderInfo.Name.Contains("DokanLibrary") Or folderInfo.Name.Contains("Dokan Library") Then
                Dim dokanCtl As String = Path.Combine(folder, "dokanctl.exe")
                Return If(File.Exists(dokanCtl), dokanCtl, "")
            End If
        Next
        Return ""
    End Function

    ''' <summary>"Library: x - Driver: y" from dokanctl /v, or "" when Dokan is not installed.</summary>
    Public Shared Function ReadDokanVersion() As String
        Dim dokanCtl As String = FindDokanCtl()
        If String.IsNullOrEmpty(dokanCtl) Then Return ""
        Try
            Using process As New Process()
                process.StartInfo.FileName = dokanCtl
                process.StartInfo.Arguments = "/v"
                process.StartInfo.RedirectStandardOutput = True
                process.StartInfo.UseShellExecute = False
                process.StartInfo.CreateNoWindow = True
                process.Start()
                Dim lines As String() = process.StandardOutput.ReadToEnd().Split({vbCrLf}, StringSplitOptions.None)
                If Not process.WaitForExit(5000) Then process.Kill()
                If lines.Length > 3 AndAlso lines(2).Contains(":") AndAlso lines(3).Contains(":") Then
                    Return "Library: " + lines(2).Trim().Split(":"c)(1).Trim() + " - Driver: " + lines(3).Trim().Split(":"c)(1).Trim()
                End If
            End Using
        Catch ex As ComponentModel.Win32Exception
        Catch ex As InvalidOperationException
        End Try
        Return ""
    End Function

    ''' <summary>True when the last connect found a drive but WMIC is missing to resolve its device path.</summary>
    Public Property WmicInstallRequired As Boolean

    Public ReadOnly Property Kind As StorageBackendKind Implements IPSXStorageBackend.Kind
        Get
            Return StorageBackendKind.LocalWindows
        End Get
    End Property

    Public ReadOnly Property DisplayName As String Implements IPSXStorageBackend.DisplayName
        Get
            Return "Local HDD"
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
                Throw New InvalidOperationException("Invalid backend state transition " + _state.ToString() + " -> " + newState.ToString())
            End If
            changed = _state <> newState
            _state = newState
        End SyncLock
        If changed Then RaiseEvent StateChanged(Me, EventArgs.Empty)
    End Sub

    Public Function ProbeAsync(cancellationToken As CancellationToken) As Task(Of BackendProbeResult) Implements IPSXStorageBackend.ProbeAsync
        Dim result As New BackendProbeResult With {
            .BackendKind = Kind,
            .HdlDumpPresent = File.Exists(HdlDumpPath),
            .PfsShellPresent = File.Exists(PfsShellPath),
            .PfsFusePresent = File.Exists(PfsFusePath)
        }
        If Not result.HdlDumpPresent Then result.MissingDependencies.Add(HdlDumpPath)
        If Not result.PfsShellPresent Then result.MissingDependencies.Add(PfsShellPath)
        result.IsReady = result.HdlDumpPresent AndAlso result.PfsShellPresent
        If Not result.IsReady Then
            result.SetupErrorCode = BackendErrorCodes.LocalToolMissing
            result.SetupMessage = "Missing: " + String.Join(", ", result.MissingDependencies)
            If State = ConnectionState.Disconnected Then SetState(ConnectionState.Unavailable)
        ElseIf State = ConnectionState.Unavailable Then
            SetState(ConnectionState.Disconnected)
        End If
        Return Task.FromResult(result)
    End Function

    ''' <summary>
    ''' Local "connect" is the existing local-HDD detection: hdl_dump query for a formatted PS2 HDD plus the device path
    ''' lookup. <paramref name="ipAddress"/> and <paramref name="port"/> are not used.
    ''' </summary>
    Public Async Function ConnectAsync(ipAddress As String, port As Integer, cancellationToken As CancellationToken) As Task(Of MountedPSXDrive) Implements IPSXStorageBackend.ConnectAsync
        Await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(False)
        Try
            If ConnectionStateRules.IsHddAvailable(State) Then Return MountedDrive
            If Not File.Exists(HdlDumpPath) Then
                Throw New StorageBackendException(BackendErrorCodes.LocalToolMissing, "hdl_dump.exe was not found at " + HdlDumpPath + ".")
            End If
            If Not ConnectionStateRules.CanConnect(State) Then
                Throw New StorageBackendException(BackendErrorCodes.BackendBusy, "Cannot detect the local HDD while the backend is '" + State.ToString() + "'.")
            End If
            SetState(ConnectionState.Connecting)
            Try
                Dim hdlDriveName As String = Await Task.Run(Function() Utils.IsLocalHDDConnected()).ConfigureAwait(False)
                If String.IsNullOrEmpty(hdlDriveName) Then
                    Throw New StorageBackendException(BackendErrorCodes.LocalHddNotFound, "No local PS2/PSX formatted HDD was found by hdl_dump.")
                End If
                Dim devicePath As String = Await Task.Run(Function() Utils.GetLocalHDDDevicePath()).ConfigureAwait(False)
                WmicInstallRequired = (devicePath = "WMIC_INSTALL_REQUIRED")

                Dim drive As New MountedPSXDrive With {
                    .BackendKind = StorageBackendKind.LocalWindows,
                    .HDLDriveName = hdlDriveName,
                    .DriveID = devicePath,
                    .NativeDevicePath = devicePath,
                    .WindowsAccessibleDevicePath = devicePath,
                    .ConnectedOnIP = "",
                    .NBDDriveName = "",
                    .IsReadOnly = False
                }
                SyncLock _stateLock
                    _mountedDrive = drive
                End SyncLock
                SetState(ConnectionState.Connected)
                Return drive
            Catch
                If State = ConnectionState.Connecting Then SetState(ConnectionState.Disconnected)
                Throw
            End Try
        Finally
            _operationLock.Release()
        End Try
    End Function

    Public Async Function DisconnectAsync(cancellationToken As CancellationToken) As Task Implements IPSXStorageBackend.DisconnectAsync
        If Not Await _operationLock.WaitAsync(0).ConfigureAwait(False) Then
            Throw New StorageBackendException(BackendErrorCodes.BackendBusy, "A PSX HDD operation is still running. Try again after it has finished.")
        End If
        Try
            If State = ConnectionState.Disconnected OrElse State = ConnectionState.Unavailable Then Return
            SetState(ConnectionState.Disconnecting)
            For Each mount As PfsMountHandle In ActiveMounts
                Await UnmountCoreAsync(mount).ConfigureAwait(False)
            Next
            SyncLock _stateLock
                _mountedDrive = New MountedPSXDrive()
            End SyncLock
            SetState(ConnectionState.Disconnected)
        Finally
            _operationLock.Release()
        End Try
    End Function

    Private Async Function RunExclusiveAsync(Of T)(isWrite As Boolean, operation As Func(Of Task(Of T)), cancellationToken As CancellationToken) As Task(Of T)
        Await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(False)
        Try
            If Not ConnectionStateRules.CanStartOperation(State) Then
                Throw New StorageBackendException(BackendErrorCodes.NotConnected, "No local PS2/PSX HDD is connected.")
            End If
            SetState(If(isWrite, ConnectionState.BusyWrite, ConnectionState.BusyRead))
            Try
                Return Await operation().ConfigureAwait(False)
            Finally
                SetState(ConnectionState.Connected)
            End Try
        Finally
            _operationLock.Release()
        End Try
    End Function

    Private Async Function RunToolAsync(fileName As String, arguments As IList(Of String), workingDirectory As String, standardInput As Byte(),
                                        timeout As TimeSpan, progress As IProgress(Of String), cancellationToken As CancellationToken) As Task(Of ProcessResult)
        If Not File.Exists(fileName) Then
            Throw New StorageBackendException(BackendErrorCodes.LocalToolMissing, Path.GetFileName(fileName) + " was not found at " + fileName + ".")
        End If
        Dim request As New ProcessRequest With {
            .FileName = fileName,
            .Arguments = arguments,
            .WorkingDirectory = If(String.IsNullOrEmpty(workingDirectory), AppDomain.CurrentDomain.BaseDirectory, workingDirectory),
            .StandardInput = standardInput,
            .Timeout = timeout,
            .OutputEncoding = Console.OutputEncoding,
            .KillOnCancel = False,
            .LogDescription = Path.GetFileName(fileName) + " " + CommandLineQuoting.Join(arguments)
        }
        If progress IsNot Nothing Then request.StandardOutputLineCallback = AddressOf progress.Report
        cancellationToken.ThrowIfCancellationRequested()

        Dim stopwatch As Stopwatch = Stopwatch.StartNew()
        Dim result As ProcessResult = Await _runner.RunAsync(request, CancellationToken.None).ConfigureAwait(False)
        result.StandardOutput = OutputText.NormalizeLineEndings(result.StandardOutput)
        result.StandardError = OutputText.NormalizeLineEndings(result.StandardError)
        BackendLog.Write(Kind, "", Path.GetFileNameWithoutExtension(fileName), request.LogDescription, result.ExitCode, stopwatch.Elapsed, result.ErrorCode, result.StandardOutput, result.StandardError)
        Return result
    End Function

    Public Function RunHdlDumpAsync(arguments As IList(Of String), windowsWorkingDirectory As String, cancellationToken As CancellationToken,
                                    Optional progress As IProgress(Of String) = Nothing) As Task(Of ProcessResult) Implements IPSXStorageBackend.RunHdlDumpAsync
        If arguments Is Nothing OrElse arguments.Count = 0 Then Throw New ArgumentException("hdl_dump needs a command.", NameOf(arguments))
        Dim verb As String = arguments(0)
        Return RunExclusiveAsync(Of ProcessResult)(Not HdlDumpCommands.IsReadOnly(verb),
            Function() RunToolAsync(HdlDumpPath, arguments, windowsWorkingDirectory, Nothing, OperationTimeouts.ForHdlDump(verb), progress, cancellationToken),
            cancellationToken)
    End Function

    Public Function RunPfsShellAsync(commands As IList(Of String), windowsWorkingDirectory As String, cancellationToken As CancellationToken) As Task(Of ProcessResult) Implements IPSXStorageBackend.RunPfsShellAsync
        If commands Is Nothing OrElse commands.Count = 0 Then Throw New ArgumentException("pfsshell needs commands.", NameOf(commands))
        If PfsShellCommands.ContainsLineBreak(commands) Then
            Throw New StorageBackendException(BackendErrorCodes.InvalidRequest, "A pfsshell command contains a line break.")
        End If
        ' Same bytes the old Tools\cmdlist\*.txt files fed to pfsshell.exe, without the shared file and cmd.exe.
        Dim script As String = String.Join(vbCrLf, commands) + vbCrLf
        Dim input As Byte() = New UTF8Encoding(False).GetBytes(script)
        Return RunExclusiveAsync(Of ProcessResult)(Not PfsShellCommands.IsReadOnly(commands),
            Function() RunToolAsync(PfsShellPath, New List(Of String), windowsWorkingDirectory, input, OperationTimeouts.ForPfsShell(commands), Nothing, cancellationToken),
            cancellationToken)
    End Function

    ''' <summary>pfsfuse.exe on the next free drive letter through Dokan, as before.</summary>
    Public Function MountPfsPartitionAsync(partitionName As String, displayName As String, cancellationToken As CancellationToken) As Task(Of PfsMountHandle) Implements IPSXStorageBackend.MountPfsPartitionAsync
        Return RunExclusiveAsync(Of PfsMountHandle)(True,
            Async Function()
                If Not File.Exists(PfsFusePath) Then
                    Throw New StorageBackendException(BackendErrorCodes.LocalToolMissing, "pfsfuse.exe was not found at " + PfsFusePath + ".")
                End If
                Dim driveLetter As String = Utils.FindNextAvailableDriveLetter()
                Dim volumeName As String = If(String.IsNullOrEmpty(displayName), partitionName, displayName)
                Dim drive As MountedPSXDrive = MountedDrive

                ' pfsfuse.exe keeps running while the Dokan volume is mounted, so it is started and not awaited.
                Using pfsFuse As New Process()
                    pfsFuse.StartInfo.FileName = PfsFusePath
                    pfsFuse.StartInfo.Arguments = CommandLineQuoting.Join({"--partition=" + partitionName, drive.DriveID, driveLetter, "-o", "volname=" + volumeName})
                    pfsFuse.StartInfo.UseShellExecute = False
                    pfsFuse.StartInfo.CreateNoWindow = True
                    pfsFuse.Start()
                End Using
                BackendLog.Note(Kind, "", "pfsfuse.exe --partition=" + partitionName + " " + drive.DriveID + " " + driveLetter)

                Dim handle As New PfsMountHandle With {
                    .BackendKind = Kind,
                    .MountId = "local-" + driveLetter.ToUpperInvariant(),
                    .PartitionName = partitionName,
                    .DisplayName = volumeName,
                    .LegacyDriveLetter = driveLetter.ToUpperInvariant(),
                    .NativePath = driveLetter.ToUpperInvariant() + ":\",
                    .WindowsPath = driveLetter.ToUpperInvariant() + ":\"
                }

                ' The Dokan volume shows up after a short delay (the old code waited with a timer).
                Dim deadline As DateTime = DateTime.UtcNow.AddSeconds(10)
                While Not Directory.Exists(handle.WindowsPath) AndAlso DateTime.UtcNow < deadline
                    Await Task.Delay(OperationTimeouts.MountVisibilityPoll).ConfigureAwait(False)
                End While

                SyncLock _stateLock
                    _mounts(handle.MountId) = handle
                End SyncLock
                Return handle
            End Function, cancellationToken)
    End Function

    Public Function UnmountPfsPartitionAsync(mount As PfsMountHandle, cancellationToken As CancellationToken) As Task Implements IPSXStorageBackend.UnmountPfsPartitionAsync
        If mount Is Nothing Then Return Task.CompletedTask
        Return RunExclusiveAsync(Of Boolean)(True,
            Async Function()
                Await UnmountCoreAsync(mount).ConfigureAwait(False)
                Return True
            End Function, cancellationToken)
    End Function

    Private Async Function UnmountCoreAsync(mount As PfsMountHandle) As Task
        Dim dokanCtl As String = FindDokanCtl()
        If String.IsNullOrEmpty(dokanCtl) Then
            Throw New StorageBackendException(BackendErrorCodes.PfsUnmountFailed,
                                              "Could not unmount " + mount.PartitionName + ": dokanctl.exe was not found under " + Path.Combine(My.Computer.FileSystem.SpecialDirectories.ProgramFiles, "Dokan") + ".")
        End If
        Dim request As New ProcessRequest With {
            .FileName = dokanCtl,
            .Arguments = New List(Of String) From {"/u", mount.LegacyDriveLetter},
            .Timeout = OperationTimeouts.UnmountConfirmation,
            .OutputEncoding = Console.OutputEncoding
        }
        Dim result As ProcessResult = Await _runner.RunAsync(request, CancellationToken.None).ConfigureAwait(False)
        BackendLog.Write(Kind, "", "dokanctl", "dokanctl /u " + mount.LegacyDriveLetter, result.ExitCode, TimeSpan.Zero, result.ErrorCode, result.StandardOutput, result.StandardError)
        Dim removed As PfsMountHandle = Nothing
        SyncLock _stateLock
            If _mounts.TryGetValue(mount.MountId, removed) Then _mounts.Remove(mount.MountId)
        End SyncLock
        RaiseEvent MountRemoved(Me, If(removed, mount))
    End Function

    Public Function ConvertWindowsPathAsync(windowsPath As String, cancellationToken As CancellationToken, Optional mustExist As Boolean = False) As Task(Of String) Implements IPSXStorageBackend.ConvertWindowsPathAsync
        If mustExist AndAlso Not File.Exists(windowsPath) AndAlso Not Directory.Exists(windowsPath) Then
            Throw New StorageBackendException(BackendErrorCodes.PathTranslationFailed, "'" + windowsPath + "' does not exist.")
        End If
        Return Task.FromResult(windowsPath)
    End Function

    Public Function GetRawDeviceSizeAsync(cancellationToken As CancellationToken) As Task(Of Long) Implements IPSXStorageBackend.GetRawDeviceSizeAsync
        Throw New StorageBackendException(BackendErrorCodes.InvalidRequest, "Raw device size is only reported by the WSL2 NBD backend; local disks are listed by the Utilities window.")
    End Function

    Public Function BackupRawDeviceAsync(windowsDestinationFile As String, blockSize As String, progress As IProgress(Of String), cancellationToken As CancellationToken) As Task(Of ProcessResult) Implements IPSXStorageBackend.BackupRawDeviceAsync
        Throw New StorageBackendException(BackendErrorCodes.InvalidRequest, "Local disk backups use the existing Windows dd.exe path in the Utilities window.")
    End Function

    Public Function RestoreRawDeviceAsync(windowsSourceFile As String, blockSize As String, progress As IProgress(Of String), cancellationToken As CancellationToken) As Task(Of ProcessResult) Implements IPSXStorageBackend.RestoreRawDeviceAsync
        Throw New StorageBackendException(BackendErrorCodes.InvalidRequest, "Local disk restores use the existing Windows dd.exe path in the Utilities window.")
    End Function

End Class
