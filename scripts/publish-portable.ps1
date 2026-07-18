param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repositoryRoot 'src\NoGaReader\NoGaReader.csproj'
$outputPath = Join-Path $repositoryRoot 'artifacts\publish\win-x64'

dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained false `
    --output $outputPath

Write-Host "Portable build: $outputPath"


# After publish, users can register current-user file associations from the app sidebar:
# "文件关联" button calls FileAssociationService.RegisterCurrentUser.
