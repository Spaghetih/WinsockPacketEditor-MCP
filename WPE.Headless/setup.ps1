# WPE.Headless - Windows install script for Claude Code (MCP)
#
# Steps:
#   1. Restore NuGet packages (downloads nuget.exe if missing)
#   2. Build the .NET 4.8 solution in Release via MSBuild
#      (WPELibrary, WinsockPacketEditor, WPE.Headless.Inject, WPE.Headless.Host)
#   3. Copy WPE.Headless.Inject.dll + EasyHook* next to the host exe
#   4. Install and build the TypeScript MCP server
#   5. Write WPE.Headless/mcp-server/.env with WPE_HOST_EXE = absolute path
#   6. Generate ../.mcp.json with absolute paths for Claude Code (project scope)
#
# Usage (from repo root):
#   powershell -ExecutionPolicy Bypass -File .\WPE.Headless\setup.ps1
# Compatible with Windows PowerShell 5.1 (powershell.exe) and PowerShell 7+ (pwsh).
#
# This file is intentionally ASCII-only: PowerShell 5.1 reads .ps1 as ANSI when
# there is no UTF-8 BOM, which corrupts non-ASCII characters.

[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn2($m)  { Write-Host "    $m" -ForegroundColor Yellow }

function Get-CommandPath([string]$Name) {
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

# ----- paths -----
$Root      = (Resolve-Path "$PSScriptRoot\..").Path
$Solution  = Join-Path $Root "WinSockPacketEditor.sln"
$HostCsproj = Join-Path $Root "WPE.Headless\WPE.Headless.Host\WPE.Headless.Host.csproj"
$Headless  = Join-Path $Root "WPE.Headless"
$HostProj  = Join-Path $Headless "WPE.Headless.Host"
$HostBin   = Join-Path $HostProj "bin\$Configuration"
$HostExe   = Join-Path $HostBin  "WPE.Headless.Host.exe"
$InjectBin = Join-Path $Headless "WPE.Headless.Inject\bin\$Configuration"
$WPELibBin = Join-Path $Root "WPELibrary\bin\$Configuration"
$McpDir    = Join-Path $Headless "mcp-server"
$McpEntry  = Join-Path $McpDir   "dist\index.js"
$McpJson   = Join-Path $Root     ".mcp.json"

if (!(Test-Path $Solution)) { throw "Solution not found: $Solution" }

# ----- 1. nuget restore -----
Write-Step "Restoring NuGet packages"
$Nuget = Get-CommandPath "nuget"
if (-not $Nuget) {
    $Nuget = Join-Path $env:TEMP "nuget.exe"
    if (-not (Test-Path $Nuget)) {
        Write-Warn2 "nuget.exe not found, downloading to $Nuget"
        # PS 5.1 defaults to TLS 1.0 - dist.nuget.org requires TLS 1.2
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile $Nuget -UseBasicParsing
    }
}
& $Nuget restore $Solution
if ($LASTEXITCODE -ne 0) { throw "nuget restore failed" }
Write-Ok "OK"

function Find-MSBuild {
    # 1. PATH
    $p = Get-CommandPath "msbuild.exe"
    if (-not $p) { $p = Get-CommandPath "msbuild" }
    if ($p) { return $p }

    # 2. vswhere (any product, any prerelease, just needs MSBuild)
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $hits = & $vswhere -prerelease -products * -latest `
            -requires Microsoft.Component.MSBuild `
            -find "MSBuild\**\Bin\MSBuild.exe" 2>$null
        if ($hits) { return ($hits | Select-Object -First 1) }
        # widen: any MSBuild without requires
        $hits = & $vswhere -prerelease -products * -all `
            -find "MSBuild\**\Bin\MSBuild.exe" 2>$null
        if ($hits) { return ($hits | Select-Object -First 1) }
    }

    # 3. Brute-force scan of common install roots (VS 2017..2026 + BuildTools)
    $roots = @(
        "${env:ProgramFiles}\Microsoft Visual Studio",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio"
    )
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $found = Get-ChildItem -Path $root -Recurse -Filter "MSBuild.exe" `
            -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\MSBuild\\(Current|\d+\.0)\\Bin\\MSBuild\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

# ----- 2. msbuild -----
# We build only WPE.Headless.Host.csproj (which transitively builds WPELibrary
# and WPE.Headless.Inject). This skips the WinForms shell project
# WinsockPacketEditor.csproj, which fails with MSB3323 unless a ClickOnce
# signing certificate is installed - and we don't need its UI for the headless
# MCP path.
Write-Step "Building $HostCsproj ($Configuration)"
$MsBuild = Find-MSBuild
if (-not $MsBuild) {
    throw "MSBuild not found. Install Visual Studio 2019/2022/2026 (workload 'Desktop development with .NET') or the Build Tools (https://aka.ms/vs/17/release/vs_BuildTools.exe), then re-run."
}
Write-Ok "Using $MsBuild"
& $MsBuild $HostCsproj "/p:Configuration=$Configuration" "/p:Platform=AnyCPU" /m /nologo /v:minimal /restore
if ($LASTEXITCODE -ne 0) { throw "msbuild failed" }
Write-Ok "OK"

if (!(Test-Path $HostExe)) { throw "Build succeeded but $HostExe is missing" }

# ----- 3. copy inject + EasyHook next to host exe -----
Write-Step "Copying native dependencies next to host exe"
foreach ($f in @(
    "WPE.Headless.Inject.dll",
    "WPE.Headless.Inject.pdb"
)) {
    $src = Join-Path $InjectBin $f
    if (Test-Path $src) { Copy-Item $src $HostBin -Force }
}
foreach ($f in @(
    "EasyHook32.dll","EasyHook64.dll","EasyHook32Svc.exe","EasyHook64Svc.exe",
    "EasyLoad32.dll","EasyLoad64.dll"
)) {
    $src = Join-Path $WPELibBin $f
    if (Test-Path $src) { Copy-Item $src $HostBin -Force }
    else {
        $alt = Join-Path $Root "WPELibrary\$f"
        if (Test-Path $alt) { Copy-Item $alt $HostBin -Force }
    }
}
Write-Ok "OK ($HostBin)"

# ----- 4. MCP server (npm install + build) -----
Write-Step "Installing and building the TypeScript MCP server"
Push-Location $McpDir
try {
    $npm = Get-CommandPath "npm.cmd"
    if (-not $npm) { $npm = Get-CommandPath "npm" }
    if (-not $npm) { throw "npm not found. Install Node.js 18+ from https://nodejs.org/" }
    & $npm install --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
    & $npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build failed" }
} finally { Pop-Location }
if (!(Test-Path $McpEntry)) { throw "TS build succeeded but $McpEntry is missing" }
Write-Ok "OK"

# ----- 5. write .env -----
Write-Step "Writing $McpDir\.env"
$envContent = "WPE_HOST_EXE=$HostExe`n"
Set-Content -Path (Join-Path $McpDir ".env") -Value $envContent -Encoding ASCII
Write-Ok "OK"

# ----- 6. .mcp.json for Claude Code -----
Write-Step "Generating $McpJson (Claude Code MCP config)"
$mcpConfig = [ordered]@{
    mcpServers = [ordered]@{
        "wpe-headless" = [ordered]@{
            command = "node"
            args    = @($McpEntry)
            env     = [ordered]@{
                WPE_HOST_EXE = $HostExe
            }
        }
    }
}
$mcpConfig | ConvertTo-Json -Depth 6 | Set-Content -Path $McpJson -Encoding ASCII
Write-Ok "OK"

# ----- summary -----
Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host " Install complete." -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host ""
Write-Host " Host exe : $HostExe"
Write-Host " MCP js   : $McpEntry"
Write-Host " .mcp.json: $McpJson  (project scope, auto-detected by Claude Code)"
Write-Host ""
Write-Host " Manual Claude Code wiring (alternative):"
Write-Host "   claude mcp add wpe-headless --scope user node `"$McpEntry`""
Write-Host ""
Write-Host " Test:"
Write-Host "   claude mcp list"
Write-Host "   claude mcp get wpe-headless"
Write-Host ""
