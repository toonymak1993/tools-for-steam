$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

Add-Type -AssemblyName System.Net.Http

$gamesUrl = "https://crackrelease.com/games/"
$homeUrl = "https://crackrelease.com/"
$postsRestUrl = "https://crackrelease.com/wp-json/wp/v2/posts"
$cachePath = Join-Path $env:TFS_PLUGIN_DATA_DIR "crackwatch-cache.json"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$regexOptions = [System.Text.RegularExpressions.RegexOptions]::Singleline -bor
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
$cardPattern = [regex]::new(
    '<div\s+class="p-wrap[^">]*"\s+data-pid="(?<id>\d+)".*?<a\s+class="p-flink"\s+href="(?<url>[^"]+)"\s+title="(?<title>[^"]+)"[^>]*>.*?<img\b[^>]*\bsrc="(?<image>[^"]+)"[^>]*>.*?<div\s+class="cw-card-badge\s+(?<statusClass>is-[^"]+)"[^>]*>(?<badge>[^<]+)</div>',
    $regexOptions)

$httpHandler = New-Object System.Net.Http.HttpClientHandler
$httpHandler.AutomaticDecompression =
    [System.Net.DecompressionMethods]::GZip -bor [System.Net.DecompressionMethods]::Deflate
$httpClient = New-Object System.Net.Http.HttpClient($httpHandler)
$httpClient.Timeout = [TimeSpan]::FromSeconds(30)
$httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 ToolsForSteam-Crackwatch/0.3")
$httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml")
$httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9")

function New-EmptySnapshot {
    return [ordered]@{
        schemaVersion = 3
        sourceUrl = $gamesUrl
        hotSourceUrl = $homeUrl
        fetchedAtUtc = ""
        hotFetchedAtUtc = ""
        checkedAtUtc = ""
        sourceEtag = ""
        sourceLastModified = ""
        totalGames = 0
        totalCracked = 0
        games = @()
        allGames = @()
        hotGames = @()
    }
}

function Get-ObjectProperty {
    param(
        [object]$Value,
        [string]$Name
    )

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.Collections.IDictionary] -and $Value.Contains($Name)) {
        return $Value[$Name]
    }

    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-ObjectPropertyValue {
    param(
        [object]$Value,
        [string]$Name
    )

    $propertyValue = Get-ObjectProperty -Value $Value -Name $Name
    if ($null -eq $propertyValue) {
        return ""
    }

    return [string]$propertyValue
}

function Get-CachedSnapshot {
    if (-not (Test-Path -LiteralPath $cachePath -PathType Leaf)) {
        return New-EmptySnapshot
    }

    try {
        $cached = Get-Content -LiteralPath $cachePath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $cached -or $null -eq (Get-ObjectProperty -Value $cached -Name "games")) {
            return New-EmptySnapshot
        }

        return $cached
    }
    catch {
        return New-EmptySnapshot
    }
}

function Save-Snapshot {
    param([object]$Snapshot)

    $cacheDirectory = Split-Path -Parent $cachePath
    New-Item -ItemType Directory -Path $cacheDirectory -Force | Out-Null
    $temporaryPath = "$cachePath.tmp"
    $json = $Snapshot | ConvertTo-Json -Depth 8 -Compress
    [System.IO.File]::WriteAllText($temporaryPath, $json, $utf8NoBom)

    if (Test-Path -LiteralPath $cachePath -PathType Leaf) {
        $backupPath = "$cachePath.bak"
        if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
            [System.IO.File]::Delete($backupPath)
        }
        [System.IO.File]::Replace($temporaryPath, $cachePath, $backupPath)
        [System.IO.File]::Delete($backupPath)
    }
    else {
        [System.IO.File]::Move($temporaryPath, $cachePath)
    }
}

function Test-CrackReleaseUri {
    param(
        [string]$Value,
        [switch]$Image
    )

    try {
        $uri = New-Object System.Uri($Value)
        if ($uri.Scheme -ne "https" -or $uri.Host -ne "crackrelease.com") {
            return $false
        }

        return -not $Image -or $uri.AbsolutePath.StartsWith(
            "/wp-content/uploads/",
            [System.StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

function ConvertFrom-CrackReleaseHtml {
    param(
        [string]$Html,
        [int]$MaximumCount = 0
    )

    $games = New-Object System.Collections.Generic.List[object]
    $seenIds = @{}
    foreach ($match in $cardPattern.Matches($Html)) {
        $status = $match.Groups["statusClass"].Value.ToLowerInvariant().Replace("is-", "")
        if ($status -notin @("cracked", "uncracked", "unreleased")) {
            continue
        }

        $sourceId = [int]$match.Groups["id"].Value
        if ($seenIds.ContainsKey($sourceId)) {
            continue
        }

        $title = [System.Net.WebUtility]::HtmlDecode($match.Groups["title"].Value).Trim()
        $gameUrl = [System.Net.WebUtility]::HtmlDecode($match.Groups["url"].Value).Trim()
        $imageUrl = [System.Net.WebUtility]::HtmlDecode($match.Groups["image"].Value).Trim()
        $badge = [System.Net.WebUtility]::HtmlDecode($match.Groups["badge"].Value).Trim()
        if ([string]::IsNullOrWhiteSpace($title) -or
            -not (Test-CrackReleaseUri -Value $gameUrl) -or
            -not (Test-CrackReleaseUri -Value $imageUrl -Image)) {
            continue
        }

        if ($title.Length -gt 200) {
            $title = $title.Substring(0, 200)
        }

        $dayOffset = $null
        $offsetMatch = [regex]::Match($badge, '\bD(?<offset>[+-]\d+)\b')
        if ($offsetMatch.Success) {
            $dayOffset = [int]$offsetMatch.Groups["offset"].Value
        }

        $seenIds[$sourceId] = $true
        $games.Add([ordered]@{
            sourceId = $sourceId
            rank = $games.Count + 1
            title = $title
            status = $status
            badge = if ($badge.Length -gt 40) { $badge.Substring(0, 40) } else { $badge }
            dayOffset = $dayOffset
            sourceUrl = $gameUrl
            imageUrl = $imageUrl
        })

        if ($MaximumCount -gt 0 -and $games.Count -ge $MaximumCount) {
            break
        }
    }

    if ($games.Count -eq 0) {
        throw "CrackRelease returned no game cards; its page structure may have changed."
    }

    return $games
}

function ConvertTo-UtcTimestamp {
    param([object]$Value)

    $text = if ($null -eq $Value) { "" } else { [string]$Value }
    if ([string]::IsNullOrWhiteSpace($text)) {
        return ""
    }

    $timestamp = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
        $text,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::AssumeUniversal,
        [ref]$timestamp)) {
        return ""
    }

    return $timestamp.ToUniversalTime().ToString("o")
}

function Get-CrackReleasePostDates {
    param([object[]]$Games)

    $dateMap = @{}
    $sourceIds = @(
        $Games |
            ForEach-Object { [int]$_.sourceId } |
            Sort-Object -Unique
    )

    for ($offset = 0; $offset -lt $sourceIds.Count; $offset += 100) {
        $take = [Math]::Min(100, $sourceIds.Count - $offset)
        $chunk = @($sourceIds[$offset..($offset + $take - 1)])
        $include = $chunk -join ","
        $requestUrl = "$postsRestUrl`?include=$include&per_page=$take&_fields=id,date_gmt,modified_gmt"
        if (-not (Test-CrackReleaseUri -Value $requestUrl)) {
            throw "The CrackRelease date endpoint URL is invalid."
        }

        $request = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Get,
            [System.Uri]$requestUrl)
        $request.Headers.Accept.ParseAdd("application/json")
        try {
            $response = $httpClient.SendAsync(
                $request,
                [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            try {
                if (-not $response.IsSuccessStatusCode) {
                    throw "CrackRelease returned HTTP $([int]$response.StatusCode) for its date metadata."
                }

                $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                $posts = $json | ConvertFrom-Json
                foreach ($post in $posts) {
                    $publishedAtUtc = ConvertTo-UtcTimestamp -Value (Get-ObjectProperty -Value $post -Name "date_gmt")
                    $updatedAtUtc = ConvertTo-UtcTimestamp -Value (Get-ObjectProperty -Value $post -Name "modified_gmt")
                    if ([string]::IsNullOrWhiteSpace($updatedAtUtc)) {
                        $updatedAtUtc = $publishedAtUtc
                    }

                    $dateMap[[string][int]$post.id] = [ordered]@{
                        publishedAtUtc = $publishedAtUtc
                        updatedAtUtc = $updatedAtUtc
                    }
                }
            }
            finally {
                $response.Dispose()
            }
        }
        finally {
            $request.Dispose()
        }
    }

    return $dateMap
}

function Add-GameDates {
    param(
        [object[]]$Games,
        [hashtable]$DateMap,
        [object[]]$CachedGames = @()
    )

    $cachedById = @{}
    foreach ($cachedGame in $CachedGames) {
        $cachedSourceId = Get-ObjectPropertyValue -Value $cachedGame -Name "sourceId"
        if (-not [string]::IsNullOrWhiteSpace($cachedSourceId)) {
            $cachedById[$cachedSourceId] = $cachedGame
        }
    }

    $datedGames = New-Object System.Collections.Generic.List[object]
    foreach ($game in $Games) {
        $sourceId = [string]$game.sourceId
        $publishedAtUtc = ""
        $updatedAtUtc = ""
        if ($DateMap.ContainsKey($sourceId)) {
            $publishedAtUtc = Get-ObjectPropertyValue -Value $DateMap[$sourceId] -Name "publishedAtUtc"
            $updatedAtUtc = Get-ObjectPropertyValue -Value $DateMap[$sourceId] -Name "updatedAtUtc"
        }

        if ($cachedById.ContainsKey($sourceId)) {
            if ([string]::IsNullOrWhiteSpace($publishedAtUtc)) {
                $publishedAtUtc = Get-ObjectPropertyValue -Value $cachedById[$sourceId] -Name "publishedAtUtc"
            }
            if ([string]::IsNullOrWhiteSpace($updatedAtUtc)) {
                $updatedAtUtc = Get-ObjectPropertyValue -Value $cachedById[$sourceId] -Name "updatedAtUtc"
            }
        }

        $datedGames.Add([ordered]@{
            sourceId = $game.sourceId
            rank = $game.rank
            title = $game.title
            status = $game.status
            badge = $game.badge
            dayOffset = $game.dayOffset
            sourceUrl = $game.sourceUrl
            imageUrl = $game.imageUrl
            publishedAtUtc = $publishedAtUtc
            updatedAtUtc = $updatedAtUtc
        })
    }

    return $datedGames
}

function Get-GameSortTicks {
    param([object]$Game)

    $timestamp = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParse(
        (Get-ObjectPropertyValue -Value $Game -Name "updatedAtUtc"),
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::AssumeUniversal,
        [ref]$timestamp)) {
        return $timestamp.UtcDateTime.Ticks
    }

    return [Int64]::MinValue
}

function Get-CrackedGames {
    param([object[]]$AllGames)

    $crackedGames = New-Object System.Collections.Generic.List[object]
    $sortedGames = @(
        $AllGames |
            Where-Object { [string]$_.status -eq "cracked" } |
            Sort-Object -Property `
                @{ Expression = { Get-GameSortTicks -Game $_ }; Descending = $true }, `
                @{ Expression = { [int]$_.rank }; Ascending = $true }
    )
    foreach ($game in $sortedGames) {
        $crackedGames.Add([ordered]@{
            sourceId = $game.sourceId
            rank = $crackedGames.Count + 1
            title = $game.title
            status = $game.status
            badge = $game.badge
            dayOffset = $game.dayOffset
            sourceUrl = $game.sourceUrl
            imageUrl = $game.imageUrl
            publishedAtUtc = Get-ObjectPropertyValue -Value $game -Name "publishedAtUtc"
            updatedAtUtc = Get-ObjectPropertyValue -Value $game -Name "updatedAtUtc"
        })
    }

    return $crackedGames
}

function Get-HotGames {
    param([string]$Html)

    $hotMarker = [regex]::Match($Html, '>\s*Hot Games\s*<', $regexOptions)
    if (-not $hotMarker.Success) {
        throw "CrackRelease did not expose its Hot Games section."
    }

    $hotRemainder = $Html.Substring($hotMarker.Index)
    $nextSection = [regex]::Match($hotRemainder, '>\s*Upcoming Games\s*<', $regexOptions)
    $hotHtml = if ($nextSection.Success) {
        $hotRemainder.Substring(0, $nextSection.Index)
    }
    else {
        $hotRemainder
    }

    return ConvertFrom-CrackReleaseHtml -Html $hotHtml -MaximumCount 5
}

function Invoke-CrackReleaseRefresh {
    $cached = Get-CachedSnapshot
    $cachedSchemaVersion = 0
    [void][int]::TryParse(
        (Get-ObjectPropertyValue -Value $cached -Name "schemaVersion"),
        [ref]$cachedSchemaVersion)
    $cachedAllGames = @(Get-ObjectProperty -Value $cached -Name "allGames")
    $canUseConditionalRequest = $cachedSchemaVersion -ge 3 -and $cachedAllGames.Count -gt 0

    $gamesRequest = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Get,
        [System.Uri]$gamesUrl)
    try {
        if ($canUseConditionalRequest) {
            $etag = Get-ObjectPropertyValue -Value $cached -Name "sourceEtag"
            if (-not [string]::IsNullOrWhiteSpace($etag)) {
                $gamesRequest.Headers.TryAddWithoutValidation("If-None-Match", $etag) | Out-Null
            }

            $lastModified = Get-ObjectPropertyValue -Value $cached -Name "sourceLastModified"
            if (-not [string]::IsNullOrWhiteSpace($lastModified)) {
                $gamesRequest.Headers.TryAddWithoutValidation("If-Modified-Since", $lastModified) | Out-Null
            }
        }

        $gamesResponse = $httpClient.SendAsync(
            $gamesRequest,
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            $now = [DateTimeOffset]::UtcNow.ToString("o")
            if ([int]$gamesResponse.StatusCode -eq 304) {
                $allGames = $cachedAllGames
                $fetchedAtUtc = Get-ObjectPropertyValue -Value $cached -Name "fetchedAtUtc"
                $sourceEtag = Get-ObjectPropertyValue -Value $cached -Name "sourceEtag"
                $sourceLastModified = Get-ObjectPropertyValue -Value $cached -Name "sourceLastModified"
            }
            else {
                if (-not $gamesResponse.IsSuccessStatusCode) {
                    throw "CrackRelease returned HTTP $([int]$gamesResponse.StatusCode) for its games page."
                }

                $gamesHtml = $gamesResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                $allGames = @(ConvertFrom-CrackReleaseHtml -Html $gamesHtml)
                $fetchedAtUtc = $now
                $sourceEtag = if ($null -ne $gamesResponse.Headers.ETag) {
                    $gamesResponse.Headers.ETag.ToString()
                }
                else {
                    ""
                }
                $sourceLastModified = if ($null -ne $gamesResponse.Content.Headers.LastModified) {
                    $gamesResponse.Content.Headers.LastModified.Value.ToString("R")
                }
                else {
                    ""
                }
            }

            $postDateMap = @{}
            try {
                $postDateMap = Get-CrackReleasePostDates -Games $allGames
            }
            catch {
                $postDateMap = @{}
            }
            $allGames = @(Add-GameDates -Games $allGames -DateMap $postDateMap -CachedGames $cachedAllGames)
            $crackedGames = @(Get-CrackedGames -AllGames $allGames)
            if ($crackedGames.Count -eq 0) {
                throw "CrackRelease returned no explicitly cracked games."
            }

            $hotGames = @(Get-ObjectProperty -Value $cached -Name "hotGames")
            $hotFetchedAtUtc = Get-ObjectPropertyValue -Value $cached -Name "hotFetchedAtUtc"
            $homeRequest = [System.Net.Http.HttpRequestMessage]::new(
                [System.Net.Http.HttpMethod]::Get,
                [System.Uri]$homeUrl)
            try {
                $homeResponse = $httpClient.SendAsync(
                    $homeRequest,
                    [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
                try {
                    if ($homeResponse.IsSuccessStatusCode) {
                        $homeHtml = $homeResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        $hotGames = @(Get-HotGames -Html $homeHtml)
                        $hotGames = @(Add-GameDates -Games $hotGames -DateMap $postDateMap -CachedGames @(
                            Get-ObjectProperty -Value $cached -Name "hotGames"))
                        $hotFetchedAtUtc = $now
                    }
                    elseif ($hotGames.Count -eq 0) {
                        throw "CrackRelease returned HTTP $([int]$homeResponse.StatusCode) for its Hot Games page."
                    }
                }
                finally {
                    $homeResponse.Dispose()
                }
            }
            catch {
                if ($hotGames.Count -eq 0) {
                    throw
                }
            }
            finally {
                $homeRequest.Dispose()
            }

            $snapshot = [ordered]@{
                schemaVersion = 3
                sourceUrl = $gamesUrl
                hotSourceUrl = $homeUrl
                fetchedAtUtc = $fetchedAtUtc
                hotFetchedAtUtc = $hotFetchedAtUtc
                checkedAtUtc = $now
                sourceEtag = $sourceEtag
                sourceLastModified = $sourceLastModified
                totalGames = $allGames.Count
                totalCracked = $crackedGames.Count
                games = $crackedGames
                allGames = $allGames
                hotGames = $hotGames
            }
            Save-Snapshot -Snapshot $snapshot
            return $snapshot
        }
        finally {
            $gamesResponse.Dispose()
        }
    }
    finally {
        $gamesRequest.Dispose()
    }
}

try {
    while ($null -ne ($line = [Console]::In.ReadLine())) {
        $request = $null
        $rpcId = ""
        try {
            $request = $line | ConvertFrom-Json
            $rpcId = [string]$request.tfsRpcId
            $result = switch ([string]$request.method) {
                "getSnapshot" {
                    Get-CachedSnapshot
                    break
                }
                "refresh" {
                    Invoke-CrackReleaseRefresh
                    break
                }
                "status" {
                    $cached = Get-CachedSnapshot
                    [ordered]@{
                        sourceUrl = $gamesUrl
                        hotSourceUrl = $homeUrl
                        cachePath = $cachePath
                        hasCache = Test-Path -LiteralPath $cachePath -PathType Leaf
                        fetchedAtUtc = Get-ObjectPropertyValue -Value $cached -Name "fetchedAtUtc"
                        checkedAtUtc = Get-ObjectPropertyValue -Value $cached -Name "checkedAtUtc"
                        totalGames = @(Get-ObjectProperty -Value $cached -Name "allGames").Count
                        totalCracked = @(Get-ObjectProperty -Value $cached -Name "games").Count
                        totalHot = @(Get-ObjectProperty -Value $cached -Name "hotGames").Count
                    }
                    break
                }
                default {
                    throw "Unknown backend method: $($request.method)"
                }
            }

            [ordered]@{ tfsRpcId = $rpcId; result = $result } |
                ConvertTo-Json -Depth 10 -Compress |
                Write-Output
        }
        catch {
            $errorMessage = $_.Exception.Message
            if ($_.InvocationInfo.ScriptLineNumber -gt 0) {
                $errorMessage = "$errorMessage (backend line $($_.InvocationInfo.ScriptLineNumber))"
            }
            [ordered]@{ tfsRpcId = $rpcId; error = $errorMessage } |
                ConvertTo-Json -Depth 5 -Compress |
                Write-Output
        }
    }
}
finally {
    $httpClient.Dispose()
    $httpHandler.Dispose()
}
