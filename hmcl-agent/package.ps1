$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $MyInvocation.MyCommand.Path)

$buildDir = Join-Path $PWD "build"
$classesDir = Join-Path $buildDir "classes"
$jarPath = Join-Path $buildDir "hmcl-agent.jar"
$manifestFile = Join-Path $PWD "MANIFEST.MF"

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression

if (Test-Path $jarPath) { Remove-Item $jarPath -Force }

# 用 ZipArchive 直接创建 JAR
$fs = [System.IO.File]::Open($jarPath, [System.IO.FileMode]::Create)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        # 1. 添加 MANIFEST
        $manifestEntry = $archive.CreateEntry("META-INF/MANIFEST.MF")
        $ms = $manifestEntry.Open()
        try {
            $manifestContent = Get-Content $manifestFile -Raw
            $enc = New-Object System.Text.UTF8Encoding($false)
            $bytes = $enc.GetBytes($manifestContent)
            $ms.Write($bytes, 0, $bytes.Length)
        } finally { $ms.Dispose() }

        # 2. 添加 class 文件
        Get-ChildItem $classesDir -Recurse -File | ForEach-Object {
            $relative = $_.FullName.Substring($classesDir.Length + 1)
            $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            try {
                $fileStream = [System.IO.File]::OpenRead($_.FullName)
                try { $fileStream.CopyTo($entryStream) } finally { $fileStream.Dispose() }
            } finally { $entryStream.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $fs.Dispose() }

Write-Host "Done: $jarPath ($((Get-Item $jarPath).Length) bytes)" -ForegroundColor Green
