param(
    [ValidateRange(1, 65535)]
    [int]$Port = 8080
)
$ErrorActionPreference = 'Stop'
$pythonCommand = Get-Command python -ErrorAction SilentlyContinue
if (-not $pythonCommand) {
    Write-Error 'Python 3 is required. Install Python and add it to PATH.'
    exit 1
}
Write-Host "Local: http://localhost:$Port"
try {
    $addresses = Get-NetIPAddress -AddressFamily IPv4 | Where-Object {
        $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' -and $_.AddressState -eq 'Preferred'
    } | Select-Object -ExpandProperty IPAddress -Unique
    foreach ($address in $addresses) { Write-Host "LAN:   http://${address}:$Port" }
} catch {
    Write-Warning 'Could not list LAN addresses. Use ipconfig to find your Wi-Fi or Ethernet IPv4 address.'
}
Write-Host 'Starting server (0.0.0.0). Wait for the Serving message. Press Ctrl+C to stop.'
& $pythonCommand.Source (Join-Path $PSScriptRoot 'serve.py') --port $Port
exit $LASTEXITCODE
