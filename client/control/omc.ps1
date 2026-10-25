<#
    omc - OpenMesh Gaming client control helper (PowerShell)

    Drive the Windows client over its loopback control channel.

    .\omc.ps1 status
    .\omc.ps1 log
    .\omc.ps1 connect
    .\omc.ps1 disconnect
    .\omc.ps1 cancel
    .\omc.ps1 ping
    .\omc.ps1 service
    .\omc.ps1 settings server=192.168.31.96 port=8080 token=XXXX
    .\omc.ps1 quit
#>
param(
    [Parameter(Position = 0)]
    [string]$Command = 'status',

    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Rest
)

$ErrorActionPreference = 'Stop'
$port = if ($env:OPENMESH_CONTROL_PORT) { $env:OPENMESH_CONTROL_PORT } else { 17871 }
$tokenFile = Join-Path $env:APPDATA 'OpenMeshGaming\control_token.txt'

if (-not (Test-Path $tokenFile)) {
    Write-Error "control token not found at $tokenFile - start the client first"
    exit 1
}
$token = (Get-Content $tokenFile -Raw).Trim()

$method = 'GET'
$path = '/status'
$body = $null

switch ($Command.ToLower()) {
    'status'     { $path = '/status' }
    'log'        { $path = '/log' }
    'token'      { $path = '/token' }
    'connect'    { $method = 'POST'; $path = '/connect' }
    'disconnect' { $method = 'POST'; $path = '/disconnect' }
    'cancel'     { $method = 'POST'; $path = '/cancel' }
    'ping'       { $method = 'POST'; $path = '/ping' }
    'service'    { $method = 'POST'; $path = '/service'; $body = 'action=' + $(if ($Rest) { $Rest[0] } else { 'toggle' }) }
    'settings'   { $method = 'POST'; $path = '/settings'; $body = ($Rest -join '&') }
    'quit'       { $method = 'POST'; $path = '/quit' }
    default {
        Write-Error "unknown command: $Command"
        Get-Help $MyInvocation.MyCommand.Name
        exit 2
    }
}

$headers = @{ 'X-OpenMesh-Control' = $token }
$uri = "http://127.0.0.1:$port$path"

if ($method -eq 'POST') {
    if ($body) {
        Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -Body $body -ContentType 'application/x-www-form-urlencoded'
    } else {
        Invoke-RestMethod -Uri $uri -Method Post -Headers $headers
    }
} else {
    Invoke-RestMethod -Uri $uri -Method Get -Headers $headers
}
