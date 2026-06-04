param(
    [string]$ReadmePath = "README.md",
    [string]$OutputPath = "docs/performance-results.svg"
)

$ErrorActionPreference = "Stop"

function Escape-Xml {
    param([string]$Value)

    return [System.Security.SecurityElement]::Escape($Value)
}

function Parse-Number {
    param([string]$Value)

    return [double]::Parse($Value.Replace(",", ""), [System.Globalization.CultureInfo]::InvariantCulture)
}

$rows = New-Object System.Collections.Generic.List[object]
$scenario = $null
$inPerformanceSection = $false

foreach ($line in Get-Content -LiteralPath $ReadmePath) {
    if ($line -eq "## Performance Results" -or $line -eq "## SQLite Performance Results") {
        $inPerformanceSection = $true
        continue
    }

    if ($inPerformanceSection -and $line.StartsWith("## ") -and $line -ne "## Performance Results" -and $line -ne "## SQLite Performance Results") {
        break
    }

    if (-not $inPerformanceSection) {
        continue
    }

    if ($line.StartsWith("### ")) {
        $scenario = $line.Substring(4).Trim()
        continue
    }

    if (-not $scenario -or -not $line.StartsWith("|") -or $line.Contains("---") -or $line.Contains("Rows |")) {
        continue
    }

    $cells = $line.Trim("|").Split("|") | ForEach-Object { $_.Trim() }
    if ($cells.Count -ne 5) {
        continue
    }

    $method = $cells[1]
    $batchSize = $cells[2]
    $elapsedMs = Parse-Number $cells[3]
    $speedup = Parse-Number $cells[4].TrimEnd("x")
    $rowCount = [int](Parse-Number $cells[0])
    $showBatchSize = -not [string]::IsNullOrWhiteSpace($batchSize)
    if ($showBatchSize) {
        $batchSizeValue = [int](Parse-Number $batchSize)
        $showBatchSize = $batchSizeValue -lt $rowCount
    }

    $methodLabel = if (-not $showBatchSize) {
        $method
    }
    else {
        "$method (batch $batchSize)"
    }

    $rowLabel = if ($cells[0] -eq "1") { "1 row" } else { "$($cells[0]) rows" }

    $rows.Add([pscustomobject]@{
        Scenario = $scenario
        Rows = $rowLabel
        MethodLabel = $methodLabel
        Method = $method
        ElapsedMs = $elapsedMs
        Speedup = $speedup
    })
}

if ($rows.Count -eq 0) {
    throw "No performance rows found in $ReadmePath."
}

$maxSpeedup = [Math]::Ceiling(($rows | Measure-Object -Property Speedup -Maximum).Maximum)
$chartWidth = 960
$left = 270
$right = 40
$top = 74
$rowHeight = 28
$scenarioHeaderHeight = 30
$scenarioGap = 24
$rowGroupHeaderHeight = 34
$rowGroupGap = 10
$axisWidth = $chartWidth - $left - $right
$groupedRows = $rows | Group-Object Scenario
$rowGroupCount = ($groupedRows | ForEach-Object { ($_.Group | Group-Object Rows).Count } | Measure-Object -Sum).Sum
$chartHeight = $top + ($rows.Count * $rowHeight) + ($rowGroupCount * ($rowGroupHeaderHeight + $rowGroupGap)) + ($groupedRows.Count * ($scenarioHeaderHeight + $scenarioGap)) + 78

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$svg = New-Object System.Text.StringBuilder
[void]$svg.AppendLine("<svg xmlns=""http://www.w3.org/2000/svg"" width=""$chartWidth"" height=""$chartHeight"" viewBox=""0 0 $chartWidth $chartHeight"" role=""img"" aria-labelledby=""title desc"">")
[void]$svg.AppendLine("  <title id=""title"">BulkSaveChanges performance results</title>")
[void]$svg.AppendLine("  <desc id=""desc"">Horizontal bar chart showing speedup versus SaveChanges for save and synchronize scenarios.</desc>")
[void]$svg.AppendLine("  <rect width=""100%"" height=""100%"" fill=""#ffffff""/>")
[void]$svg.AppendLine("  <style>")
[void]$svg.AppendLine("    text { font-family: Segoe UI, Arial, sans-serif; fill: #172033; }")
[void]$svg.AppendLine("    .title { font-size: 22px; font-weight: 700; }")
[void]$svg.AppendLine("    .subtitle { font-size: 13px; fill: #566174; }")
[void]$svg.AppendLine("    .scenario { font-size: 15px; font-weight: 700; }")
[void]$svg.AppendLine("    .row-count { font-size: 13px; font-weight: 700; fill: #394255; }")
[void]$svg.AppendLine("    .method { font-size: 12px; fill: #566174; }")
[void]$svg.AppendLine("    .subsection-rule { stroke: #e2e8f0; stroke-width: 1; }")
[void]$svg.AppendLine("    .axis { stroke: #c9d1dd; stroke-width: 1; }")
[void]$svg.AppendLine("    .grid { stroke: #edf1f6; stroke-width: 1; }")
[void]$svg.AppendLine("    .tick { font-size: 11px; fill: #697386; }")
[void]$svg.AppendLine("    .value { font-size: 12px; font-weight: 600; }")
[void]$svg.AppendLine("  </style>")
[void]$svg.AppendLine("  <text x=""24"" y=""32"" class=""title"">BulkSaveChanges Performance</text>")
[void]$svg.AppendLine("  <text x=""24"" y=""52"" class=""subtitle"">Speedup is calculated against SaveChanges for the same scenario and row count. Values below 1x are slower.</text>")

for ($tick = 0; $tick -le $maxSpeedup; $tick++) {
    $x = $left + (($tick / $maxSpeedup) * $axisWidth)
    [void]$svg.AppendLine("  <line x1=""$x"" y1=""$top"" x2=""$x"" y2=""$($chartHeight - 48)"" class=""grid""/>")
    [void]$svg.AppendLine("  <text x=""$x"" y=""$($chartHeight - 24)"" class=""tick"" text-anchor=""middle"">$($tick)x</text>")
}

[void]$svg.AppendLine("  <line x1=""$left"" y1=""$top"" x2=""$left"" y2=""$($chartHeight - 48)"" class=""axis""/>")

$y = $top
foreach ($group in $groupedRows) {
    [void]$svg.AppendLine("  <text x=""24"" y=""$($y + 18)"" class=""scenario"">$(Escape-Xml $group.Name)</text>")
    $y += $scenarioHeaderHeight

    foreach ($rowGroup in ($group.Group | Group-Object Rows)) {
        $rowCount = Escape-Xml $rowGroup.Name
        $ruleY = $y + 10

        [void]$svg.AppendLine("  <text x=""44"" y=""$($y + 16)"" class=""row-count"">$rowCount</text>")
        [void]$svg.AppendLine("  <line x1=""142"" y1=""$ruleY"" x2=""$($chartWidth - $right)"" y2=""$ruleY"" class=""subsection-rule""/>")
        $y += $rowGroupHeaderHeight

        foreach ($row in $rowGroup.Group) {
            $barWidth = [Math]::Max(1, ($row.Speedup / $maxSpeedup) * $axisWidth)
            $barColor = if ($row.Method -eq "SaveChanges") { "#7a8798" } elseif ($row.Speedup -lt 1) { "#d65f5f" } else { "#2374ab" }
            $barY = $y - 14
            $methodLabel = Escape-Xml $row.MethodLabel
            $value = Escape-Xml ("{0:N2}x ({1:N2} ms)" -f $row.Speedup, $row.ElapsedMs)

            [void]$svg.AppendLine("  <text x=""72"" y=""$y"" class=""method"">$methodLabel</text>")
            [void]$svg.AppendLine("  <rect x=""$left"" y=""$barY"" width=""$barWidth"" height=""16"" rx=""3"" fill=""$barColor""/>")
            [void]$svg.AppendLine("  <text x=""$($left + $barWidth + 8)"" y=""$y"" class=""value"">$value</text>")
            $y += $rowHeight
        }

        $y += $rowGroupGap
    }

    $y += $scenarioGap - 4
}

[void]$svg.AppendLine("</svg>")

Set-Content -LiteralPath $OutputPath -Value $svg.ToString() -Encoding utf8
Write-Host "Generated $OutputPath"
