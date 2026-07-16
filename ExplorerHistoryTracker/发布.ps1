$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
$parentDir = Split-Path $scriptDir -Parent
$finalPublishDir = [System.IO.Path]::Combine($parentDir, [char]0x8F6F + [char]0x4EF6 + [char]0x53D1 + [char]0x5E03)

# Use a temp English directory for dotnet publish output to avoid encoding issues
$tempPublishDir = Join-Path $env:TEMP 'App_PublishOutput'

# ── 动态获取项目名称 ──
$csprojFiles = Get-ChildItem -Path $scriptDir -Filter *.csproj
if ($csprojFiles.Count -eq 0) { throw '找不到 .csproj 文件' }
$projectPath = $csprojFiles[0].FullName
$projectName = $csprojFiles[0].BaseName

# ── 停止运行中的实例 ──
Write-Host "Stopping running instances of $projectName and Tuna..."
$runningInstances = Get-Process -Name $projectName, 'Tuna' -ErrorAction SilentlyContinue
foreach ($process in $runningInstances) {
    try {
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        [void]$process.WaitForExit(5000)
    }
    catch {
        Write-Warning ("Unable to stop process {0} ({1}): {2}" -f $process.ProcessName, $process.Id, $_)
    }
}

# Shut down reusable MSBuild/Roslyn servers before removing intermediate files.
# They restart automatically on the next restore/build.
try {
    & dotnet build-server shutdown | Out-Null
}
catch {
    Write-Warning ("Unable to shut down .NET build servers: {0}" -f $_)
}

# Stop only stale NativeAOT/linker processes whose command line points into this
# project. Do not terminate unrelated dotnet/link processes on the machine.
try {
    Get-CimInstance Win32_Process -ErrorAction Stop |
        Where-Object {
            ($_.Name -eq 'ilc.exe' -or $_.Name -eq 'link.exe' -or $_.Name -eq 'dotnet.exe') -and
            $_.CommandLine -and
            $_.CommandLine.IndexOf($scriptDir, [StringComparison]::OrdinalIgnoreCase) -ge 0
        } |
        ForEach-Object {
            Write-Host ("Stopping stale build process {0} ({1})..." -f $_.Name, $_.ProcessId)
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
}
catch {
    Write-Warning ("Unable to inspect stale build processes: {0}" -f $_)
}

function Remove-DirectoryWithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [int]$MaxAttempts = 15
    )

    if (-not (Test-Path -LiteralPath $Path)) { return }

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -eq $MaxAttempts) {
                throw ("Failed to clean '{0}' after {1} attempts. A process is still locking a build file. Last error: {2}" -f $Path, $MaxAttempts, $_)
            }

            Write-Host ("Build output is temporarily locked; retrying cleanup ({0}/{1})..." -f $attempt, $MaxAttempts) -ForegroundColor Yellow
            Start-Sleep -Milliseconds ([Math]::Min(1000, 200 + ($attempt * 100)))
        }
    }
}

# ── 清理旧构建产物 ──
Write-Host 'Cleaning bin/obj...'
Get-ChildItem -LiteralPath $scriptDir -Directory -Force |
    Where-Object { $_.Name -eq 'bin' -or $_.Name -eq 'obj' } |
    ForEach-Object { Remove-DirectoryWithRetry -Path $_.FullName }

# ── 初始化 MSVC C++ 工具链（NativeAOT 需要 link.exe） ──
function Initialize-MsvcToolchain() {
    $vswhereExe = Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhereExe)) {
        $vswhereExe = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    }
    if (-not (Test-Path -LiteralPath $vswhereExe)) {
        throw 'vswhere.exe not found. NativeAOT requires Visual Studio with the C++ workload.'
    }

    $vsInstallPath = & $vswhereExe -latest -property installationPath
    if (-not $vsInstallPath) {
        throw 'Visual Studio installation not found.'
    }

    $vcvars64 = Join-Path $vsInstallPath 'VC\Auxiliary\Build\vcvars64.bat'
    if (-not (Test-Path -LiteralPath $vcvars64)) {
        throw 'vcvars64.bat not found: ' + $vcvars64
    }

    Write-Host ('Initializing C++ toolchain: ' + $vcvars64)

    $envOutput = & cmd /c ('"' + $vcvars64 + '" >nul 2>&1 && set')
    foreach ($line in $envOutput) {
        $idx = $line.IndexOf('=')
        if ($idx -gt 0) {
            $name = $line.Substring(0, $idx)
            $value = $line.Substring($idx + 1)
            [System.Environment]::SetEnvironmentVariable($name, $value, 'Process')
        }
    }

    $linkExe = Get-Command link.exe -ErrorAction SilentlyContinue
    if (-not $linkExe) {
        throw 'link.exe still not on PATH after vcvars64. Check the VS C++ workload installation.'
    }
    Write-Host ('C++ linker ready: ' + $linkExe.Source)
}

Write-Host 'Initializing MSVC toolchain for NativeAOT...'
Initialize-MsvcToolchain

# ── 清理临时发布目录 ──
if (Test-Path $tempPublishDir) {
    Remove-Item -LiteralPath $tempPublishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $tempPublishDir | Out-Null

# (projectPath is already resolved dynamically above)

# ── NuGet 还原 ──
Write-Host 'Restoring NuGet packages...'
& dotnet restore $projectPath -r win-x64
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

# ── NativeAOT 发布到临时目录 ──
Write-Host 'Publishing with NativeAOT (single exe, no IL)...'
$publishArgs = @(
    'publish', $projectPath,
    '-c', 'Release',
    '-r', 'win-x64',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:Configuration=Release',
    '-p:Platform=x64',
    '-p:EmbedNativeForSingleExe=true',
    '-o', $tempPublishDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# ── UPX 压缩函数 ──
function Compress-WithUpx($targetExePath) {
    Write-Host 'Checking for UPX...'
    $upxDir = Join-Path $env:TEMP 'upx_tools'
    $upxExe = Join-Path $upxDir 'upx.exe'
    
    if (-not (Test-Path -LiteralPath $upxExe)) {
        Write-Host 'UPX not found, downloading UPX...'
        try {
            $upxUrl = "https://github.com/upx/upx/releases/download/v4.2.4/upx-4.2.4-win64.zip"
            $zipPath = Join-Path $env:TEMP 'upx.zip'
            
            # Download using Net.WebClient
            $webClient = New-Object System.Net.WebClient
            $webClient.DownloadFile($upxUrl, $zipPath)
            
            Write-Host 'Extracting UPX...'
            Expand-Archive -Path $zipPath -DestinationPath $upxDir -Force
            
            # Find upx.exe in the extracted folder hierarchy
            $extractedExe = Get-ChildItem -Path $upxDir -Filter upx.exe -Recurse | Select-Object -First 1
            if ($extractedExe) {
                $destParent = Split-Path $upxExe
                if (-not (Test-Path -LiteralPath $destParent)) {
                    New-Item -ItemType Directory -Path $destParent -Force | Out-Null
                }
                Copy-Item -LiteralPath $extractedExe.FullName -Destination $upxExe -Force
            }
            
            # Clean up zip
            Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
        }
        catch {
            Write-Warning "Failed to download or extract UPX: $_. Skipping compression."
            return
        }
    }
    
    if (Test-Path -LiteralPath $upxExe) {
        Write-Host 'Running UPX compression on ASCII staging path...'
        # Copy to ASCII staging path to prevent path encoding issues
        $stagingPath = Join-Path $env:TEMP 'Tuna_staging.exe'
        if (Test-Path -LiteralPath $stagingPath) {
            Remove-Item -LiteralPath $stagingPath -Force -ErrorAction SilentlyContinue
        }
        Copy-Item -LiteralPath $targetExePath -Destination $stagingPath -Force
        
        # Execute UPX
        & $upxExe --best --lzma $stagingPath
        
        # Copy compressed exe back
        Copy-Item -LiteralPath $stagingPath -Destination $targetExePath -Force
        
        # Remove staging file
        Remove-Item -LiteralPath $stagingPath -Force -ErrorAction SilentlyContinue
        Write-Host 'UPX compression completed!' -ForegroundColor Green
    } else {
        Write-Warning 'UPX.exe not found. Skipping compression.'
    }
}

$exeFiles = Get-ChildItem -Path $tempPublishDir -Filter *.exe
if ($exeFiles.Count -eq 0) { throw 'Published exe not found.' }
$tempExe = $exeFiles[0].FullName
$realExeName = $exeFiles[0].BaseName

# Run UPX compression on the target exe before publishing
Compress-WithUpx $tempExe

# ── 清理最终发布目录，只放 exe ──
# Re-resolve these values after UPX. This avoids relying on variables that may be
# overwritten by external tools or a caller's PowerShell scope.
$stagedExe = Get-ChildItem -LiteralPath $tempPublishDir -Filter '*.exe' -File |
    Select-Object -First 1
if ($null -eq $stagedExe) {
    throw ('Published exe not found after UPX: ' + $tempPublishDir)
}

$resolvedParentDir = Split-Path $scriptDir -Parent
$releaseFolderName = -join @(
    [char]0x8F6F,
    [char]0x4EF6,
    [char]0x53D1,
    [char]0x5E03
)
$resolvedFinalPublishDir = Join-Path -Path $resolvedParentDir -ChildPath $releaseFolderName
if ([string]::IsNullOrWhiteSpace($resolvedFinalPublishDir)) {
    throw 'Unable to resolve the final publish directory.'
}

if (Test-Path -LiteralPath $resolvedFinalPublishDir) {
    Remove-DirectoryWithRetry -Path $resolvedFinalPublishDir
}
New-Item -ItemType Directory -Path $resolvedFinalPublishDir -Force | Out-Null

$publishedExePath = Join-Path -Path $resolvedFinalPublishDir -ChildPath $stagedExe.Name
Write-Host ('Copying published exe to: ' + $publishedExePath)
Copy-Item -LiteralPath $stagedExe.FullName -Destination $publishedExePath -Force -ErrorAction Stop
if (-not (Test-Path -LiteralPath $publishedExePath -PathType Leaf)) {
    throw ('Published exe copy failed: ' + $publishedExePath)
}

# ── 清理临时目录 ──
Remove-DirectoryWithRetry -Path $tempPublishDir

# ── 报告最终大小 ──
$exeInfo = Get-Item -LiteralPath $publishedExePath
$sizeMB = [math]::Round($exeInfo.Length / 1MB, 2)
Write-Host ''
Write-Host '========================================='
Write-Host ('Publish completed: ' + $publishedExePath)
Write-Host ('Size: ' + $sizeMB + ' MB')
Write-Host '========================================='

if ($sizeMB -lt 10) {
    Write-Host 'Under 10MB target!' -ForegroundColor Green
} else {
    Write-Host ('Size is ' + $sizeMB + 'MB, above 10MB target.') -ForegroundColor Yellow
}

# ── 启动测试 ──
if ($env:NO_AUTO_START -ne '1') {
    Write-Host ('Starting ' + $stagedExe.BaseName + '...')
    Start-Process -FilePath $publishedExePath -WorkingDirectory $resolvedFinalPublishDir | Out-Null
}
