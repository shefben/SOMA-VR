Imports System.Threading
Imports PSX_XMB_Manager.Structs

''' <summary>
''' Every raw PSX HDD operation goes through this interface. Windows never decide whether a Windows or a
''' WSL executable runs; the backend implementation does.
''' </summary>
Public Interface IPSXStorageBackend

    ReadOnly Property Kind As StorageBackendKind
    ReadOnly Property DisplayName As String
    ReadOnly Property MountedDrive As MountedPSXDrive
    ReadOnly Property IsConnected As Boolean

    ''' <summary>Current connection state; UI enablement is derived from it.</summary>
    ReadOnly Property State As ConnectionState

    ''' <summary>PFS partitions this backend currently has mounted.</summary>
    ReadOnly Property ActiveMounts As IReadOnlyList(Of PfsMountHandle)

    ''' <summary>Raised (on any thread) whenever <see cref="State"/> changes.</summary>
    Event StateChanged As EventHandler

    ''' <summary>Raised (on any thread) after a PFS partition was unmounted, with the handle that went away.</summary>
    Event MountRemoved As EventHandler(Of PfsMountHandle)

    Function ProbeAsync(cancellationToken As CancellationToken) As Task(Of BackendProbeResult)

    Function ConnectAsync(
        ipAddress As String,
        port As Integer,
        cancellationToken As CancellationToken
    ) As Task(Of MountedPSXDrive)

    Function DisconnectAsync(
        cancellationToken As CancellationToken
    ) As Task

    ''' <param name="progress">Receives progress lines (for example injection percentages) while the tool runs.</param>
    Function RunHdlDumpAsync(
        arguments As IList(Of String),
        windowsWorkingDirectory As String,
        cancellationToken As CancellationToken,
        Optional progress As IProgress(Of String) = Nothing
    ) As Task(Of ProcessResult)

    Function RunPfsShellAsync(
        commands As IList(Of String),
        windowsWorkingDirectory As String,
        cancellationToken As CancellationToken
    ) As Task(Of ProcessResult)

    Function MountPfsPartitionAsync(
        partitionName As String,
        displayName As String,
        cancellationToken As CancellationToken
    ) As Task(Of PfsMountHandle)

    Function UnmountPfsPartitionAsync(
        mount As PfsMountHandle,
        cancellationToken As CancellationToken
    ) As Task

    ''' <summary>Returns the path a tool of this backend must be given for a Windows file or folder.</summary>
    ''' <param name="mustExist">True when the path is an input file that has to be visible to the backend.</param>
    Function ConvertWindowsPathAsync(
        windowsPath As String,
        cancellationToken As CancellationToken,
        Optional mustExist As Boolean = False
    ) As Task(Of String)

    Function GetRawDeviceSizeAsync(
        cancellationToken As CancellationToken
    ) As Task(Of Long)

    ''' <summary>Copies the whole connected HDD into <paramref name="windowsDestinationFile"/>.</summary>
    ''' <param name="blockSize">"1M" or "4M".</param>
    Function BackupRawDeviceAsync(
        windowsDestinationFile As String,
        blockSize As String,
        progress As IProgress(Of String),
        cancellationToken As CancellationToken
    ) As Task(Of ProcessResult)

    ''' <summary>
    ''' Overwrites the whole connected HDD with <paramref name="windowsSourceFile"/>. The caller must already have shown a
    ''' destructive confirmation. Sizes must match exactly. Not cancellable once writing has started.
    ''' </summary>
    Function RestoreRawDeviceAsync(
        windowsSourceFile As String,
        blockSize As String,
        progress As IProgress(Of String),
        cancellationToken As CancellationToken
    ) As Task(Of ProcessResult)

End Interface
