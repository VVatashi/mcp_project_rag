<#
.SYNOPSIS
Connects an agent or a client of MCP to the knowledge base.

.DESCRIPTION
The knowledge base serves MCP on http://127.0.0.1:5180/mcp over the transport Streamable HTTP.
The script adds the server project-rag to the settings of one client, for all its projects.
A client with its own command line, such as Claude Code, Codex or Gemini CLI, gets the server through that command.
The script writes the JSON settings of the other clients. It keeps the old file next to the new one with the extension .bak.
The new file loses the comments of the old one.
Claude Desktop starts only local commands, so it gets the bridge tools/mcp-stdio.mjs, which needs Node.js.
The client json prints the entries for any other client and changes nothing.
Restart the client after the change.

.EXAMPLE
pwsh tools/connect-mcp.ps1 -Client claude-code

.EXAMPLE
pwsh tools/connect-mcp.ps1 -Client claude-desktop -DryRun

.EXAMPLE
pwsh tools/connect-mcp.ps1 -Client json
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('claude-code', 'claude-desktop', 'codex', 'gemini', 'cursor', 'vscode', 'windsurf', 'json')]
    [string] $Client,

    # The MCP endpoint of the server.
    [string] $Url = 'http://127.0.0.1:5180/mcp',

    # The bearer token of the server, see Server:Token. An empty text sends no token.
    [string] $Token = $env:PROJECT_RAG_TOKEN,

    # The name of the server in the settings of the client.
    [string] $Name = 'project-rag',

    # The settings file of a client with JSON settings. An empty text takes the usual place of the client.
    [string] $ConfigPath = '',

    # Print the command or the new settings and change nothing.
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'

$onWindows = $IsWindows -or $env:OS -eq 'Windows_NT'
$userHome = [Environment]::GetFolderPath('UserProfile')
$bridge = Join-Path $PSScriptRoot 'mcp-stdio.mjs'

# The folder of the settings of a desktop application: AppData on Windows, Application Support on macOS, .config on Linux.
function Get-AppDataPath([string] $relative) {
    if ($onWindows) { return Join-Path $env:APPDATA $relative }
    if ($IsMacOS) { return Join-Path $userHome (Join-Path 'Library/Application Support' $relative) }
    return Join-Path $userHome (Join-Path '.config' $relative)
}

function Get-Headers {
    if ($Token -eq '') { return $null }
    return [ordered]@{ Authorization = "Bearer $Token" }
}

# Runs the command line of a client. The command first removes an old entry of the server, so a second run changes the address.
function Invoke-Client([string] $command, [string[]] $removeArgs, [string[]] $addArgs) {
    $shown = ($addArgs | ForEach-Object {
            $text = if ($Token -ne '' -and $_ -eq "Authorization: Bearer $Token") { 'Authorization: Bearer <token>' } else { $_ }
            if ($text -match '\s') { "`"$text`"" } else { $text }
        }) -join ' '
    Write-Host "$command $shown"
    if ($DryRun) { return }

    if ($null -eq (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "The command $command is not on PATH. Install the client, or run the command above in a terminal where it works."
    }

    # The removal fails when the client has no such server. That is the usual case of the first run.
    & $command @removeArgs 2>&1 | Out-Null
    & $command @addArgs
    if ($LASTEXITCODE -ne 0) { throw "$command exited with the code $LASTEXITCODE." }
}

# Puts the entry of the server into one section of a JSON settings file and keeps the other settings.
function Set-JsonEntry([string] $path, [string] $section, [System.Collections.IDictionary] $entry) {
    if ($ConfigPath -ne '') { $path = $ConfigPath }

    $config = [ordered]@{}
    if (Test-Path $path) {
        $text = Get-Content $path -Raw
        if ($null -ne $text -and $text.Trim() -ne '') {
            try {
                $config = $text | ConvertFrom-Json -AsHashtable
            }
            catch {
                throw "The file $path is not JSON that the script can read: $($_.Exception.Message) Add the entry by hand, see pwsh tools/connect-mcp.ps1 -Client json."
            }
        }
    }

    if (-not $config.Contains($section) -or $null -eq $config[$section]) { $config[$section] = [ordered]@{} }
    $config[$section][$Name] = $entry
    $json = $config | ConvertTo-Json -Depth 32

    if ($DryRun) {
        Write-Host "${path}:"
        Write-Host $json
        return
    }

    if (Test-Path $path) { Copy-Item $path "$path.bak" -Force }
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    Set-Content $path $json -Encoding utf8NoBOM
    Write-Host "The server $Name is in $path. Restart the client."
}

function Get-HttpEntry([string] $urlKey) {
    $entry = [ordered]@{}
    if ($urlKey -eq 'url') { $entry.type = 'http' }
    $entry[$urlKey] = $Url
    $headers = Get-Headers
    if ($null -ne $headers) { $entry.headers = $headers }
    return $entry
}

function Get-StdioEntry {
    # The full path of node works also when the client starts with a shorter PATH than the terminal.
    $node = Get-Command node -ErrorAction SilentlyContinue
    $environment = [ordered]@{ PROJECT_RAG_URL = $Url }
    if ($Token -ne '') { $environment.PROJECT_RAG_TOKEN = $Token }
    return [ordered]@{
        command = if ($null -ne $node) { $node.Source } else { 'node' }
        args = @($bridge)
        env = $environment
    }
}

switch ($Client) {
    'claude-code' {
        $addArgs = @('mcp', 'add', '--scope', 'user', '--transport', 'http', $Name, $Url)
        if ($Token -ne '') { $addArgs += @('--header', "Authorization: Bearer $Token") }
        Invoke-Client 'claude' @('mcp', 'remove', '--scope', 'user', $Name) $addArgs
    }
    'codex' {
        # Codex reads the token from the variable PROJECT_RAG_TOKEN of its environment and keeps no copy of it.
        $addArgs = @('mcp', 'add', $Name, '--url', $Url)
        if ($Token -ne '') { $addArgs += @('--bearer-token-env-var', 'PROJECT_RAG_TOKEN') }
        Invoke-Client 'codex' @('mcp', 'remove', $Name) $addArgs
    }
    'gemini' {
        $addArgs = @('mcp', 'add', '--scope', 'user', '--transport', 'http')
        if ($Token -ne '') { $addArgs += @('--header', "Authorization: Bearer $Token") }
        Invoke-Client 'gemini' @('mcp', 'remove', '--scope', 'user', $Name) ($addArgs + @($Name, $Url))
    }
    'claude-desktop' {
        Set-JsonEntry (Get-AppDataPath 'Claude/claude_desktop_config.json') 'mcpServers' (Get-StdioEntry)
    }
    'cursor' {
        Set-JsonEntry (Join-Path $userHome '.cursor/mcp.json') 'mcpServers' (Get-HttpEntry 'url')
    }
    'vscode' {
        Set-JsonEntry (Get-AppDataPath 'Code/User/mcp.json') 'servers' (Get-HttpEntry 'url')
    }
    'windsurf' {
        Set-JsonEntry (Join-Path $userHome '.codeium/windsurf/mcp_config.json') 'mcpServers' (Get-HttpEntry 'serverUrl')
    }
    'json' {
        Write-Host 'A client with Streamable HTTP:'
        Write-Host ([ordered]@{ mcpServers = [ordered]@{ $Name = (Get-HttpEntry 'url') } } | ConvertTo-Json -Depth 8)
        Write-Host 'A client that starts only local commands:'
        Write-Host ([ordered]@{ mcpServers = [ordered]@{ $Name = (Get-StdioEntry) } } | ConvertTo-Json -Depth 8)
    }
}
