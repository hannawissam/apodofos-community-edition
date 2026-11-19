# Builds and tests the whole solution (requires the .NET 11 SDK).
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet build QuietLoad.slnx -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test QuietLoad.slnx -c $Configuration --no-build
exit $LASTEXITCODE
