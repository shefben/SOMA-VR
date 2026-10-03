''' <summary>Creates the backend for the connection method chosen in the main window.</summary>
Public NotInheritable Class StorageBackendFactory

    Private Sub New()
    End Sub

    ''' <summary>Parses the persisted StorageBackend setting; anything unknown falls back to the WSL2 default.</summary>
    Public Shared Function ParseKind(value As String) As StorageBackendKind
        If String.Equals(value, StorageBackendKind.LocalWindows.ToString(), StringComparison.OrdinalIgnoreCase) Then Return StorageBackendKind.LocalWindows
        Return StorageBackendKind.WSL2NBD
    End Function

    Public Shared Function Create(kind As StorageBackendKind, wslDistro As String) As IPSXStorageBackend
        Select Case kind
            Case StorageBackendKind.WSL2NBD
                Return New WSL2NBDBackend(wslDistro)
            Case StorageBackendKind.LocalWindows
                Return New LocalWindowsBackend()
            Case Else
                Throw New ArgumentOutOfRangeException(NameOf(kind), "No storage backend for " + kind.ToString())
        End Select
    End Function

End Class
