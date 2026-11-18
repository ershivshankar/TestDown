' ── Self-Elevate to Administrator (required to control TaskManager, Antigravity, Taskbar) ──
If WScript.Arguments.Count = 0 Then
    On Error Resume Next
    Set objApp = CreateObject("Shell.Application")
    objApp.ShellExecute "wscript.exe", """" & WScript.ScriptFullName & """ /elevated", "", "runas", 1
    If Err.Number = 0 Then
        WScript.Quit
    End If
    On Error GoTo 0
End If

Set objShell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
Dim dir : dir = fso.GetParentFolderName(WScript.ScriptFullName)

' ── Kill any existing instance ───────────────────────────────────────────────
objShell.Run "taskkill /F /IM WinSpeechHost.exe", 0, True
WScript.Sleep 800

' ── Swap in a pending update if one exists ───────────────────────────────────
Dim newExe : newExe = dir & "\WinSpeechHost_new.exe"
Dim oldExe : oldExe = dir & "\WinSpeechHost.exe"
If fso.FileExists(newExe) Then
    If fso.FileExists(oldExe) Then fso.DeleteFile oldExe, True
    fso.MoveFile newExe, oldExe
End If

' ── Write host-manifest.json with the CORRECT path for this machine ──────────
Dim manifestPath : manifestPath = dir & "\host-manifest.json"
Dim exePath      : exePath      = dir & "\WinSpeechHost.exe"

' Escape backslashes for JSON
Dim exePathJson : exePathJson = Replace(exePath, "\", "\\")

Dim manifestContent
manifestContent = "{" & Chr(13) & Chr(10)
manifestContent = manifestContent & "    ""name"": ""com.jarvistype.host""," & Chr(13) & Chr(10)
manifestContent = manifestContent & "    ""description"": ""JarvisType Native Host - full Windows control""," & Chr(13) & Chr(10)
manifestContent = manifestContent & "    ""path"": """ & exePathJson & """," & Chr(13) & Chr(10)
manifestContent = manifestContent & "    ""type"": ""stdio""," & Chr(13) & Chr(10)
manifestContent = manifestContent & "    ""allowed_origins"": [" & Chr(13) & Chr(10)
manifestContent = manifestContent & "        ""chrome-extension://hejbleljjhhjbfdfoemknnbiojjenbnf/""," & Chr(13) & Chr(10)
manifestContent = manifestContent & "        ""chrome-extension://peijjbnbncfednpgnamjaggbmikgkdfe/""" & Chr(13) & Chr(10)
manifestContent = manifestContent & "    ]" & Chr(13) & Chr(10)
manifestContent = manifestContent & "}" & Chr(13) & Chr(10)

Dim ts : Set ts = fso.OpenTextFile(manifestPath, 2, True)
ts.Write manifestContent
ts.Close

' ── Register the manifest in the Windows registry (HKCU — no admin needed) ──
Dim regKey : regKey = "HKCU\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.jarvistype.host"
objShell.RegWrite regKey & "\", manifestPath, "REG_SZ"

' ── Launch WinSpeechHost in standalone mode (hidden window) ──────────────────
objShell.Run """" & oldExe & """ --standalone", 0, False

' ── Wait 2 seconds, then verify the process started ─────────────────────────
WScript.Sleep 2000
Dim checkCmd : checkCmd = "cmd /c tasklist /FI ""IMAGENAME eq WinSpeechHost.exe"" | find /I ""WinSpeechHost.exe"" > nul"
Dim exitCode : exitCode = objShell.Run(checkCmd, 0, True)
If exitCode <> 0 Then
    MsgBox "⚠ WinSpeechHost failed to start." & Chr(13) & Chr(10) & _
           "Path: " & oldExe & Chr(13) & Chr(10) & Chr(13) & Chr(10) & _
           "Please check that the file exists and try again.", _
           vbExclamation, "JarvisType Host Error"
End If
