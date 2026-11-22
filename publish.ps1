# Publish both ends to C:\APP\message (single copy, no debug/release variants).
#
#   pwsh -File .\publish.ps1
#
# Steps:
#   1. stop the running Message instance
#   2. dotnet publish (framework-dependent, no PDB) into C:\APP\message
#   3. copy the Android APK there as Message-android.apk if present
#   4. repoint the startup entry; drop the old shortcut so the app rebuilds
#      it with its AppUserModelID (Toast depends on that)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$target = 'C:\APP\message'
$exe = Join-Path $target 'MessageBridge.exe'

Write-Host '==> stopping running instance' -ForegroundColor Cyan
Get-Process MessageBridge -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Host "==> publishing to $target" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $target -Force | Out-Null
$project = Join-Path $root 'windows\MessageBridge\MessageBridge.csproj'
dotnet publish $project -c Release -r win-x64 --self-contained false -o $target -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$apk = Join-Path $root 'android\app\build\outputs\apk\debug\app-debug.apk'
if (Test-Path $apk) {
    Copy-Item $apk (Join-Path $target 'Message-android.apk') -Force
    Write-Host '==> android apk copied' -ForegroundColor Cyan
} else {
    Write-Host '==> android apk not found, skipped' -ForegroundColor Yellow
}

Write-Host '==> updating startup entry' -ForegroundColor Cyan
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Message' -Value "`"$exe`""

Write-Host '==> removing old shortcut (app recreates it with AUMID)' -ForegroundColor Cyan
$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Message.lnk'
if (Test-Path $lnk) { Remove-Item $lnk -Force }

Write-Host '==> starting' -ForegroundColor Cyan
Start-Process -FilePath $exe

# dotnet publish 会先在仓库里生成一份 bin/obj 再拷过去，这里顺手清掉，
# 让仓库里只剩源码，避免再出现「不知道跑的是哪一份」的问题。
Write-Host '==> cleaning intermediate output' -ForegroundColor Cyan
foreach ($sub in @('windows\MessageBridge\bin', 'windows\MessageBridge\obj')) {
    $full = Join-Path $root $sub
    if (Test-Path $full) { Remove-Item $full -Recurse -Force -ErrorAction SilentlyContinue }
}

$size = (Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("done: {0} files, {1:N1} MB" -f (Get-ChildItem $target -File).Count, ($size / 1MB)) -ForegroundColor Green
