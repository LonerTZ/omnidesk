$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

& $csc /nologo /target:winexe /platform:x64 /optimize+ /out:"$out\OmniDesk.exe" `
    /win32icon:"$root\assets\omnidesk.ico" /resource:"$root\assets\omnidesk.ico,OmniDesk.ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll `
    "$root\src\Native.cs" "$root\src\SwapService.cs" "$root\src\Supervisor.cs" "$root\src\Agent.cs" "$root\src\Program.cs"
if ($LASTEXITCODE -ne 0) { throw "build failed" }

# The installer embeds OmniDesk.exe, so it is built second.
& $csc /nologo /target:winexe /platform:x64 /optimize+ /out:"$out\OmniDeskSetup.exe" `
    /win32manifest:"$root\setup\setup.manifest" /win32icon:"$root\assets\omnidesk.ico" `
    /resource:"$out\OmniDesk.exe,OmniDesk.exe" `
    /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll `
    "$root\setup\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw "setup build failed" }

Write-Host "Built $out\OmniDesk.exe and $out\OmniDeskSetup.exe"
