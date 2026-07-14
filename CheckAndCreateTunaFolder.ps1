$ErrorActionPreference = 'Stop'

# Assembling "北京林或西" from char codes to prevent any encoding/code page issues
$folderName = [string][char]21271 + [char]20140 + [char]26519 + [char]25110 + [char]35199
$subPath = "Documents\Quicker\$folderName\Tuna"
$targetPath = Join-Path $env:USERPROFILE $subPath

# Check if the folder exists
if (Test-Path -LiteralPath $targetPath) {
    Write-Output "True"
} else {
    try {
        # Create directory and all missing parent directories
        New-Item -ItemType Directory -Path $targetPath -Force | Out-Null
        Write-Output "True"
    } catch {
        Write-Output "False"
        Write-Error "Error creating directory: $_"
    }
}
