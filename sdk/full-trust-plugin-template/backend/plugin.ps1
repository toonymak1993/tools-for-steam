$ErrorActionPreference = "Stop"

Write-Output "TFS backend ready for $env:TFS_PLUGIN_ID"
Write-Output "Plugin directory: $env:TFS_PLUGIN_DIR"
Write-Output "Data directory: $env:TFS_PLUGIN_DATA_DIR"

while ($null -ne ($line = [Console]::In.ReadLine())) {
    try {
        $request = $line | ConvertFrom-Json
        $result = switch ([string]$request.method) {
            "ping" {
                [ordered]@{
                    message = "pong"
                    pluginId = $env:TFS_PLUGIN_ID
                    received = $request.arguments
                }
            }
            default {
                throw "Unknown backend method: $($request.method)"
            }
        }
        [ordered]@{ tfsRpcId = $request.tfsRpcId; result = $result } |
            ConvertTo-Json -Depth 20 -Compress |
            Write-Output
    }
    catch {
        [ordered]@{ tfsRpcId = $request.tfsRpcId; error = $_.Exception.Message } |
            ConvertTo-Json -Depth 20 -Compress |
            Write-Output
    }
}
