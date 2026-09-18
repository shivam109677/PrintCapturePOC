[CmdletBinding()]
param(
    [string]$UploadUrl = "",
    [string]$InstallDirectory = "$env:ProgramData\PrintCapturePOC"
)

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"",
        "-InstallDirectory", "`"$InstallDirectory`"")
    if ($UploadUrl) { $arguments += @("-UploadUrl", "`"$UploadUrl`") }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments
    return
}

$project = Join-Path $PSScriptRoot "..\src\PrintMonitor\PrintMonitor.csproj"
$publish = Join-Path $env:TEMP "PrintCapturePOC-publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $InstallDirectory "captured_jobs") | Out-Null
Copy-Item (Join-Path $publish "PrintMonitor.exe") (Join-Path $InstallDirectory "PrintMonitor.exe") -Force

$arguments = "--output `"$InstallDirectory\captured_jobs`""
if ($UploadUrl) { $arguments += " --upload-url `"$UploadUrl`"" }
$action = New-ScheduledTaskAction -Execute (Join-Path $InstallDirectory "PrintMonitor.exe") -Argument $arguments
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName "PrintCapturePOC" -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName "PrintCapturePOC"

Write-Host "Installed and started. Captures: $InstallDirectory\captured_jobs"
Write-Host "Remove with: Unregister-ScheduledTask -TaskName PrintCapturePOC"
