$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

& $csc /nologo /target:winexe /platform:x64 /optimize+ /out:"$out\AccountSwap.exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll `
    "$root\src\Native.cs" "$root\src\SwapService.cs" "$root\src\Agent.cs" "$root\src\Program.cs"
if ($LASTEXITCODE -ne 0) { throw "build failed" }

# The installer embeds AccountSwap.exe, so it is built second.
& $csc /nologo /target:winexe /platform:x64 /optimize+ /out:"$out\AccountSwapSetup.exe" `
    /win32manifest:"$root\setup\setup.manifest" `
    /resource:"$out\AccountSwap.exe,AccountSwap.exe" `
    /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll `
    "$root\setup\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw "setup build failed" }

Write-Host "Built $out\AccountSwap.exe and $out\AccountSwapSetup.exe"
