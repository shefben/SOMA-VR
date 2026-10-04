' Structs.vb (linked) declares an AssetListViewItem with a WPF ImageSource property. WPF does not exist on net8.0/Linux,
' so the test assembly supplies an empty stand-in; nothing under test touches it.
Namespace Global.System.Windows.Media
    Public MustInherit Class ImageSource
    End Class
End Namespace
