<#
.SYNOPSIS
  Compile ClaudeDeck depuis les sources, sur votre machine.

.DESCRIPTION
  Alternative au téléchargement d'un binaire : le code est lisible dans ce dépôt et l'exécutable est produit
  localement (pas de « Mark of the Web », donc pas de blocage SmartScreen lié au téléchargement).
  Prérequis : SDK .NET 8  ->  winget install Microsoft.DotNet.SDK.8

.PARAMETER Variant
  light    (défaut) : ~14 Mo, nécessite le .NET 8 Desktop Runtime (installé avec le SDK)
  portable          : ~160 Mo, runtime .NET embarqué, aucun prérequis sur la machine cible

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build.ps1
  .\publish\claude-deck.exe
#>
param(
    [ValidateSet("light", "portable")] [string] $Variant = "light",
    [string] $Output = "publish",
    [switch] $SkipTests
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "SDK .NET 8 introuvable. Installez-le avec : winget install Microsoft.DotNet.SDK.8"
}

if (-not $SkipTests) {
    dotnet test tests/ClaudeDeck.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests en échec." }
}

$selfContained = if ($Variant -eq "portable") { "true" } else { "false" }
dotnet publish src/ClaudeDeck -c Release -p:SelfContained=$selfContained -o $Output
if ($LASTEXITCODE -ne 0) { throw "Publication en échec." }

$exe = Join-Path (Resolve-Path $Output) "claude-deck.exe"
Write-Host ""
Write-Host "ClaudeDeck ($Variant) compilé : $exe" -ForegroundColor Green
Write-Host "SHA-256 : $((Get-FileHash $exe -Algorithm SHA256).Hash)"
