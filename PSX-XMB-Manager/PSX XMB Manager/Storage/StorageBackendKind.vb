''' <summary>
''' Where raw PSX HDD operations are executed.
''' There is intentionally no Windows WNBD value: the network path is WSL2 + nbdfuse only.
''' </summary>
Public Enum StorageBackendKind
    None = 0
    LocalWindows = 1
    WSL2NBD = 2
End Enum
