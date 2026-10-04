Imports System.Threading
Imports PSX_XMB_Manager.Structs

Public Class NewPartition

    Public MountedDrive As MountedPSXDrive
    Public StorageBackend As IPSXStorageBackend

    Private Async Sub CreatePartitionButton_Click(sender As Object, e As RoutedEventArgs) Handles CreatePartitionButton.Click
        If MsgBox("Dou you really want to create the partition " + NewPartitionNameTextBox.Text + " with the size of " + NewPartitionSizeTextBox.Text + " MB ?", MsgBoxStyle.YesNo, "Please confirm") = MsgBoxResult.Yes Then

            If StorageBackend Is Nothing OrElse Not StorageBackend.IsConnected Then
                MsgBox("The PSX HDD is not connected anymore.", MsgBoxStyle.Exclamation)
                Return
            End If

            'mkpart commands, kept in memory and sent straight to pfsshell
            Dim Commands As New List(Of String) From {
                "device " + StorageBackend.MountedDrive.DriveID,
                "mkpart " + NewPartitionNameTextBox.Text + " " + NewPartitionSizeTextBox.Text + "M PFS",
                "exit"
            }

            CreatePartitionButton.IsEnabled = False
            Try
                'Proceed to partition creation
                Dim Result As ProcessResult = Await StorageBackend.RunPfsShellAsync(Commands, Nothing, CancellationToken.None)
                Dim PFSShellOutput As String = Result.CombinedOutput

                If PFSShellOutput.Contains("Main partition of " + NewPartitionSizeTextBox.Text + "M created.") Then
                    MsgBox("Partition " + NewPartitionNameTextBox.Text + " created with success!", MsgBoxStyle.Information)
                    Utils.ReloadPartitions()
                    Close()
                Else
                    MsgBox("There was an error in creating the partition, please check if the name doesn't already exists of if you have enough space." + vbCrLf + PFSShellOutput.Replace(vbLf, vbCrLf), MsgBoxStyle.Exclamation)
                End If
            Catch ex As Exception
                Dim BackendError As StorageBackendException = TryCast(ex, StorageBackendException)
                MsgBox(If(BackendError IsNot Nothing, BackendErrorCodes.FormatForUser(BackendError), ex.Message), MsgBoxStyle.Critical, "Error")
            Finally
                CreatePartitionButton.IsEnabled = True
            End Try

        End If
    End Sub

End Class
