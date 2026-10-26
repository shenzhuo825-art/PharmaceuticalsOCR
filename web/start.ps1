param(
    [ValidateRange(1, 65535)]
    [int]$Port = 8080
)
$ErrorActionPreference = 'Stop'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCommand) {
    Write-Error '.NET 10 SDK is required. Install the SDK and add it to PATH.'
    exit 1
}
$project = Join-Path $PSScriptRoot '../api/PrescriptionScanner/PrescriptionScanner.csproj'
$artifacts = Join-Path ([System.IO.Path]::GetTempPath()) 'PharmaceuticalsOCR-build'
& $dotnetCommand.Source build $project --artifacts-path $artifacts --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Local: http://localhost:$Port"
try {
    $addresses = Get-NetIPAddress -AddressFamily IPv4 | Where-Object {
        $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' -and $_.AddressState -eq 'Preferred'
    } | Select-Object -ExpandProperty IPAddress -Unique
    foreach ($address in $addresses) { Write-Host "LAN:   http://${address}:$Port" }
} catch {
    Write-Warning 'Could not list LAN addresses. Use ipconfig to find your Wi-Fi or Ethernet IPv4 address.'
}
Write-Host 'Starting server (0.0.0.0). Wait for the Now listening message. Press Ctrl+C to stop.'
$dll = Join-Path $artifacts 'bin/PrescriptionScanner/debug/PrescriptionScanner.dll'
& $dotnetCommand.Source $dll --serve --urls "http://0.0.0.0:$Port" --web-root $PSScriptRoot
exit $LASTEXITCODE
