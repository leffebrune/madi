[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [ValidateSet("win-x64")]
    [string] $RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "src/Madi.GUI/Madi.GUI.csproj"
$configurationDirectory = if ($Configuration -eq "Debug") { "debug" } else { "release" }
$outputPath = Join-Path $PSScriptRoot "artifacts/publish/madi-gui/$configurationDirectory/$RuntimeIdentifier"
$publishRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "artifacts/publish/madi-gui"))
$resolvedOutputPath = [System.IO.Path]::GetFullPath($outputPath)

if (-not $resolvedOutputPath.StartsWith($publishRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear a publish path outside the Madi GUI artifact directory: $resolvedOutputPath"
}

if (Test-Path -LiteralPath $resolvedOutputPath) {
    Remove-Item -LiteralPath $resolvedOutputPath -Recurse -Force
}

$publishArguments = @(
    "publish",
    $projectPath,
    "--configuration", $Configuration,
    "--runtime", $RuntimeIdentifier,
    "--self-contained", "true",
    "--output", $outputPath,
    "-p:TreatWarningsAsErrors=true"
)

dotnet @publishArguments

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if ($Configuration -eq "Release") {
    Get-ChildItem -LiteralPath $outputPath -File -Filter "*.pdb" |
        Remove-Item -Force
}

$payloadFiles = @(Get-ChildItem -LiteralPath $outputPath -File)
if ($Configuration -eq "Release" -and
    ($payloadFiles.Count -ne 1 -or $payloadFiles[0].Extension -ne ".exe")) {
    $names = $payloadFiles.Name -join ", "
    throw "Expected one distributable EXE, found: $names"
}

Write-Host "Published Madi.GUI to: $outputPath"
