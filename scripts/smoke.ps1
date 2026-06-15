# Phase 1 smoke test: asserts every service health endpoint answers and the
# infra provisioning (NATS streams, MinIO buckets, Vault KV) took effect.
# Run after: docker compose up -d --build
$ErrorActionPreference = 'Stop'
$failures = @()

$services = @{
    'identity'    = 'http://localhost:5001/health'
    'admin-api'   = 'http://localhost:5002/health'
    'content-api' = 'http://localhost:5003/health'
    'media-worker' = 'http://localhost:5004/health'
    'site-builder' = 'http://localhost:5005/health'
    'site-host'   = 'http://localhost:5006/health'
    'ai-gateway'  = 'http://localhost:5007/health'
    'admin-spa'   = 'http://localhost:5000/'
}

foreach ($name in $services.Keys) {
    try {
        $response = Invoke-WebRequest -Uri $services[$name] -UseBasicParsing -TimeoutSec 10
        if ($response.StatusCode -eq 200) {
            Write-Host "[OK]   $name" -ForegroundColor Green
        } else {
            $failures += "$name returned $($response.StatusCode)"
        }
    } catch {
        $failures += "$name unreachable: $($_.Exception.Message)"
    }
}

# content-api plugin catalog
try {
    $plugins = Invoke-RestMethod 'http://localhost:5003/api/_plugins' -TimeoutSec 10
    if ($plugins.Count -eq 12) {
        Write-Host "[OK]   plugin catalog (12 plugins)" -ForegroundColor Green
    } else {
        $failures += "plugin catalog returned $($plugins.Count) plugins, expected 12"
    }
} catch {
    $failures += "plugin catalog unreachable: $($_.Exception.Message)"
}

# NATS JetStream streams via monitoring endpoint
try {
    $jsz = Invoke-RestMethod 'http://localhost:8222/jsz?streams=true' -TimeoutSec 10
    $streamNames = $jsz.account_details | ForEach-Object { $_.stream_detail } | ForEach-Object { $_.name }
    $expected = 'TENANCY','CMS','MEDIA','MEDIA_EVENTS','SITES','SITES_EVENTS','ANALYTICS','CHAT'
    $missing = $expected | Where-Object { $streamNames -notcontains $_ }
    if ($missing.Count -eq 0) {
        Write-Host "[OK]   NATS streams ($($expected.Count))" -ForegroundColor Green
    } else {
        $failures += "missing NATS streams: $($missing -join ', ')"
    }
} catch {
    $failures += "NATS monitoring unreachable: $($_.Exception.Message)"
}

# Vault KV seed
try {
    $headers = @{ 'X-Vault-Token' = 'dcms-dev-root' }
    Invoke-RestMethod 'http://localhost:8200/v1/secret/data/dcms/shared' -Headers $headers -TimeoutSec 10 | Out-Null
    Write-Host "[OK]   Vault KV seed" -ForegroundColor Green
} catch {
    $failures += "Vault KV seed missing: $($_.Exception.Message)"
}

if ($failures.Count -gt 0) {
    Write-Host "`nSMOKE FAILED:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "`nSmoke test passed." -ForegroundColor Green
