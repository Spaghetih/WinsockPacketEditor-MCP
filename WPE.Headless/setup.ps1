# WPE.Headless — script d'installation Windows pour Claude Code (MCP)
#
# Étapes :
#   1. Restaure les packages NuGet (télécharge nuget.exe si absent)
#   2. Compile la solution .NET 4.8 en Release (WPELibrary, WinsockPacketEditor,
#      WPE.Headless.Inject, WPE.Headless.Host) via MSBuild
#   3. Copie WPE.Headless.Inject.dll + EasyHook* à côté du host exe
#   4. Installe et compile le serveur MCP TypeScript
#   5. Écrit WPE.Headless/mcp-server/.env avec WPE_HOST_EXE = chemin absolu
#   6. Met à jour ../.mcp.json avec les chemins absolus pour Claude Code
#
# Utilisation (depuis la racine du dépôt) :
#   powershell -ExecutionPolicy Bypass -File .\WPE.Headless\setup.ps1
# Compatible Windows PowerShell 5.1 et PowerShell 7+.

[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn2($m)  { Write-Host "    $m" -ForegroundColor Yellow }

# ----- chemins -----
$Root      = (Resolve-Path "$PSScriptRoot\..").Path
$Solution  = Join-Path $Root "WinSockPacketEditor.sln"
$Headless  = Join-Path $Root "WPE.Headless"
$HostProj  = Join-Path $Headless "WPE.Headless.Host"
$HostBin   = Join-Path $HostProj "bin\$Configuration"
$HostExe   = Join-Path $HostBin  "WPE.Headless.Host.exe"
$InjectBin = Join-Path $Headless "WPE.Headless.Inject\bin\$Configuration"
$WPELibBin = Join-Path $Root "WPELibrary\bin\$Configuration"
$McpDir    = Join-Path $Headless "mcp-server"
$McpEntry  = Join-Path $McpDir   "dist\index.js"
$McpJson   = Join-Path $Root     ".mcp.json"

if (!(Test-Path $Solution)) { throw "Solution introuvable : $Solution" }

function Get-CommandPath([string]$Name) {
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

# ----- 1. nuget restore -----
Write-Step "Restauration des packages NuGet"
$Nuget = Get-CommandPath "nuget"
if (-not $Nuget) {
    $Nuget = Join-Path $env:TEMP "nuget.exe"
    if (-not (Test-Path $Nuget)) {
        Write-Warn2 "nuget.exe non installé : téléchargement dans $Nuget"
        # TLS 1.2 obligatoire sur Windows PowerShell 5.1 pour dist.nuget.org
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile $Nuget -UseBasicParsing
    }
}
& $Nuget restore $Solution
if ($LASTEXITCODE -ne 0) { throw "nuget restore a échoué" }
Write-Ok "OK"

# ----- 2. msbuild -----
Write-Step "Compilation de $Solution ($Configuration)"
$MsBuild = Get-CommandPath "msbuild"
if (-not $MsBuild) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $MsBuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
    }
}
if (-not $MsBuild) {
    throw "MSBuild introuvable. Installez Visual Studio 2019/2022 (workload .NET desktop) ou les Build Tools (https://aka.ms/vs/17/release/vs_BuildTools.exe — workload 'Outils de génération .NET desktop'), puis relancez."
}
& $MsBuild $Solution "/p:Configuration=$Configuration" "/p:Platform=Any CPU" /m /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw "msbuild a échoué" }
Write-Ok "OK"

if (!(Test-Path $HostExe)) { throw "Build OK mais $HostExe absent" }

# ----- 3. copier inject + EasyHook à côté de l'exe host -----
Write-Step "Copie des dépendances natives à côté du host"
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
Write-Step "Installation et build du serveur MCP TypeScript"
Push-Location $McpDir
try {
    $npm = Get-CommandPath "npm.cmd"
    if (-not $npm) { $npm = Get-CommandPath "npm" }
    if (-not $npm) { throw "npm introuvable. Installez Node.js 18+ depuis https://nodejs.org/" }
    & $npm install --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "npm install a échoué" }
    & $npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build a échoué" }
} finally { Pop-Location }
if (!(Test-Path $McpEntry)) { throw "Build TS OK mais $McpEntry absent" }
Write-Ok "OK"

# ----- 5. écrire .env -----
Write-Step "Écriture de $McpDir\.env"
$envContent = "WPE_HOST_EXE=$HostExe`n"
Set-Content -Path (Join-Path $McpDir ".env") -Value $envContent -Encoding UTF8
Write-Ok "OK"

# ----- 6. .mcp.json prêt pour Claude Code -----
Write-Step "Génération de $McpJson (configuration MCP de Claude Code)"
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
$mcpConfig | ConvertTo-Json -Depth 6 | Set-Content -Path $McpJson -Encoding UTF8
Write-Ok "OK"

# ----- résumé -----
Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host " Installation terminée." -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host ""
Write-Host " Host exe : $HostExe"
Write-Host " MCP js   : $McpEntry"
Write-Host " .mcp.json: $McpJson  (project-scoped, détecté automatiquement par Claude Code)"
Write-Host ""
Write-Host " Brancher manuellement à Claude Code (alternative) :"
Write-Host "   claude mcp add wpe-headless --scope user node `"$McpEntry`""
Write-Host ""
Write-Host " Tester :"
Write-Host "   claude mcp list"
Write-Host "   claude mcp get wpe-headless"
Write-Host ""
