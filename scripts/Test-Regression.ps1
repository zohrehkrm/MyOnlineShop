param([string]$ResultsDirectory = 'artifacts/phase16-tests', [switch]$SummarizeOnly)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$previousLocation = Get-Location
$infrastructureVariables = @(
    'FOUNDATION_TEST_SQL_SERVER', 'IDENTITY_TEST_SQL_SERVER', 'INVENTORY_TEST_SQL_SERVER',
    'CART_TEST_SQL_SERVER', 'PRICING_TEST_SQL_SERVER', 'ORDER_TEST_SQL_SERVER', 'WALLET_TEST_SQL_SERVER',
    'MESSAGING_TEST_SQL_SERVER', 'MESSAGING_TEST_RABBITMQ', 'REFUND_TEST_SQL_SERVER',
    'SHIPPING_TEST_SQL_SERVER', 'REDIS_TEST_CONNECTION_STRING', 'REPORTING_TEST_SQL_SERVER'
)
$previousVariables = @{}
$disabledFeatures = @('Messaging__Enabled', 'RefundProcessing__Enabled', 'Redis__Enabled', 'BootstrapIdentityAdmin')
$projects = @('Foundation', 'Identity', 'Catalog', 'Inventory', 'Cart', 'PricingDiscount', 'Order',
    'Wallet', 'Messaging', 'Refund', 'Shipping', 'Caching', 'Reporting')
$summary = @()
try {
    Set-Location -LiteralPath $repository
    foreach ($name in $infrastructureVariables) {
        $previousVariables[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    foreach ($name in $disabledFeatures) {
        $previousVariables[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, 'false', 'Process')
    }
    $results = [IO.Path]::GetFullPath((Join-Path $repository $ResultsDirectory))
    foreach ($module in $projects) {
        $project = "tests/MyOnlineShop.$module.Tests/MyOnlineShop.$module.Tests.csproj"
        $resultFile = "MyOnlineShop.$module.Tests.trx"
        # Builds one relevant test project at a time. Gated infrastructure tests compile and skip.
        if (-not $SummarizeOnly) {
            & dotnet test $project --verbosity quiet --results-directory $results --logger "trx;LogFileName=$resultFile"
            if ($LASTEXITCODE -ne 0) { throw "Regression failed for $module (exit $LASTEXITCODE). Review $resultFile." }
        }
        [xml]$report = Get-Content -LiteralPath (Join-Path $results $resultFile)
        $counts = $report.TestRun.ResultSummary.Counters
        # xUnit TRX counters can report notExecuted=0 while result outcomes contain skipped tests.
        $skipped = @($report.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'NotExecuted' }).Count
        if ([int]$counts.passed + [int]$counts.failed + $skipped -ne [int]$counts.total) {
            throw "Unexpected test outcomes for $module. Review $resultFile before trusting the summary."
        }
        $summary += [pscustomobject]@{ Module = $module; Passed = [int]$counts.passed; Failed = [int]$counts.failed; Skipped = $skipped }
    }
    $summary | Format-Table -AutoSize
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $results 'summary.json') -Encoding UTF8
    Write-Output ("Passed: {0}; failed: {1}; skipped: {2}" -f
        ($summary | Measure-Object Passed -Sum).Sum, ($summary | Measure-Object Failed -Sum).Sum, ($summary | Measure-Object Skipped -Sum).Sum)
}
finally {
    foreach ($name in $previousVariables.Keys) { [Environment]::SetEnvironmentVariable($name, $previousVariables[$name], 'Process') }
    Set-Location -LiteralPath $previousLocation.Path
}
