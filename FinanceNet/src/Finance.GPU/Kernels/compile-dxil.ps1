# Compiles finance.hlsl to DXIL (shader model 6.0) for the Direct3D 12 engine and records the source hash.
# Run after editing finance.hlsl:  powershell -File src/Finance.GPU/Kernels/compile-dxil.ps1  (Windows PowerShell 5.1 or pwsh)
# The DXC compiler is taken from the Microsoft.Direct3D.DXC NuGet package (downloaded once into obj/).
# Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$version = '1.8.2505.32'
$tools = Join-Path $here "../obj/dxc-$version"
$dxc = Join-Path $tools 'build/native/bin/x64/dxc.exe'
if (-not (Test-Path $dxc)) {
    New-Item -ItemType Directory -Force $tools | Out-Null
    $pkg = Join-Path $tools 'dxc.zip'
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.direct3d.dxc/$version/microsoft.direct3d.dxc.$version.nupkg" -OutFile $pkg
    Expand-Archive $pkg -DestinationPath $tools -Force
}

$src = Join-Path $here 'finance.hlsl'
$out = Join-Path $here 'dxil'
New-Item -ItemType Directory -Force $out | Out-Null
foreach ($entry in 'mc_european', 'mc_asian', 'black_scholes', 'portfolio_return') {
    & $dxc -nologo -T cs_6_0 -E $entry -O3 -WX -HV 2021 -Fo (Join-Path $out "$entry.dxil") $src
    if ($LASTEXITCODE -ne 0) { throw "dxc failed for $entry" }
}
# Hash of the source with line endings normalised, checked by Finance.Native.Tests so stale DXIL is caught.
$text = [IO.File]::ReadAllText($src).Replace("`r`n", "`n")
$sha = [Security.Cryptography.SHA256]::Create()
$hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).Replace("-", "")
Set-Content -NoNewline -Encoding ascii (Join-Path $out 'finance.hlsl.sha256') $hash
Write-Host "DXIL written to $out ($hash)"
