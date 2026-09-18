$ErrorActionPreference = 'Stop'
$ProjectDirectory = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $ProjectDirectory 'src\PrintSaveApp\PrintSaveApp.csproj') -- @args
