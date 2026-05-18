param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\SyncWaveAudio\SyncWaveAudio.csproj"
$output = Join-Path $root "artifacts\publish\SyncWaveAudio"

dotnet restore $project
dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -o $output

Write-Host "Published SyncWave Audio to $output"
