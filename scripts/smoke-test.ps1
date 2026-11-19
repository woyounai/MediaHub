param(
  [Parameter(Mandatory=$true)][string]$BaseUrl,
  [Parameter(Mandatory=$true)][string]$ApiKey,
  [Parameter(Mandatory=$true)][string]$TestFolder
)
$headers = @{ "X-MediaHub-Key" = $ApiKey }
$health = Invoke-RestMethod "$BaseUrl/api/health"
if (-not $health.ok) { throw "Health check failed" }
$body = @{ path = $TestFolder; recursive = $false } | ConvertTo-Json
$indexed = Invoke-RestMethod "$BaseUrl/api/index" -Method Post -Headers $headers -ContentType "application/json" -Body $body
$items = Invoke-RestMethod "$BaseUrl/api/items" -Headers $headers
Write-Host "Health: OK"
Write-Host "Indexed: $($indexed.indexed)"
Write-Host "Total indexed records: $($items.count)"
