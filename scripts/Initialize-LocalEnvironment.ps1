# Creates local secret configuration only. Does not invoke Docker, SQL or the API.
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$target = Join-Path $repository '.env'
if (Test-Path -LiteralPath $target) { throw '.env already exists. Inspect/edit it; this script will not overwrite it.' }

function New-RandomBytes {
    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    return ,$bytes
}
function New-HexSecret { return [BitConverter]::ToString((New-RandomBytes)).Replace('-', '') }
$sqlPassword = 'Dev1!' + (New-HexSecret)
$redisPassword = New-HexSecret
$rabbitPassword = New-HexSecret
$jwtKey = [Convert]::ToBase64String((New-RandomBytes))
$settings = @(
    '# Generated local-only secrets. Do not commit/share this file.',
    "MSSQL_SA_PASSWORD='$sqlPassword'",
    "API_SQL_CONNECTION_STRING='Server=sqlserver,1433;Database=MyOnlineShop;User ID=sa;Password=$sqlPassword;Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=False'",
    "JWT_SIGNING_KEY_BASE64='$jwtKey'",
    "REDIS_PASSWORD='$redisPassword'",
    'RABBITMQ_USERNAME=shop_local',
    "RABBITMQ_PASSWORD='$rabbitPassword'",
    'API_PORT=8080', 'SQL_PORT=14333', 'RABBITMQ_MANAGEMENT_PORT=15672',
    'ASPNETCORE_ENVIRONMENT=Development', 'REDIS_ENABLED=true', 'MESSAGING_ENABLED=false'
)
# CreateNew also protects against an existing file appearing between the check and write.
$stream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $writer = New-Object IO.StreamWriter($stream, (New-Object Text.UTF8Encoding($false)))
    try { $writer.WriteLine(($settings -join [Environment]::NewLine)) } finally { $writer.Dispose() }
}
finally { $stream.Dispose() }
Write-Output '.env created with fresh local secrets. No infrastructure was started.'
