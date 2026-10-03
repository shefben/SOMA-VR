Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

' Backend data models and the small pure helpers (parsers, validation, timeouts, error codes)
' shared by both storage backends. Nothing in this file touches WPF so it can be unit tested.

''' <summary>Result of one external tool run (a Windows tool, wsl.exe, or a Linux tool reported by the WSL helper).</summary>
Public Class ProcessResult
    Public Property ExitCode As Integer
    Public Property StandardOutput As String = ""
    Public Property StandardError As String = ""
    Public Property TimedOut As Boolean
    Public Property Cancelled As Boolean

    ''' <summary>Stable backend error code when the run failed (empty on success).</summary>
    Public Property ErrorCode As String = ""

    ''' <summary>Raw bytes as read from the process, kept so wsl.exe's own UTF-16 messages can be decoded.</summary>
    Public Property StandardOutputBytes As Byte() = New Byte() {}
    Public Property StandardErrorBytes As Byte() = New Byte() {}

    Public ReadOnly Property Succeeded As Boolean
        Get
            Return ExitCode = 0 AndAlso Not TimedOut AndAlso Not Cancelled
        End Get
    End Property

    ''' <summary>stdout followed by stderr: the same text the old "cmd /c ... 2>&amp;1" pipelines searched.</summary>
    Public ReadOnly Property CombinedOutput As String
        Get
            If String.IsNullOrEmpty(StandardError) Then Return StandardOutput
            If String.IsNullOrEmpty(StandardOutput) Then Return StandardError
            Return StandardOutput & vbLf & StandardError
        End Get
    End Property
End Class

Public Class WslDistroInfo
    Public Property Name As String = ""
    Public Property State As String = ""
    Public Property Version As Integer
    Public Property IsDefault As Boolean

    Public Overrides Function ToString() As String
        Return Name
    End Function
End Class

Public Class BackendProbeResult
    Public Property BackendKind As StorageBackendKind
    Public Property IsReady As Boolean

    ' WSL and distribution
    Public Property WslInstalled As Boolean
    Public Property WslExePath As String = ""
    Public Property AvailableDistros As New List(Of WslDistroInfo)
    Public Property SelectedDistro As String = ""
    Public Property WslVersion As Integer
    Public Property DistroOsId As String = ""
    Public Property DistroOsVersion As String = ""
    Public Property DistroOsName As String = ""
    Public Property DistroSupportsAutomaticSetup As Boolean
    Public Property LinuxUser As String = ""
    Public Property LinuxHome As String = ""

    ' Linux tools
    Public Property NbdfusePresent As Boolean
    Public Property NbdfuseVersion As String = ""
    Public Property NbdinfoPresent As Boolean
    Public Property NbdinfoVersion As String = ""
    Public Property HdlDumpPresent As Boolean
    Public Property PfsShellPresent As Boolean
    Public Property PfsFusePresent As Boolean
    Public Property Fusermount3Present As Boolean
    Public Property FusermountPresent As Boolean
    Public Property DevFusePresent As Boolean
    Public Property Python3Present As Boolean
    Public Property HelperInstalled As Boolean
    Public Property HelperVersion As Integer
    Public Property MissingDependencies As New List(Of String)

    ' Existing NBD session reported by the helper (never modified by a probe)
    Public Property ConnectionStatus As String = ""
    Public Property NbdMounted As Boolean
    Public Property RawDevicePath As String = ""
    Public Property RecordedIp As String = ""
    Public Property RecordedPort As Integer
    Public Property RawDeviceSize As Long
    Public Property ActivePfsMounts As New List(Of PfsMountHandle)

    ' Outcome
    Public Property SetupErrorCode As String = ""
    Public Property SetupMessage As String = ""
End Class

''' <summary>A mounted PFS partition. UI code must read files through <see cref="WindowsPath"/>.</summary>
Public Class PfsMountHandle
    Public Property BackendKind As StorageBackendKind
    Public Property MountId As String = ""
    Public Property PartitionName As String = ""
    Public Property NativePath As String = ""
    Public Property WindowsPath As String = ""
    Public Property LegacyDriveLetter As String = ""
    Public Property DisplayName As String = ""
End Class

''' <summary>The single JSON object the WSL helper writes to stdout.</summary>
Public Class HelperResponse
    <JsonProperty("ok")>
    Public Property Ok As Boolean
    <JsonProperty("code")>
    Public Property Code As String = ""
    <JsonProperty("message")>
    Public Property Message As String = ""
    <JsonProperty("stdout")>
    Public Property Stdout As String = ""
    <JsonProperty("stderr")>
    Public Property Stderr As String = ""
    <JsonProperty("data")>
    Public Property Data As JObject = New JObject()

    ''' <summary>Parses the helper's stdout. Returns Nothing when it is not exactly one JSON object.</summary>
    Public Shared Function TryParse(text As String) As HelperResponse
        If String.IsNullOrWhiteSpace(text) Then Return Nothing
        Try
            Dim token As JToken = JToken.Parse(text.Trim())
            If token.Type <> JTokenType.Object Then Return Nothing
            Dim obj = CType(token, JObject)
            If obj("ok") Is Nothing OrElse obj("code") Is Nothing Then Return Nothing
            Dim response As HelperResponse = obj.ToObject(Of HelperResponse)()
            If response.Data Is Nothing Then response.Data = New JObject()
            If response.Code Is Nothing Then response.Code = ""
            If response.Message Is Nothing Then response.Message = ""
            If response.Stdout Is Nothing Then response.Stdout = ""
            If response.Stderr Is Nothing Then response.Stderr = ""
            Return response
        Catch ex As JsonException
            Return Nothing
        End Try
    End Function

    Public Function GetString(key As String) As String
        Dim value As JToken = Data(key)
        If value Is Nothing OrElse value.Type = JTokenType.Null Then Return ""
        Return value.ToString()
    End Function

    Public Function GetLong(key As String) As Long
        Dim value As JToken = Data(key)
        If value Is Nothing OrElse value.Type = JTokenType.Null Then Return 0
        Dim result As Long
        If Long.TryParse(value.ToString(), result) Then Return result
        Return 0
    End Function

    Public Function GetInt(key As String) As Integer
        Return CInt(Math.Max(Integer.MinValue, Math.Min(Integer.MaxValue, GetLong(key))))
    End Function

    Public Function GetBool(key As String) As Boolean
        Dim value As JToken = Data(key)
        If value Is Nothing OrElse value.Type <> JTokenType.Boolean Then Return False
        Return value.Value(Of Boolean)()
    End Function

    ''' <summary>The tool's own exit/output when the helper ran a Linux tool (hdl, pfsshell, backup...).</summary>
    Public Function ToProcessResult() As ProcessResult
        Dim exitCode As Integer = If(Data("exit_code") IsNot Nothing, GetInt("exit_code"), If(Ok, 0, -1))
        Return New ProcessResult With {
            .ExitCode = exitCode,
            .StandardOutput = OutputText.NormalizeLineEndings(Stdout),
            .StandardError = OutputText.NormalizeLineEndings(Stderr),
            .TimedOut = (Code = BackendErrorCodes.OperationTimeout),
            .Cancelled = (Code = BackendErrorCodes.OperationCancelled),
            .ErrorCode = If(Ok, "", Code)
        }
    End Function
End Class

''' <summary>A backend failure carrying a stable machine code. UI code branches on <see cref="Code"/>.</summary>
Public Class StorageBackendException
    Inherits Exception

    Public ReadOnly Property Code As String
    Public ReadOnly Property ToolOutput As String

    Public Sub New(code As String, message As String, Optional toolOutput As String = "", Optional inner As Exception = Nothing)
        MyBase.New(message, inner)
        Me.Code = code
        Me.ToolOutput = If(toolOutput, "")
    End Sub
End Class

''' <summary>Stable error codes shared with the WSL helper (psx-xmb-helper.py uses the same strings).</summary>
Public NotInheritable Class BackendErrorCodes
    Public Const Ok As String = "OK"
    Public Const WslNotInstalled As String = "WSL_NOT_INSTALLED"
    Public Const NoWsl2Distro As String = "NO_WSL2_DISTRO"
    Public Const UnsupportedDistro As String = "UNSUPPORTED_DISTRO"
    Public Const WslDistroNotFound As String = "WSL_DISTRO_NOT_FOUND"
    Public Const BackendSetupRequired As String = "BACKEND_SETUP_REQUIRED"
    Public Const HelperVersionMismatch As String = "HELPER_VERSION_MISMATCH"
    Public Const FuseUnavailable As String = "FUSE_UNAVAILABLE"
    Public Const NbdinfoMissing As String = "NBDINFO_MISSING"
    Public Const NbdfuseMissing As String = "NBDFUSE_MISSING"
    Public Const HdlDumpMissing As String = "HDL_DUMP_MISSING"
    Public Const PfsShellMissing As String = "PFSSHELL_MISSING"
    Public Const PfsFuseMissing As String = "PFSFUSE_MISSING"
    Public Const InvalidIp As String = "INVALID_IP"
    Public Const InvalidPort As String = "INVALID_PORT"
    Public Const NbdServerUnreachable As String = "NBD_SERVER_UNREACHABLE"
    Public Const NbdAlreadyConnectedOtherEndpoint As String = "NBD_ALREADY_CONNECTED_OTHER_ENDPOINT"
    Public Const NbdfuseStartFailed As String = "NBDFUSE_START_FAILED"
    Public Const NbdMountNotReady As String = "NBD_MOUNT_NOT_READY"
    Public Const PsxHddValidationFailed As String = "PSX_HDD_VALIDATION_FAILED"
    Public Const PfsMountFailed As String = "PFS_MOUNT_FAILED"
    Public Const PfsUnmountFailed As String = "PFS_UNMOUNT_FAILED"
    Public Const WindowsUncNotVisible As String = "WINDOWS_UNC_NOT_VISIBLE"
    Public Const HdlDumpFailed As String = "HDL_DUMP_FAILED"
    Public Const PfsShellFailed As String = "PFSSHELL_FAILED"
    Public Const PathTranslationFailed As String = "PATH_TRANSLATION_FAILED"
    Public Const InputFileNotVisibleInWsl As String = "INPUT_FILE_NOT_VISIBLE_IN_WSL"
    Public Const BackendBusy As String = "BACKEND_BUSY"
    Public Const BackupFailed As String = "BACKUP_FAILED"
    Public Const RestoreFailed As String = "RESTORE_FAILED"
    Public Const SizeMismatch As String = "SIZE_MISMATCH"
    Public Const DisconnectFailed As String = "DISCONNECT_FAILED"
    Public Const StaleMountState As String = "STALE_MOUNT_STATE"

    ' Additional codes needed by the implementation
    Public Const OperationTimeout As String = "OPERATION_TIMEOUT"
    Public Const OperationCancelled As String = "OPERATION_CANCELLED"
    Public Const NotConnected As String = "NOT_CONNECTED"
    Public Const InvalidRequest As String = "INVALID_REQUEST"
    Public Const InvalidPartitionName As String = "INVALID_PARTITION_NAME"
    Public Const HelperProtocolError As String = "HELPER_PROTOCOL_ERROR"
    Public Const WslLaunchFailed As String = "WSL_LAUNCH_FAILED"
    Public Const LocalToolMissing As String = "LOCAL_TOOL_MISSING"
    Public Const LocalHddNotFound As String = "LOCAL_HDD_NOT_FOUND"

    Public Const HelperOutdatedMessage As String = "WSL backend helper is outdated. Run Install / Repair WSL Backend."

    ''' <summary>The actionable next step shown under every backend error.</summary>
    Public Shared Function NextStep(code As String) As String
        Select Case code
            Case WslNotInstalled
                Return "Install WSL2 and an Ubuntu distribution (for example run 'wsl --install -d Ubuntu-24.04' in an administrator terminal), restart Windows, then reopen PSX XMB Manager."
            Case NoWsl2Distro
                Return "Install an Ubuntu or Debian WSL2 distribution, or convert an existing one with 'wsl --set-version <name> 2'."
            Case WslDistroNotFound
                Return "Select an installed WSL2 distribution in 'WSL2 Distribution'."
            Case UnsupportedDistro
                Return "Use an Ubuntu or Debian WSL2 distribution, or install nbdfuse, nbdinfo, hdl_dump, pfsshell and pfsfuse manually in this distribution."
            Case BackendSetupRequired, NbdinfoMissing, NbdfuseMissing, HdlDumpMissing, PfsShellMissing, PfsFuseMissing
                Return "Click 'Install / Repair WSL Backend'."
            Case HelperVersionMismatch
                Return HelperOutdatedMessage
            Case FuseUnavailable
                Return "/dev/fuse is not available in the WSL distribution. Update WSL with 'wsl --update' and make sure the distribution runs as WSL2."
            Case InvalidIp
                Return "Enter the PSX IPv4 address shown by Open PS2 Loader's NBD server, for example 192.168.1.50."
            Case InvalidPort
                Return "Use a port between 1 and 65535 (Open PS2 Loader uses 10809)."
            Case NbdServerUnreachable
                Return "Start the NBD server in Open PS2 Loader on the PSX, check the IP address, and make sure the PC and PSX are on the same network and no firewall blocks port 10809."
            Case NbdAlreadyConnectedOtherEndpoint
                Return "Disconnect the existing PSX connection first."
            Case NbdfuseStartFailed, NbdMountNotReady
                Return "Check the NBD server on the PSX and try again. The nbdfuse log is in ~/.local/state/psx-xmb-manager/nbdfuse.log inside WSL."
            Case PsxHddValidationFailed
                Return "The NBD export did not look like a PS2/PSX HDD. Make sure Open PS2 Loader exports the internal HDD."
            Case PfsMountFailed, PfsUnmountFailed
                Return "Close any program that has files open on the partition and try again."
            Case WindowsUncNotVisible
                Return "Windows could not see the WSL mount. Open \\wsl.localhost in Explorer once, run 'wsl --update', and try again."
            Case PathTranslationFailed, InputFileNotVisibleInWsl
                Return "Store the file on a local Windows drive (for example C:\ or D:\) that WSL can see under /mnt."
            Case BackendBusy
                Return "Wait for the running PSX HDD operation to finish and try again."
            Case SizeMismatch
                Return "Only restore a backup that was made from this exact HDD size."
            Case StaleMountState, DisconnectFailed
                Return "Use 'Recover WSL Connection' to clean up the old mount, then connect again."
            Case OperationTimeout
                Return "The PSX did not answer in time. Check the network connection and try again."
            Case NotConnected
                Return "Connect to the PSX first."
            Case LocalToolMissing
                Return "Make sure the Tools folder next to PSX XMB Manager.exe is complete."
            Case LocalHddNotFound
                Return "Connect a PS2/PSX formatted HDD to this PC."
            Case Else
                Return "Check the details below and try again."
        End Select
    End Function

    ''' <summary>High-level problem, endpoint/path, next step and the raw tool output.</summary>
    Public Shared Function FormatForUser(ex As StorageBackendException) As String
        Dim text As New StringBuilder()
        text.Append(ex.Message)
        text.Append(vbCrLf).Append(vbCrLf).Append("What to do: ").Append(NextStep(ex.Code))
        text.Append(vbCrLf).Append(vbCrLf).Append("Error code: ").Append(ex.Code)
        If Not String.IsNullOrWhiteSpace(ex.ToolOutput) Then
            Dim output As String = ex.ToolOutput.Trim()
            If output.Length > 1500 Then output = output.Substring(output.Length - 1500)
            text.Append(vbCrLf).Append(vbCrLf).Append("Tool output:").Append(vbCrLf).Append(output.Replace(vbLf, vbCrLf))
        End If
        Return text.ToString()
    End Function
End Class

''' <summary>Connection states of a storage backend. UI enablement is derived from these, never from button text.</summary>
Public Enum ConnectionState
    Unavailable
    NeedsSetup
    Disconnected
    Connecting
    Connected
    BusyRead
    BusyWrite
    Disconnecting
    Faulted
End Enum

Public NotInheritable Class ConnectionStateRules

    Public Shared Function CanConnect(state As ConnectionState) As Boolean
        Return state = ConnectionState.Disconnected
    End Function

    Public Shared Function CanStartOperation(state As ConnectionState) As Boolean
        Return state = ConnectionState.Connected
    End Function

    ''' <summary>A running write (or any running operation) prevents disconnect; Faulted may be cleaned up.</summary>
    Public Shared Function CanDisconnect(state As ConnectionState) As Boolean
        Return state = ConnectionState.Connected OrElse state = ConnectionState.Faulted
    End Function

    ''' <summary>The application may only close while nothing is writing to the HDD.</summary>
    Public Shared Function CanCloseApplication(state As ConnectionState) As Boolean
        Return state <> ConnectionState.BusyWrite AndAlso state <> ConnectionState.Connecting AndAlso state <> ConnectionState.Disconnecting
    End Function

    Public Shared Function IsHddAvailable(state As ConnectionState) As Boolean
        Return state = ConnectionState.Connected OrElse state = ConnectionState.BusyRead OrElse state = ConnectionState.BusyWrite
    End Function

    Public Shared Function IsValidTransition(fromState As ConnectionState, toState As ConnectionState) As Boolean
        If fromState = toState Then Return True
        Select Case fromState
            Case ConnectionState.Unavailable, ConnectionState.NeedsSetup
                Return toState = ConnectionState.Unavailable OrElse toState = ConnectionState.NeedsSetup OrElse
                       toState = ConnectionState.Disconnected OrElse toState = ConnectionState.Connected OrElse
                       toState = ConnectionState.Faulted
            Case ConnectionState.Disconnected
                ' Connected without Connecting = an existing healthy session recovered at startup.
                Return toState = ConnectionState.Connecting OrElse toState = ConnectionState.Connected OrElse
                       toState = ConnectionState.NeedsSetup OrElse toState = ConnectionState.Unavailable OrElse
                       toState = ConnectionState.Faulted
            Case ConnectionState.Connecting
                Return toState = ConnectionState.Connected OrElse toState = ConnectionState.Disconnected OrElse
                       toState = ConnectionState.Faulted
            Case ConnectionState.Connected
                Return toState = ConnectionState.BusyRead OrElse toState = ConnectionState.BusyWrite OrElse
                       toState = ConnectionState.Disconnecting OrElse toState = ConnectionState.Faulted
            Case ConnectionState.BusyRead, ConnectionState.BusyWrite
                Return toState = ConnectionState.Connected OrElse toState = ConnectionState.Faulted
            Case ConnectionState.Disconnecting
                Return toState = ConnectionState.Disconnected OrElse toState = ConnectionState.Connected OrElse
                       toState = ConnectionState.Faulted
            Case ConnectionState.Faulted
                Return toState = ConnectionState.Disconnecting OrElse toState = ConnectionState.Disconnected OrElse
                       toState = ConnectionState.Connected OrElse toState = ConnectionState.NeedsSetup OrElse
                       toState = ConnectionState.Unavailable
        End Select
        Return False
    End Function
End Class

''' <summary>Fixed timeouts from the migration plan. A timeout is reported as OPERATION_TIMEOUT, never as a tool failure.</summary>
Public NotInheritable Class OperationTimeouts
    Public Shared ReadOnly WslProbe As TimeSpan = TimeSpan.FromSeconds(5)
    Public Shared ReadOnly NbdInfo As TimeSpan = TimeSpan.FromSeconds(5)
    Public Shared ReadOnly NbdfuseReadiness As TimeSpan = TimeSpan.FromSeconds(10)
    Public Shared ReadOnly HelperStatus As TimeSpan = TimeSpan.FromSeconds(5)
    Public Shared ReadOnly MountVisibility As TimeSpan = TimeSpan.FromSeconds(5)
    Public Shared ReadOnly MountVisibilityPoll As TimeSpan = TimeSpan.FromMilliseconds(100)
    Public Shared ReadOnly UnmountConfirmation As TimeSpan = TimeSpan.FromSeconds(10)
    Public Shared ReadOnly HdlDumpToc As TimeSpan = TimeSpan.FromSeconds(30)
    Public Shared ReadOnly PfsShellMetadata As TimeSpan = TimeSpan.FromSeconds(60)
    Public Shared ReadOnly Unlimited As TimeSpan = Timeout.InfiniteTimeSpan

    ''' <summary>Starting a stopped WSL2 VM/distro can take longer than the 5 s probe budget; the first call gets this.</summary>
    Public Shared ReadOnly WslDistroStart As TimeSpan = TimeSpan.FromSeconds(60)

    ''' <summary>Extra time given to the wsl.exe process so the helper's own (exact) timeout always fires first.</summary>
    Public Shared ReadOnly WslLaunchOverhead As TimeSpan = TimeSpan.FromSeconds(20)

    ''' <summary>Bootstrap: apt-get plus two source builds.</summary>
    Public Shared ReadOnly Bootstrap As TimeSpan = TimeSpan.FromMinutes(45)

    Public Shared Function ForHdlDump(verb As String) As TimeSpan
        Select Case If(verb, "").ToLowerInvariant()
            Case "inject_cd", "inject_dvd"
                Return Unlimited
            Case "toc", "hdl_toc", "query", "info", "dump_header"
                Return HdlDumpToc
            Case Else
                Return PfsShellMetadata
        End Select
    End Function

    Public Shared Function ForPfsShell(commands As IEnumerable(Of String)) As TimeSpan
        For Each command As String In commands
            If PfsShellCommands.VerbOf(command) = "put" Then Return Unlimited
        Next
        Return PfsShellMetadata
    End Function

    ''' <summary>Outer wait for a wsl.exe process whose helper enforces <paramref name="inner"/>.</summary>
    Public Shared Function WithLaunchOverhead(inner As TimeSpan) As TimeSpan
        If inner = Unlimited Then Return Unlimited
        Return inner + WslLaunchOverhead
    End Function

    ''' <summary>Seconds for the helper JSON request; 0 means no limit.</summary>
    Public Shared Function ToHelperSeconds(value As TimeSpan) As Integer
        If value = Unlimited Then Return 0
        Return CInt(Math.Ceiling(value.TotalSeconds))
    End Function
End Class

Public NotInheritable Class HdlDumpCommands
    ''' <summary>hdl_dump verbs that only read the HDD.</summary>
    Public Shared Function IsReadOnly(verb As String) As Boolean
        Select Case If(verb, "").ToLowerInvariant()
            Case "toc", "hdl_toc", "query", "info", "dump_header"
                Return True
        End Select
        Return False
    End Function

    Public Shared Function IsCancellable(verb As String) As Boolean
        ' hdl_dump commits the partition table only at the end of an injection, so SIGINT is safe.
        Select Case If(verb, "").ToLowerInvariant()
            Case "inject_cd", "inject_dvd", "toc", "hdl_toc", "info", "dump_header"
                Return True
        End Select
        Return False
    End Function
End Class

Public NotInheritable Class PfsShellCommands
    Private Shared ReadOnly ReadOnlyVerbs As String() = {"device", "mount", "umount", "ls", "lcd", "cd", "pwd", "get", "help", "exit", "lspart"}

    Public Shared Function VerbOf(command As String) As String
        Dim trimmed As String = If(command, "").Trim()
        Dim space As Integer = trimmed.IndexOf(" "c)
        Return If(space < 0, trimmed, trimmed.Substring(0, space)).ToLowerInvariant()
    End Function

    ''' <summary>Unknown verbs count as writes, which is the safe default.</summary>
    Public Shared Function IsReadOnly(commands As IEnumerable(Of String)) As Boolean
        For Each command As String In commands
            Dim verb As String = VerbOf(command)
            If verb.Length > 0 AndAlso Array.IndexOf(ReadOnlyVerbs, verb) < 0 Then Return False
        Next
        Return True
    End Function

    ''' <summary>pfsshell reads one command per line, so a line break inside a command would inject another command.</summary>
    Public Shared Function ContainsLineBreak(commands As IEnumerable(Of String)) As Boolean
        For Each command As String In commands
            If command Is Nothing Then Return True
            If command.IndexOfAny({ControlChars.Cr, ControlChars.Lf, ControlChars.NullChar}) >= 0 Then Return True
        Next
        Return False
    End Function

    ''' <summary>
    ''' Commands that upload <paramref name="relativeWindowsPath"/> (relative to the working directory) into the
    ''' current PFS directory under its own file name.
    ''' Local Windows keeps the original "put res\x" + "rename res\x x" pair. Linux pfsshell stores "put a/b" as the
    ''' PFS path "a/b", so for WSL the local directory is entered with lcd instead.
    ''' </summary>
    Public Shared Function PutFile(kind As StorageBackendKind, relativeWindowsPath As String) As List(Of String)
        Dim parts As String() = relativeWindowsPath.Split({"\"c, "/"c}, StringSplitOptions.RemoveEmptyEntries)
        Dim fileName As String = parts(parts.Length - 1)
        Dim result As New List(Of String)

        If kind = StorageBackendKind.WSL2NBD Then
            If parts.Length = 1 Then
                result.Add("put " + fileName)
            Else
                Dim directoryParts As String() = parts.Take(parts.Length - 1).ToArray()
                result.Add("lcd " + String.Join("/", directoryParts))
                result.Add("put " + fileName)
                result.Add("lcd " + String.Join("/", Enumerable.Repeat("..", directoryParts.Length)))
            End If
        Else
            result.Add("put " + relativeWindowsPath)
            If parts.Length > 1 Then result.Add("rename " + relativeWindowsPath + " " + fileName)
        End If

        Return result
    End Function
End Class

Public NotInheritable Class OutputText
    ''' <summary>Converts CRLF and lone CR to LF so Windows and Linux tool output parse the same way.</summary>
    Public Shared Function NormalizeLineEndings(text As String) As String
        If String.IsNullOrEmpty(text) Then Return ""
        Return text.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf)
    End Function

    Public Shared Function SplitLines(text As String) As String()
        Return NormalizeLineEndings(text).Split({vbLf}, StringSplitOptions.None)
    End Function

    ''' <summary>
    ''' Decodes wsl.exe output. wsl.exe writes UTF-16LE unless WSL_UTF8=1 is honoured; Linux programs started
    ''' through it write UTF-8.
    ''' </summary>
    Public Shared Function DecodeWslText(bytes As Byte()) As String
        If bytes Is Nothing OrElse bytes.Length = 0 Then Return ""
        Dim text As String
        If bytes.Length >= 2 AndAlso bytes(0) = &HFF AndAlso bytes(1) = &HFE Then
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
        ElseIf LooksLikeUtf16LE(bytes) Then
            text = Encoding.Unicode.GetString(bytes, 0, bytes.Length - (bytes.Length Mod 2))
        ElseIf bytes.Length >= 3 AndAlso bytes(0) = &HEF AndAlso bytes(1) = &HBB AndAlso bytes(2) = &HBF Then
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
        Else
            text = Encoding.UTF8.GetString(bytes)
        End If
        Return text.Replace(ControlChars.NullChar, "")
    End Function

    Private Shared Function LooksLikeUtf16LE(bytes As Byte()) As Boolean
        Dim pairs As Integer = Math.Min(bytes.Length \ 2, 256)
        If pairs = 0 Then Return False
        Dim zeroHigh As Integer = 0
        For i As Integer = 0 To pairs - 1
            If bytes(2 * i + 1) = 0 AndAlso bytes(2 * i) <> 0 Then zeroHigh += 1
        Next
        Return zeroHigh * 2 >= pairs
    End Function
End Class

Public NotInheritable Class WslOutputParsers

    ''' <summary>Parses "wsl.exe --list --quiet".</summary>
    Public Shared Function ParseQuietList(text As String) As List(Of String)
        Dim names As New List(Of String)
        For Each line As String In OutputText.SplitLines(text)
            Dim name As String = line.Trim()
            If name.Length > 0 AndAlso Not name.Contains(" ") Then names.Add(name)
        Next
        Return names
    End Function

    ''' <summary>
    ''' Parses "wsl.exe --list --verbose". The header is localized, so rows are recognised by shape instead:
    ''' optional "*" default marker, the name, the state, and a numeric WSL version as the last column.
    ''' </summary>
    Public Shared Function ParseVerboseList(text As String) As List(Of WslDistroInfo)
        Dim distros As New List(Of WslDistroInfo)
        For Each line As String In OutputText.SplitLines(text)
            Dim row As String = line.Trim()
            If row.Length = 0 Then Continue For
            Dim isDefault As Boolean = row.StartsWith("*")
            If isDefault Then row = row.Substring(1).Trim()
            Dim columns As String() = row.Split({" "c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)
            If columns.Length < 3 Then Continue For
            Dim version As Integer
            If Not Integer.TryParse(columns(columns.Length - 1), version) Then Continue For
            If version < 1 OrElse version > 9 Then Continue For
            distros.Add(New WslDistroInfo With {
                .Name = String.Join(" ", columns.Take(columns.Length - 2)),
                .State = columns(columns.Length - 2),
                .Version = version,
                .IsDefault = isDefault
            })
        Next
        Return distros
    End Function

    ''' <summary>Combines the quiet list (names) with the verbose list (versions).</summary>
    Public Shared Function MergeLists(quietNames As List(Of String), verbose As List(Of WslDistroInfo)) As List(Of WslDistroInfo)
        Dim merged As New List(Of WslDistroInfo)
        For Each info As WslDistroInfo In verbose
            merged.Add(info)
        Next
        For Each name As String In quietNames
            If Not merged.Any(Function(d) String.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) Then
                merged.Add(New WslDistroInfo With {.Name = name, .Version = 0, .State = "Unknown"})
            End If
        Next
        Return merged
    End Function

    ''' <summary>
    ''' Selection order from the plan: saved distro, Ubuntu-24.04, Ubuntu, the only WSL2 distro; otherwise nothing.
    ''' </summary>
    Public Shared Function SelectDistro(distros As IEnumerable(Of WslDistroInfo), savedDistro As String) As String
        Dim wsl2 As List(Of WslDistroInfo) = distros.Where(Function(d) d.Version = 2).ToList()
        If Not String.IsNullOrEmpty(savedDistro) Then
            Dim saved As WslDistroInfo = wsl2.FirstOrDefault(Function(d) String.Equals(d.Name, savedDistro, StringComparison.OrdinalIgnoreCase))
            If saved IsNot Nothing Then Return saved.Name
        End If
        For Each preferred As String In {"Ubuntu-24.04", "Ubuntu"}
            Dim match As WslDistroInfo = wsl2.FirstOrDefault(Function(d) d.Name = preferred)
            If match IsNot Nothing Then Return match.Name
        Next
        If wsl2.Count = 1 Then Return wsl2(0).Name
        Return ""
    End Function

    ''' <summary>Parses /etc/os-release KEY=VALUE lines (values may be quoted).</summary>
    Public Shared Function ParseOsRelease(text As String) As Dictionary(Of String, String)
        Dim values As New Dictionary(Of String, String)(StringComparer.Ordinal)
        For Each line As String In OutputText.SplitLines(text)
            Dim row As String = line.Trim()
            If row.Length = 0 OrElse row.StartsWith("#") Then Continue For
            Dim equals As Integer = row.IndexOf("="c)
            If equals <= 0 Then Continue For
            Dim key As String = row.Substring(0, equals).Trim()
            Dim value As String = row.Substring(equals + 1).Trim()
            If value.Length >= 2 AndAlso (value.StartsWith("""") AndAlso value.EndsWith("""") OrElse value.StartsWith("'") AndAlso value.EndsWith("'")) Then
                value = value.Substring(1, value.Length - 2)
            End If
            values(key) = value
        Next
        Return values
    End Function

    ''' <summary>Automatic setup is supported on Ubuntu and Debian only.</summary>
    Public Shared Function SupportsAutomaticSetup(osRelease As Dictionary(Of String, String)) As Boolean
        Dim id As String = ""
        osRelease.TryGetValue("ID", id)
        Return id = "ubuntu" OrElse id = "debian"
    End Function
End Class

Public NotInheritable Class StorageValidation

    ''' <summary>Requires a dotted-quad IPv4 address (IPAddress.TryParse alone also accepts "1" or "1.2").</summary>
    Public Shared Function TryParseIPv4(text As String, ByRef normalized As String) As Boolean
        normalized = ""
        If String.IsNullOrWhiteSpace(text) Then Return False
        Dim candidate As String = text.Trim()
        If Not Regex.IsMatch(candidate, "^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$") Then Return False
        Dim address As IPAddress = Nothing
        If Not IPAddress.TryParse(candidate, address) Then Return False
        If address.AddressFamily <> AddressFamily.InterNetwork Then Return False
        normalized = address.ToString()
        Return True
    End Function

    Public Shared Function IsValidPort(port As Integer) As Boolean
        Return port >= 1 AndAlso port <= 65535
    End Function

    ''' <summary>APA partition names are at most 32 printable ASCII characters.</summary>
    Public Shared Function IsValidPartitionName(name As String) As Boolean
        If String.IsNullOrEmpty(name) OrElse name.Length > 32 Then Return False
        For Each c As Char In name
            If AscW(c) < &H20 OrElse AscW(c) > &H7E Then Return False
        Next
        Return True
    End Function

    ''' <summary>Mount ids come from the helper; only plain directory names are accepted back.</summary>
    Public Shared Function IsSafeMountId(mountId As String) As Boolean
        If String.IsNullOrEmpty(mountId) OrElse mountId = "." OrElse mountId = ".." Then Return False
        Return Regex.IsMatch(mountId, "^[A-Za-z0-9._-]{1,64}$")
    End Function

    Public Shared Function IsValidDistroName(distro As String) As Boolean
        Return Not String.IsNullOrEmpty(distro) AndAlso Regex.IsMatch(distro, "^[A-Za-z0-9._-]+$")
    End Function

    ''' <summary>
    ''' /home/ben/x -> \\wsl.localhost\DISTRO\home\ben\x. The path must be absolute; only the leading "/" is removed
    ''' and the remaining separators are replaced.
    ''' </summary>
    Public Shared Function LinuxPathToWslUnc(distro As String, linuxPath As String, Optional uncHost As String = "wsl.localhost") As String
        If Not IsValidDistroName(distro) Then Throw New ArgumentException("Invalid WSL distribution name: " + distro, NameOf(distro))
        If String.IsNullOrEmpty(linuxPath) OrElse Not linuxPath.StartsWith("/") Then
            Throw New ArgumentException("Linux path must be absolute: " + linuxPath, NameOf(linuxPath))
        End If
        If linuxPath.IndexOf("\"c) >= 0 Then Throw New ArgumentException("Linux path contains a backslash: " + linuxPath, NameOf(linuxPath))
        Return "\\" + uncHost + "\" + distro + "\" + linuxPath.Substring(1).Replace("/", "\")
    End Function

    ''' <summary>The restore rule: the backup must be exactly as large as the target HDD.</summary>
    Public Shared Function IsExactSizeMatch(sourceBytes As Long, targetBytes As Long) As Boolean
        Return sourceBytes > 0 AndAlso sourceBytes = targetBytes
    End Function

    Public Shared Function FormatSize(bytes As Long) As String
        Dim units As String() = {"B", "KB", "MB", "GB", "TB"}
        Dim value As Double = bytes
        Dim unit As Integer = 0
        While value >= 1024 AndAlso unit < units.Length - 1
            value /= 1024
            unit += 1
        End While
        Return String.Format(Globalization.CultureInfo.InvariantCulture, "{0:0.##} {1}", value, units(unit))
    End Function
End Class

''' <summary>Builds a Windows command line from separate arguments (the rules CommandLineToArgvW and the MSVC runtime use).</summary>
Public NotInheritable Class CommandLineQuoting
    Public Shared Function Quote(argument As String) As String
        If argument Is Nothing Then argument = ""
        If argument.Length > 0 AndAlso argument.IndexOfAny({" "c, ControlChars.Tab, ControlChars.Lf, ControlChars.VerticalTab, """"c}) < 0 Then
            Return argument
        End If
        Dim quoted As New StringBuilder("""")
        Dim backslashes As Integer = 0
        For Each c As Char In argument
            If c = "\"c Then
                backslashes += 1
            ElseIf c = """"c Then
                quoted.Append("\"c, backslashes * 2 + 1).Append(""""c)
                backslashes = 0
            Else
                quoted.Append("\"c, backslashes).Append(c)
                backslashes = 0
            End If
        Next
        quoted.Append("\"c, backslashes * 2).Append(""""c)
        Return quoted.ToString()
    End Function

    Public Shared Function Join(arguments As IEnumerable(Of String)) As String
        Return String.Join(" ", arguments.Select(Function(a) Quote(a)))
    End Function
End Class

''' <summary>Application-side log under %LOCALAPPDATA%\PSX XMB Manager\Logs\. Never throws.</summary>
Public NotInheritable Class BackendLog
    Private Shared ReadOnly SyncRoot As New Object()
    Private Const MaxOutputChars As Integer = 16000

    Public Shared Property LogDirectory As String =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PSX XMB Manager", "Logs")

    Public Shared ReadOnly Property CurrentLogFile As String
        Get
            Return Path.Combine(LogDirectory, "storage-" + DateTime.Now.ToString("yyyyMMdd", Globalization.CultureInfo.InvariantCulture) + ".log")
        End Get
    End Property

    Public Shared Sub Write(backend As StorageBackendKind, distro As String, operation As String, description As String,
                            exitCode As Integer?, elapsed As TimeSpan, errorCode As String,
                            Optional stdout As String = "", Optional stderr As String = "")
        Try
            Dim entry As New StringBuilder()
            entry.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", Globalization.CultureInfo.InvariantCulture))
            entry.Append(" backend=").Append(backend.ToString())
            If Not String.IsNullOrEmpty(distro) Then entry.Append(" distro=").Append(distro)
            entry.Append(" op=").Append(operation)
            entry.Append(" exit=").Append(If(exitCode.HasValue, exitCode.Value.ToString(Globalization.CultureInfo.InvariantCulture), "-"))
            entry.Append(" elapsed=").Append(CLng(elapsed.TotalMilliseconds).ToString(Globalization.CultureInfo.InvariantCulture)).Append("ms")
            entry.Append(" code=").Append(If(String.IsNullOrEmpty(errorCode), "OK", errorCode))
            entry.AppendLine()
            entry.Append("  cmd: ").AppendLine(description)
            If Not String.IsNullOrEmpty(stdout) Then entry.Append("  stdout: ").AppendLine(Truncate(stdout))
            If Not String.IsNullOrEmpty(stderr) Then entry.Append("  stderr: ").AppendLine(Truncate(stderr))
            SyncLock SyncRoot
                Directory.CreateDirectory(LogDirectory)
                File.AppendAllText(CurrentLogFile, entry.ToString(), New UTF8Encoding(False))
            End SyncLock
        Catch
            ' Logging must never break an HDD operation.
        End Try
    End Sub

    Public Shared Sub Note(backend As StorageBackendKind, distro As String, message As String)
        Write(backend, distro, "note", message, Nothing, TimeSpan.Zero, "")
    End Sub

    Private Shared Function Truncate(text As String) As String
        Dim value As String = text.Replace(vbCrLf, vbLf).Replace(vbLf, vbLf + "    ")
        If value.Length > MaxOutputChars Then value = value.Substring(0, MaxOutputChars) + " ...[truncated]"
        Return value
    End Function
End Class
