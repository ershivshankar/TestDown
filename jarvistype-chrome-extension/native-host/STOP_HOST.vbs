Set objShell = CreateObject("WScript.Shell")
objShell.Run "taskkill /F /IM WinSpeechHost.exe", 0, True
objShell.Run "taskkill /F /IM RuntimeHost.exe", 0, True
