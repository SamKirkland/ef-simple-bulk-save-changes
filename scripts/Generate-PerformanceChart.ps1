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

function Add-PerformanceRow {
    param(
        [System.Collections.Generic.List[object]]$Rows,
        [string]$Scenario,
        [string]$RowsText,
        [string]$Method,
        [string]$BatchSize,
        [string]$Database,
        [double]$ElapsedMs,
        [double]$Speedup
    )

    $rowCount = [int](Parse-Number $RowsText)
    $showBatchSize = -not [string]::IsNullOrWhiteSpace($BatchSize)
    if ($showBatchSize) {
        $batchSizeValue = [int](Parse-Number $BatchSize)
        $showBatchSize = $batchSizeValue -lt $rowCount
    }

    $methodLabel = if (-not $showBatchSize) {
        $Method
    }
    else {
        "$Method (batch $BatchSize)"
    }

    $rowLabel = if ($RowsText -eq "1") { "1 row" } else { "$RowsText rows" }

    $Rows.Add([pscustomobject]@{
        Scenario = $Scenario
        Rows = $rowLabel
        MethodLabel = $methodLabel
        Method = $Method
        Database = $Database
        ElapsedMs = $ElapsedMs
        Speedup = $Speedup
    })
}

$rows = New-Object System.Collections.Generic.List[object]
$scenario = $null
$inPerformanceSection = $false

foreach ($line in Get-Content -LiteralPath $ReadmePath) {
    if ($line -eq "## Performance Results" -or $line -eq "## SQLite Performance Results" -or $line -eq "## SQLite and PostgreSQL Performance Results") {
        $inPerformanceSection = $true
        continue
    }

    if ($inPerformanceSection -and $line.StartsWith("## ") -and $line -ne "## Performance Results" -and $line -ne "## SQLite Performance Results" -and $line -ne "## SQLite and PostgreSQL Performance Results") {
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
    if ($cells.Count -ne 5 -and $cells.Count -ne 7) {
        continue
    }

    $method = $cells[1]
    $batchSize = $cells[2]
    if ($cells.Count -eq 5) {
        Add-PerformanceRow $rows $scenario $cells[0] $method $batchSize "SQLite" (Parse-Number $cells[3]) (Parse-Number $cells[4].TrimEnd("x"))
        continue
    }

    Add-PerformanceRow $rows $scenario $cells[0] $method $batchSize "SQLite" (Parse-Number $cells[3]) (Parse-Number $cells[4].TrimEnd("x"))
    Add-PerformanceRow $rows $scenario $cells[0] $method $batchSize "PostgreSQL" (Parse-Number $cells[5]) (Parse-Number $cells[6].TrimEnd("x"))
}

if ($rows.Count -eq 0) {
    throw "No performance rows found in $ReadmePath."
}

$maxSpeedup = [Math]::Ceiling(($rows | Measure-Object -Property Speedup -Maximum).Maximum)
$chartWidth = 1080
$left = 340
$right = 44
$top = 102
$rowHeight = 24
$methodHeaderHeight = 20
$scenarioHeaderHeight = 30
$scenarioGap = 24
$rowGroupHeaderHeight = 34
$rowGroupGap = 10
$axisWidth = $chartWidth - $left - $right
$groupedRows = $rows | Group-Object Scenario
$rowGroupCount = ($groupedRows | ForEach-Object { ($_.Group | Group-Object Rows).Count } | Measure-Object -Sum).Sum
$methodGroupCount = ($groupedRows | ForEach-Object { $_.Group | Group-Object Rows | ForEach-Object { ($_.Group | Group-Object MethodLabel).Count } } | Measure-Object -Sum).Sum
$chartHeight = $top + ($rows.Count * $rowHeight) + ($methodGroupCount * $methodHeaderHeight) + ($rowGroupCount * ($rowGroupHeaderHeight + $rowGroupGap)) + ($groupedRows.Count * ($scenarioHeaderHeight + $scenarioGap)) + 78

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
[void]$svg.AppendLine("    .method-heading { font-size: 12px; font-weight: 700; fill: #394255; }")
[void]$svg.AppendLine("    .database { font-size: 11px; fill: #697386; }")
[void]$svg.AppendLine("    .subsection-rule { stroke: #e2e8f0; stroke-width: 1; }")
[void]$svg.AppendLine("    .axis { stroke: #c9d1dd; stroke-width: 1; }")
[void]$svg.AppendLine("    .grid { stroke: #edf1f6; stroke-width: 1; }")
[void]$svg.AppendLine("    .tick { font-size: 11px; fill: #697386; }")
[void]$svg.AppendLine("    .value { font-size: 12px; font-weight: 600; }")
[void]$svg.AppendLine("    .value-inside { font-size: 12px; font-weight: 600; fill: #ffffff; }")
[void]$svg.AppendLine("    .legend { font-size: 11px; fill: #566174; }")
[void]$svg.AppendLine("  </style>")
[void]$svg.AppendLine("  <text x=""24"" y=""32"" class=""title"">BulkSaveChanges Performance</text>")
[void]$svg.AppendLine("  <text x=""24"" y=""52"" class=""subtitle"">Speedup is calculated against SaveChanges for the same scenario and row count. Values below 1x are slower.</text>")
[void]$svg.AppendLine("  <rect x=""24"" y=""70"" width=""12"" height=""12"" rx=""2"" fill=""#2374ab""/>")
[void]$svg.AppendLine("  <text x=""44"" y=""80"" class=""legend"">SQLite</text>")
[void]$svg.AppendLine("  <rect x=""104"" y=""70"" width=""12"" height=""12"" rx=""2"" fill=""#2f855a""/>")
[void]$svg.AppendLine("  <text x=""124"" y=""80"" class=""legend"">PostgreSQL</text>")
[void]$svg.AppendLine("  <rect x=""218"" y=""70"" width=""12"" height=""12"" rx=""2"" fill=""#d65f5f""/>")
[void]$svg.AppendLine("  <text x=""238"" y=""80"" class=""legend"">slower than SaveChanges</text>")

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

        foreach ($methodGroup in ($rowGroup.Group | Group-Object MethodLabel)) {
            $methodLabel = Escape-Xml $methodGroup.Name
            [void]$svg.AppendLine("  <text x=""72"" y=""$y"" class=""method-heading"">$methodLabel</text>")
            $y += $methodHeaderHeight

            foreach ($row in $methodGroup.Group) {
                $barWidth = [Math]::Max(1, ($row.Speedup / $maxSpeedup) * $axisWidth)
                $barColor = if ($row.Speedup -lt 1) {
                    "#d65f5f"
                }
                elseif ($row.Database -eq "PostgreSQL") {
                    "#2f855a"
                }
                else {
                    "#2374ab"
                }
                $barY = $y - 14
                $database = Escape-Xml $row.Database
                $value = Escape-Xml ("{0:N2}x ({1:N2} ms)" -f $row.Speedup, $row.ElapsedMs)
                $valueX = $left + $barWidth + 8
                $valueClass = "value"
                $valueAnchor = "start"
                if ($valueX -gt ($chartWidth - 150)) {
                    $valueX = $left + $barWidth - 8
                    $valueClass = "value-inside"
                    $valueAnchor = "end"
                }

                [void]$svg.AppendLine("  <text x=""116"" y=""$y"" class=""database"">$database</text>")
                [void]$svg.AppendLine("  <rect x=""$left"" y=""$barY"" width=""$barWidth"" height=""14"" rx=""3"" fill=""$barColor""/>")
                [void]$svg.AppendLine("  <text x=""$valueX"" y=""$y"" class=""$valueClass"" text-anchor=""$valueAnchor"">$value</text>")
                $y += $rowHeight
            }
        }

        $y += $rowGroupGap
    }

    $y += $scenarioGap - 4
}

[void]$svg.AppendLine("</svg>")

Set-Content -LiteralPath $OutputPath -Value $svg.ToString() -Encoding utf8
Write-Host "Generated $OutputPath"
