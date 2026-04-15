param(
    [switch]$NoRuntime
)

$projectPath = Join-Path $PSScriptRoot "..\RdlxMcpServer.csproj"

if ($NoRuntime) {
    dotnet run --project $projectPath -- --self-test-no-runtime
}
else {
    dotnet run --project $projectPath -- --self-test
}

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
