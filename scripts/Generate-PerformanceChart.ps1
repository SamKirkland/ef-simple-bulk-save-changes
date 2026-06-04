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

function Get-DatabaseColor {
    param([string]$Database)

    if ($Database -eq "PostgreSQL") {
        return "#2f855a"
    }

    if ($Database -eq "CockroachDB") {
        return "#7c3aed"
    }

    return "#2374ab"
}

function Add-PerformanceRowIfPresent {
    param(
        [System.Collections.Generic.List[object]]$Rows,
        [string]$Scenario,
        [string]$RowsText,
        [string]$Method,
        [string]$BatchSize,
        [string]$Database,
        [string]$ElapsedMs,
        [string]$Speedup
    )

    if ([string]::IsNullOrWhiteSpace($ElapsedMs) -or [string]::IsNullOrWhiteSpace($Speedup)) {
        return
    }

    Add-PerformanceRow $Rows $Scenario $RowsText $Method $BatchSize $Database (Parse-Number $ElapsedMs) (Parse-Number $Speedup.TrimEnd("x"))
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
    $showBatchSize = -not [string]::IsNullOrWhiteSpace($BatchSize) -and $rowCount -gt 1

    $methodLabel = if (-not $showBatchSize -or $Method -eq "SaveChanges") {
        $Method
    }
    else {
        "batch $BatchSize"
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
    if ($line -eq "## Performance Results" -or $line -eq "## SQLite Performance Results" -or $line -eq "## SQLite and PostgreSQL Performance Results" -or $line -eq "## SQLite, PostgreSQL, and CockroachDB Performance Results") {
        $inPerformanceSection = $true
        continue
    }

    if ($inPerformanceSection -and $line.StartsWith("## ") -and $line -ne "## Performance Results" -and $line -ne "## SQLite Performance Results" -and $line -ne "## SQLite and PostgreSQL Performance Results" -and $line -ne "## SQLite, PostgreSQL, and CockroachDB Performance Results") {
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
    if ($cells.Count -ne 5 -and $cells.Count -ne 7 -and $cells.Count -ne 9) {
        continue
    }

    $method = $cells[1]
    $batchSize = $cells[2]
    if ($cells.Count -eq 5) {
        Add-PerformanceRowIfPresent $rows $scenario $cells[0] $method $batchSize "SQLite" $cells[3] $cells[4]
        continue
    }

    Add-PerformanceRowIfPresent $rows $scenario $cells[0] $method $batchSize "SQLite" $cells[3] $cells[4]
    Add-PerformanceRowIfPresent $rows $scenario $cells[0] $method $batchSize "PostgreSQL" $cells[5] $cells[6]
    if ($cells.Count -eq 9) {
        Add-PerformanceRowIfPresent $rows $scenario $cells[0] $method $batchSize "CockroachDB" $cells[7] $cells[8]
    }
}

if ($rows.Count -eq 0) {
    throw "No performance rows found in $ReadmePath."
}

$maxSpeedup = [Math]::Ceiling(($rows | Measure-Object -Property Speedup -Maximum).Maximum)
$tickStep = if ($maxSpeedup -le 8) { 1 } elseif ($maxSpeedup -le 20) { 2 } else { 5 }
$maxTick = [Math]::Ceiling($maxSpeedup / $tickStep) * $tickStep
$chartWidth = 1080
$left = 176
$right = 44
$top = 82
$barHeight = 12
$barGap = 3
$barStride = $barHeight + $barGap
$methodGroupGap = 7
$scenarioHeaderHeight = 42
$scenarioGap = 16
$rowGroupHeaderHeight = 22
$rowGroupGap = 10
$axisWidth = $chartWidth - $left - $right
$groupedRows = $rows | Group-Object Scenario
$rowGroupCount = ($groupedRows | ForEach-Object { ($_.Group | Group-Object Rows).Count } | Measure-Object -Sum).Sum
$methodGroupCount = ($groupedRows | ForEach-Object { $_.Group | Group-Object Rows | ForEach-Object { ($_.Group | Group-Object MethodLabel).Count } } | Measure-Object -Sum).Sum
$chartHeight = $top + ($rows.Count * $barStride) + ($methodGroupCount * $methodGroupGap) + ($rowGroupCount * ($rowGroupHeaderHeight + $rowGroupGap)) + ($groupedRows.Count * ($scenarioHeaderHeight + $scenarioGap)) + 52

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
[void]$svg.AppendLine("    .scenario { font-size: 16px; font-weight: 700; }")
[void]$svg.AppendLine("    .row-count { font-size: 12px; font-weight: 700; fill: #394255; }")
[void]$svg.AppendLine("    .method-heading { font-size: 12px; font-weight: 700; fill: #394255; }")
[void]$svg.AppendLine("    .subsection-rule { stroke: #e2e8f0; stroke-width: 1; }")
[void]$svg.AppendLine("    .axis { stroke: #c9d1dd; stroke-width: 1; }")
[void]$svg.AppendLine("    .grid { stroke: #edf1f6; stroke-width: 1; }")
[void]$svg.AppendLine("    .tick { font-size: 11px; fill: #697386; }")
[void]$svg.AppendLine("    .value { font-size: 11px; font-weight: 600; }")
[void]$svg.AppendLine("    .value-inside { font-size: 11px; font-weight: 600; fill: #ffffff; }")
[void]$svg.AppendLine("    .legend { font-size: 11px; fill: #566174; }")
[void]$svg.AppendLine("  </style>")
[void]$svg.AppendLine("  <text x=""24"" y=""32"" class=""title"">BulkSaveChanges Performance</text>")
[void]$svg.AppendLine("  <text x=""24"" y=""52"" class=""subtitle"">Speedup is versus SaveChanges for the same scenario and row count. Bar labels show speedup and elapsed milliseconds.</text>")

for ($tick = 0; $tick -le $maxTick; $tick += $tickStep) {
    $x = $left + (($tick / $maxTick) * $axisWidth)
    [void]$svg.AppendLine("  <line x1=""$x"" y1=""$top"" x2=""$x"" y2=""$($chartHeight - 48)"" class=""grid""/>")
    [void]$svg.AppendLine("  <text x=""$x"" y=""$($chartHeight - 24)"" class=""tick"" text-anchor=""middle"">$($tick)x</text>")
}

[void]$svg.AppendLine("  <line x1=""$left"" y1=""$top"" x2=""$left"" y2=""$($chartHeight - 48)"" class=""axis""/>")

$y = $top
$databaseOrder = @("SQLite", "PostgreSQL", "CockroachDB")
foreach ($group in $groupedRows) {
    $legendX = $chartWidth - $right - 354
    $legendY = $y + 18

    [void]$svg.AppendLine("  <line x1=""24"" y1=""$($y - 8)"" x2=""$($chartWidth - $right)"" y2=""$($y - 8)"" class=""subsection-rule""/>")
    [void]$svg.AppendLine("  <text x=""24"" y=""$legendY"" class=""scenario"">$(Escape-Xml $group.Name)</text>")
    [void]$svg.AppendLine("  <rect x=""$legendX"" y=""$($legendY - 10)"" width=""10"" height=""10"" rx=""2"" fill=""$(Get-DatabaseColor "SQLite")""/>")
    [void]$svg.AppendLine("  <text x=""$($legendX + 16)"" y=""$legendY"" class=""legend"">SQLite</text>")
    [void]$svg.AppendLine("  <rect x=""$($legendX + 86)"" y=""$($legendY - 10)"" width=""10"" height=""10"" rx=""2"" fill=""$(Get-DatabaseColor "PostgreSQL")""/>")
    [void]$svg.AppendLine("  <text x=""$($legendX + 102)"" y=""$legendY"" class=""legend"">PostgreSQL</text>")
    [void]$svg.AppendLine("  <rect x=""$($legendX + 208)"" y=""$($legendY - 10)"" width=""10"" height=""10"" rx=""2"" fill=""$(Get-DatabaseColor "CockroachDB")""/>")
    [void]$svg.AppendLine("  <text x=""$($legendX + 224)"" y=""$legendY"" class=""legend"">CockroachDB</text>")
    $y += $scenarioHeaderHeight

    foreach ($rowGroup in ($group.Group | Group-Object Rows)) {
        $rowCount = Escape-Xml $rowGroup.Name
        $ruleY = $y + 8

        [void]$svg.AppendLine("  <text x=""24"" y=""$($y + 13)"" class=""row-count"">$rowCount</text>")
        [void]$svg.AppendLine("  <line x1=""104"" y1=""$ruleY"" x2=""$($chartWidth - $right)"" y2=""$ruleY"" class=""subsection-rule""/>")
        $y += $rowGroupHeaderHeight

        foreach ($methodGroup in ($rowGroup.Group | Group-Object MethodLabel)) {
            $methodLabel = Escape-Xml $methodGroup.Name
            $barCount = $methodGroup.Group.Count
            $labelY = $y + (($barCount * $barStride - $barGap) / 2) + 4
            [void]$svg.AppendLine("  <text x=""166"" y=""$labelY"" class=""method-heading"" text-anchor=""end"">$methodLabel</text>")

            foreach ($database in $databaseOrder) {
                $row = $methodGroup.Group | Where-Object { $_.Database -eq $database } | Select-Object -First 1
                if (-not $row) {
                    continue
                }

                $barWidth = [Math]::Max(1, ($row.Speedup / $maxTick) * $axisWidth)
                $barColor = Get-DatabaseColor $row.Database
                $barY = $y
                $valueY = $y + 10
                $value = Escape-Xml ("{0:N2}x ({1:N2} ms)" -f $row.Speedup, $row.ElapsedMs)
                $valueX = $left + $barWidth + 8
                $valueClass = "value"
                $valueAnchor = "start"
                if ($valueX -gt ($chartWidth - 150)) {
                    $valueX = $left + $barWidth - 8
                    $valueClass = "value-inside"
                    $valueAnchor = "end"
                }

                [void]$svg.AppendLine("  <rect x=""$left"" y=""$barY"" width=""$barWidth"" height=""$barHeight"" rx=""2"" fill=""$barColor""/>")
                [void]$svg.AppendLine("  <text x=""$valueX"" y=""$valueY"" class=""$valueClass"" text-anchor=""$valueAnchor"">$value</text>")
                $y += $barStride
            }

            $y += $methodGroupGap
        }

        $y += $rowGroupGap
    }

    $y += $scenarioGap
}

[void]$svg.AppendLine("</svg>")

Set-Content -LiteralPath $OutputPath -Value $svg.ToString() -Encoding utf8
Write-Host "Generated $OutputPath"
