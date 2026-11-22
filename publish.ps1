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

# 注意：这里不用 $ErrorActionPreference='Stop'。
# PowerShell 5.1 下它会把原生命令写到 stderr 的普通提示也当成终止错误，
# dotnet 一输出警告脚本就中断了。所有关键步骤改用 $LASTEXITCODE 显式判断。
$root = $PSScriptRoot
if ([string]::IsNullOrEmpty($root)) {
    $root = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$target = 'C:\APP\message'
$exe = Join-Path $target 'MessageBridge.exe'

Write-Host "==> repo root: $root" -ForegroundColor Cyan

Write-Host '==> stopping running instance' -ForegroundColor Cyan
Get-Process MessageBridge -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Host "==> publishing to $target" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $target -Force | Out-Null
$project = Join-Path $root 'windows\MessageBridge\MessageBridge.csproj'

# 先显式 build 一次：resources.pri 是 NoVsAppx.targets 里 AfterTargets="Build"
# 钩子生成的，直接 publish 时这个钩子可能因为增量构建被跳过，导致目标目录缺 pri，
# 启动就 XamlParseException。
$buildLog = Join-Path $env:TEMP 'msgbridge-build.log'
dotnet build $project -c Release -r win-x64 *> $buildLog
if ($LASTEXITCODE -ne 0) {
    Get-Content $buildLog | Select-Object -Last 25
    throw "dotnet build failed (exit $LASTEXITCODE)"
}

$binDir = Join-Path $root 'windows\MessageBridge\bin'
$pri = Get-ChildItem $binDir -Recurse -Filter 'resources.pri' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $pri) { throw "resources.pri not found under $binDir" }

dotnet publish $project -c Release -r win-x64 --no-build -o $target -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$apk = Join-Path $root 'android\app\build\outputs\apk\debug\app-debug.apk'
if (Test-Path $apk) {
    Copy-Item $apk (Join-Path $target 'Message-android.apk') -Force
    Write-Host '==> android apk copied' -ForegroundColor Cyan
} else {
    Write-Host '==> android apk not found, skipped' -ForegroundColor Yellow
}

# resources.pri 由 NoVsAppx.targets 的 AfterBuild 钩子生成在 bin 里，
# publish 不认识它（AddPriPayloadFilesToCopyToOutputDirectoryItems 被清空了），
# 少了它会 XamlParseException 直接起不来。
Copy-Item $pri.FullName (Join-Path $target 'resources.pri') -Force
Write-Host "==> resources.pri copied ($($pri.Length) bytes)" -ForegroundColor Cyan

Write-Host '==> updating startup entry' -ForegroundColor Cyan
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Message' -Value "`"$exe`""

# 快捷方式不在这里动：它带着 AppUserModelID（Toast 靠这个身份），
# 用 WScript.Shell 重建会弄丢；而删掉再让应用重建又可能冒出「Message (2).lnk」。
# 应用每次启动都会自己校验 TargetPath，发现指向旧路径就原地覆盖重建，交给它即可。

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
