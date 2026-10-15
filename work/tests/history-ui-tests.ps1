param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Executable).Path)
$flags = [Reflection.BindingFlags]'NonPublic, Static'
$escape = $assembly.GetType('UsageWidget.WidgetWindow').GetMethod('EscapeHistoryCsv', $flags)
$connect = $assembly.GetType('UsageWidget.UsageHistoryChart').GetMethod('Connects', $flags)
function Assert-True([bool]$Value, [string]$Name) { if (-not $Value) { throw $Name } }
foreach ($danger in @('=1+2', '+SUM(1,2)', '-1+1', '@SUM(1,2)', '  =1+2', "`t=1+2", "`r=1+2")) {
    $escaped = $escape.Invoke($null, @($danger))
    Assert-True ($escaped.StartsWith('"''')) 'CSV formulas must be neutralized before quoting.'
}
Assert-True ($escape.Invoke($null, @('normal "value", text')) -eq '"normal ""value"", text"') 'CSV embedded quotes and commas must round-trip.'
Assert-True ($escape.Invoke($null, @('Codex')) -eq '"Codex"') 'Ordinary text should remain readable.'
function New-Point([int]$Minutes, [double]$Percent, [string]$Source = 'api') {
    $point = [Activator]::CreateInstance($assembly.GetType('UsageWidget.UsageHistoryPoint'))
    $point.ServiceId = 'codex'; $point.Label = '주간 한도'; $point.Source = $Source
    $point.ObservedAt = [DateTimeOffset]::Parse('2026-10-11T10:00:00+09:00').AddMinutes($Minutes)
    $point.RemainingPercent = $Percent
    return $point
}
$first = New-Point 0 80
$next = New-Point 5 75
Assert-True ($connect.Invoke($null, @($first, $next, [double]30))) 'Nearby observations of the same metric should connect.'
Assert-True (-not $connect.Invoke($null, @($first, (New-Point 45 60), [double]30))) 'Long collection gaps must remain visible.'
Assert-True (-not $connect.Invoke($null, @((New-Point 0 80 'manual'), (New-Point 5 75 'manual'), [double]30))) 'Manual entries must not imply continuous measurement.'
Assert-True (-not $connect.Invoke($null, @($first, (New-Point 5 75 'code'), [double]30))) 'A changed source must start a new line.'
$wslUbuntu = 'api:wsl:' + ('a' * 64)
$wslKali = 'api:wsl:' + ('b' * 64)
Assert-True (-not $connect.Invoke($null, @($first, (New-Point 5 75 $wslUbuntu), [double]30))) 'Windows and WSL observations must never form one trend line.'
Assert-True (-not $connect.Invoke($null, @((New-Point 0 80 $wslUbuntu), (New-Point 5 75 $wslKali), [double]30))) 'Different WSL distributions must never form one trend line.'
Assert-True ($connect.Invoke($null, @((New-Point 0 80 $wslUbuntu), (New-Point 5 75 $wslUbuntu), [double]30))) 'Nearby observations within one WSL distribution preserve their trend.'
$sourceName = $assembly.GetType('UsageWidget.WidgetWindow').GetMethod('HistorySourceName', $flags)
$wslLabel = [string]$sourceName.Invoke($null, @($wslUbuntu))
Assert-True ($wslLabel.Contains('API') -and $wslLabel.Contains('WSL aaaaaaaa') -and -not $wslLabel.Contains(('a' * 64))) 'History and CSV show the transport and a short WSL identity without exposing internal full tokens.'
Assert-True ($sourceName.Invoke($null, @('manual')) -eq '수동 기록') 'Manual history keeps its explicit manual provenance.'
Assert-True (-not $connect.Invoke($null, @($first, (New-Point 5 100), [double]30))) 'A replenishment jump must not look like usage.'
$next.ServiceId = 'eleven'
Assert-True (-not $connect.Invoke($null, @($first, $next, [double]30))) 'Different services must never share a trend line.'
$next = New-Point 5 75
$first.ResetsAt = $first.ObservedAt.AddMinutes(3)
$next.ResetsAt = $first.ResetsAt
Assert-True (-not $connect.Invoke($null, @($first, $next, [double]30))) 'A scheduled reset boundary must break the line.'
$first = New-Point 0 80; $next = New-Point 5 75
$first.Limit = [double]100; $next.Limit = [double]200
Assert-True (-not $connect.Invoke($null, @($first, $next, [double]30))) 'Capacity changes must start a new line.'
$metricValue = $assembly.GetType('UsageWidget.UsageHistoryChart').GetMethod('MetricValue', $flags)
$metricUnit = $assembly.GetType('UsageWidget.UsageHistoryChart').GetMethod('MetricUnit', $flags)
$niceMaximum = $assembly.GetType('UsageWidget.UsageHistoryChart').GetMethod('NiceMaximum', $flags)
$parseThreshold = $assembly.GetType('UsageWidget.WidgetWindow').GetMethod('ParseAbsoluteThreshold', $flags)
$first = New-Point 0 80; $next = New-Point 5 75
$first.ServiceId = 'higgsfield'; $next.ServiceId = 'higgsfield'
$first.RemainingPercent = $null; $next.RemainingPercent = $null
$first.Remaining = [double]120; $next.Remaining = [double]100
Assert-True ($metricValue.Invoke($null, @($first)) -eq 120) 'Credit chart must use the actual remaining amount when percentage is absent.'
Assert-True ($metricUnit.Invoke($null, @($first)) -eq '크레딧') 'Higgsfield web chart identifies credits.'
Assert-True ($connect.Invoke($null, @($first, $next, [double]30))) 'Auto credit observations form a usage line.'
$next.Remaining = [double]121
Assert-True (-not $connect.Invoke($null, @($first, $next, [double]30))) 'A credit top-up must break the usage line.'
$first.ServiceId = 'higgsfield-api'; $next.ServiceId = 'higgsfield-api'
$first.Source = 'manual'; $next.Source = 'manual'
$first.Remaining = [double]8.25; $next.Remaining = [double]7.5
Assert-True ($metricUnit.Invoke($null, @($first)) -eq 'USD') 'Higgsfield API chart identifies USD.'
Assert-True (-not $connect.Invoke($null, @($first, $next, [double]30))) 'Manual USD records remain separate points.'
Assert-True ($niceMaximum.Invoke($null, @([double]8.25)) -ge 8.25) 'Amount axis includes the largest value.'
Assert-True ($niceMaximum.Invoke($null, @([double]0)) -eq 1) 'Zero balance chart has a nonzero axis.'
Assert-True (-not [double]::IsNaN($niceMaximum.Invoke($null, @([double]::Epsilon)))) 'Subnormal values cannot create a NaN axis.'
Assert-True ($parseThreshold.Invoke($null, @('2.50','USD')) -eq 2.5) 'USD threshold accepts decimal amounts.'
Assert-True ($parseThreshold.Invoke($null, @('0','credits')) -eq 0) 'Exhausted-balance zero threshold is valid.'
foreach ($invalid in @('', '-1', 'NaN', 'Infinity', '1,2', '1e9', '1000000001')) {
    $rejected = $false
    try { $parseThreshold.Invoke($null, @($invalid,'USD')) | Out-Null } catch { $rejected = $true }
    Assert-True $rejected 'Malformed or excessive absolute thresholds must be rejected.'
}
$first = New-Point 0 80; $next = New-Point 60 75
$first.ResolutionMinutes = 60; $next.ResolutionMinutes = 60
Assert-True ($connect.Invoke($null, @($first, $next, [double]30))) 'Hourly representatives form a trend without pretending they were five-minute measurements.'
$summary = $assembly.GetType('UsageWidget.WidgetWindow').GetMethod('UsageNoticeSummary', $flags)
$noticeType = $assembly.GetType('UsageWidget.UsageNotice')
$noticeListType = [Collections.Generic.List``1].MakeGenericType($noticeType)
$notices = [Activator]::CreateInstance($noticeListType)
foreach ($id in @('codex', 'eleven', 'claude')) {
    $notice = [Activator]::CreateInstance($noticeType)
    $notice.ServiceId = $id; $notice.Label = 'Weekly'; $notice.Kind = 'reset-late'; $notice.Message = 'Should not emit individual catch-up messages.'
    $notices.Add($notice)
}
$summaryArgs = New-Object object[] 1
$summaryArgs[0] = $notices
$message = [string]$summary.Invoke($null, $summaryArgs)
Assert-True ($message.Contains('초기화 일정 3개') -and $message.Contains('실제 잔량은 새로고침')) 'Missed schedules are summarized with their count and an explicit request to verify actual remaining usage.'
Assert-True (-not $message.Contains('individual catch-up')) 'The catch-up summary cannot produce one message body per old schedule.'
Assert-True ($message.Contains('Codex') -and $message.Contains('ElevenLabs') -and $message.Contains('Claude')) 'A multi-service catch-up summary identifies affected services.'
$notice = [Activator]::CreateInstance($noticeType)
$notice.ServiceId = 'codex'; $notice.Kind = 'low'; $notice.Label = 'Weekly'; $notice.Message = 'Low usage'
$notices.Add($notice)
$message = [string]$summary.Invoke($null, $summaryArgs)
Assert-True ($message.Contains('외 1개')) 'Mixed current and missed notices retain the additional-notice count and direct the user to history.'
Write-Output 'PASS: CSV formula protection and escaping; trend gaps, manual/source/service changes, resets and capacity changes.'
