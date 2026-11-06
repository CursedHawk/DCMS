# Installs dcms, the DCMS command line (ADR 0032), from the console that served this script:
#   irm __DCMS_ORIGIN__/downloads/install.ps1 | iex
# Windows, x64 (arm64 runs it too). Puts it in $env:DCMS_INSTALL_DIR, else %LOCALAPPDATA%\dcms.
$ErrorActionPreference = 'Stop'
$server = '__DCMS_ORIGIN__'
$dir = if ($env:DCMS_INSTALL_DIR) { $env:DCMS_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'dcms' }
# One Windows build: x64, which Windows on Arm runs too.
$name = 'dcms-win-x64.exe'

New-Item -ItemType Directory -Force -Path $dir | Out-Null
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("dcms-" + [guid]::NewGuid() + '.exe')
try {
    Invoke-WebRequest -UseBasicParsing -Uri "$server/downloads/$name" -OutFile $tmp
    $sums = (Invoke-WebRequest -UseBasicParsing -Uri "$server/downloads/SHA256SUMS").Content
    if ($sums -is [byte[]]) { $sums = [System.Text.Encoding]::UTF8.GetString($sums) }
    $expected = ($sums -split "`n" | ForEach-Object { $p = $_.Trim() -split '\s+', 2; if ($p.Length -eq 2 -and $p[1].TrimStart('*') -eq $name) { $p[0] } }) | Select-Object -First 1
    $actual = (Get-FileHash -Algorithm SHA256 -Path $tmp).Hash.ToLowerInvariant()
    if (-not $expected -or $expected.ToLowerInvariant() -ne $actual) {
        throw "The download is not what $server's SHA256SUMS says; not installed."
    }
    $target = Join-Path $dir 'dcms.exe'
    # A running dcms.exe cannot be overwritten, but it can be renamed out of the way.
    if (Test-Path $target) { Move-Item -Force $target "$target.old" }
    Move-Item -Force $tmp $target
} finally {
    if (Test-Path $tmp) { Remove-Item -Force $tmp }
}

$path = [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not (($path -split ';') -contains $dir)) {
    [Environment]::SetEnvironmentVariable('Path', ($(if ($path) { "$path;" } else { '' }) + $dir), 'User')
    $env:Path = "$env:Path;$dir"
    Write-Host "Added $dir to your PATH (open a new terminal for other windows to see it)."
}
Write-Host "Installed dcms $(& (Join-Path $dir 'dcms.exe') --version) to $dir."
Write-Host "Next: dcms login --server $server"
