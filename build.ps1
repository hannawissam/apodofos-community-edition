# Builds and tests the whole solution (requires the .NET 11 SDK).
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet build Apodofos.slnx -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test Apodofos.slnx -c $Configuration --no-build
exit $LASTEXITCODE
