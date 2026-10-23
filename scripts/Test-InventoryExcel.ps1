param([Parameter(Mandatory=$true)][string]$FixtureDirectory)
$ErrorActionPreference = 'Stop'
$fixtureRoot = (Resolve-Path -LiteralPath $FixtureDirectory).Path
$fixturePath = Join-Path $fixtureRoot 'excel-fixture.json'
$fixture = Get-Content -LiteralPath $fixturePath -Raw -Encoding UTF8 | ConvertFrom-Json
$reportPath = Join-Path $fixtureRoot ('excel-verification-' + [Guid]::NewGuid().ToString('N') + '.json')
$existingIds = @(Get-Process EXCEL -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$report = @{ status='running'; nativeQualification=$false; fixtureSha256=(Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash; cases=@(); ownedExcelExited=$false; forcedStop=$false }
$report.harnessSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$report.existingExcelProcessIds = $existingIds
$excel = $null; $excelProcess = $null; $workbooks = $null
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class InventoryExcelWindow {
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
'@
function Release-Com($value) {
    if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value)
    }
}
try {
    $excel = New-Object -ComObject Excel.Application
    [uint32]$excelProcessId = 0
    [void][InventoryExcelWindow]::GetWindowThreadProcessId([IntPtr]$excel.Hwnd, [ref]$excelProcessId)
    if (-not $excelProcessId -or $existingIds -contains $excelProcessId) { throw 'Excel did not create a separate owned process; no workbook was opened.' }
    $excelProcess = Get-Process -Id $excelProcessId
    $report.processId = $excelProcessId
    $report.processStartedUtc = $excelProcess.StartTime.ToUniversalTime().ToString('O')
    $report.excelVersion = $excel.Version
    $report.excelBuild = $excel.Build
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $excel.EnableEvents = $false
    $excel.AutomationSecurity = 3
    $workbooks = $excel.Workbooks
    $metadataFiles = @(Get-ChildItem -LiteralPath $fixtureRoot -Recurse -File -Filter '*.metadata.json')
    foreach ($case in $fixture.cases) {
        $matches = @($metadataFiles | Where-Object {
            $metadata = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            $metadata.snapshot.snapshotId -eq $case.snapshotId -and
                (Test-Path -LiteralPath ($_.FullName -replace '\.metadata\.json$', '.xlsx'))
        })
        if ($matches.Count -ne 1) { throw 'Expected exactly one generated XLSX per Excel fixture case.' }
        $path = $matches[0].FullName -replace '\.metadata\.json$', '.xlsx'
        $beforeHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $book = $null; $sheets = $null; $sheet = $null; $range = $null; $details = $null; $warningCell = $null
        try {
            # Normal load, no link updates, read-only; default AddToMru is false.
            $book = $workbooks.Open($path, 0, $true)
            if (-not $book.ReadOnly) { throw 'Workbook did not open read-only.' }
            $sheets = $book.Worksheets
            $sheet = $sheets.Item('Inventory')
            $range = $sheet.Range('A2', ('A' + ($case.names.Count + 1)))
            if ($range.HasFormula -ne $false) { throw 'A source string became a formula.' }
            $values = $range.Value2
            for ($index = 0; $index -lt $case.names.Count; $index++) {
                $actual = [string]$values.GetValue(($index + 1), 1)
                if (-not [string]::Equals($actual, $case.names[$index], [StringComparison]::Ordinal)) {
                    throw "Excel changed inventory text at row $($index + 2)."
                }
            }
            $details = $sheets.Item('Capture details')
            $warningCell = $details.Range('B9')
            if (-not [string]::Equals([string]$warningCell.Value2, ($case.warnings -join '; '), [StringComparison]::Ordinal)) {
                throw 'Excel changed capture warnings.'
            }
            $report.cases += @{ snapshotId=$case.snapshotId; rows=$case.names.Count; path=$path; sha256=$beforeHash; status='passed' }
        } finally {
            Release-Com $warningCell; Release-Com $details; Release-Com $range; Release-Com $sheet; Release-Com $sheets
            if ($null -ne $book) { $book.Close($false); Release-Com $book }
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $beforeHash) { throw 'Read-only verification changed the workbook bytes.' }
        }
    }
    $report.status = 'passed'
} catch {
    $report.status = 'failed'; $report.error = $_.Exception.Message
    throw
} finally {
    Release-Com $workbooks
    try { if ($null -ne $excelProcess) { $excel.Quit() } }
    catch { $report.status = 'failed'; $report.cleanupError = $_.Exception.Message }
    Release-Com $excel
    [GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    if ($null -ne $excelProcess) {
        if (-not $excelProcess.WaitForExit(5000)) { $excelProcess.Kill(); $excelProcess.WaitForExit(5000) | Out-Null; $report.forcedStop = $true; $report.status = 'failed' }
        $report.ownedExcelExited = $excelProcess.HasExited
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Output "Excel receipt: $reportPath"
}
if ($report.status -ne 'passed' -or -not $report.ownedExcelExited) { throw 'Excel verification did not complete with clean owned-process exit.' }
