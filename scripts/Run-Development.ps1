$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\PrintMonitor\PrintMonitor.csproj"
$output = Join-Path $PSScriptRoot "..\captured_jobs"
dotnet run --project $project -- --output $output @args
