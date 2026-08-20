[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "src/Madi.TUI/Madi.TUI.csproj"
$configurationDirectory = if ($Configuration -eq "Debug") { "debug" } else { "release" }
$outputPath = Join-Path $PSScriptRoot "artifacts/publish/madi-tui/$configurationDirectory"

if (![string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $outputPath = Join-Path $outputPath $RuntimeIdentifier
}

$publishArguments = @(
    "publish",
    $projectPath,
    "--configuration", $Configuration,
    "--self-contained", "true",
    "--output", $outputPath,
    "-p:TreatWarningsAsErrors=true",
    "-p:IlcTreatWarningsAsErrors=true",
    "-p:ILLinkTreatWarningsAsErrors=true"
)

if (![string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $publishArguments += @("--runtime", $RuntimeIdentifier)
}

dotnet @publishArguments

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host "Published Madi.TUI to: $outputPath"
