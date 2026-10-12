' Starts the status board with the venv that sits next to this script. The window stays hidden.
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("Wscript.Shell")
folder = fso.GetParentFolderName(WScript.ScriptFullName)
py = fso.BuildPath(fso.BuildPath(fso.BuildPath(folder, ".venv"), "Scripts"), "pythonw.exe")
script = fso.BuildPath(folder, "desktop_status.py")
rt = fso.BuildPath(folder, "runtime")
If Not fso.FolderExists(rt) Then fso.CreateFolder(rt)
If Not fso.FileExists(py) Then
    Set ts = fso.CreateTextFile(fso.BuildPath(rt, "last-error.txt"), True)
    ts.WriteLine "Python was not found at " & py
    ts.Close
    WScript.Quit 1
End If
sh.Run """" & py & """ """ & script & """ --overlay", 0, False
