# MSIX を作り、開発用証明書(CN=beruk)で署名する。-Install を付けると入れ直して起動する。
# 使い方: powershell -ExecutionPolicy Bypass -File scripts\build-msix.ps1 [-Platform x64] [-Install]
# XAML コンパイラはパスが長いと失敗するので、短いパスに置いて実行する。
param(
    [string]$Platform = 'x64',
    [string]$Thumbprint = 'B71DE0A71072E7DB16963C296CFFE74E8EF9F39A',
    [switch]$Install)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
$signtool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
$dist = Join-Path $root 'dist'

if (Test-Path $dist) { Get-ChildItem $dist -Recurse -Filter *.msix | ForEach-Object { [IO.File]::Delete($_.FullName) } }
New-Item -ItemType Directory -Force $dist | Out-Null

# 同じバージョンのパッケージは入れ直せないので、リビジョン(4つ目の数字)を毎回上げる。
# 番号は dist\.devrev に記録し、マニフェストはビルド後に元へ戻す。
$manifestPath = Join-Path $root 'src\CompCalibrator.App\Package.appxmanifest'
$original = [IO.File]::ReadAllText($manifestPath)
$revFile = Join-Path $dist '.devrev'
$rev = if (Test-Path $revFile) { [int](Get-Content $revFile) + 1 } else { 1 }
Set-Content $revFile $rev
$patched = [regex]::Replace($original, '(<Identity[^>]*Version="\d+\.\d+\.\d+\.)\d+"', "`${1}$rev`"")
[IO.File]::WriteAllText($manifestPath, $patched, (New-Object Text.UTF8Encoding($false)))
try {
    & $msbuild (Join-Path $root 'src\CompCalibrator.App\CompCalibrator.App.csproj') -restore -t:Build `
        -p:Configuration=Release -p:Platform=$Platform -p:BuildMsix=true `
        -p:GenerateAppxPackageOnBuild=true "-p:AppxPackageDir=$dist\" -v:m -nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }
}
finally {
    [IO.File]::WriteAllText($manifestPath, $original, (New-Object Text.UTF8Encoding($false)))
}

$msix = Get-ChildItem $dist -Recurse -Filter *.msix | Sort-Object LastWriteTime -Descending | Select-Object -First 1
& $signtool sign /fd SHA256 /sha1 $Thumbprint /s My $msix.FullName
if ($LASTEXITCODE -ne 0) { throw "sign failed ($LASTEXITCODE)" }
Write-Host "built: $($msix.FullName)"

if ($Install) {
    Get-Process CompCalibrator -ErrorAction SilentlyContinue | Stop-Process -Force
    Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown
    $aumid = (Get-StartApps | Where-Object AppID -like 'beruk.CompCalibrator*').AppID
    Start-Process "shell:AppsFolder\$aumid"
    Write-Host "installed and started"
}
