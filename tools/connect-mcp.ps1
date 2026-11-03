#Requires -Version 7.0
<#
.SYNOPSIS
Connects a client of MCP, such as Claude Code or Cursor, to the knowledge base.

.DESCRIPTION
The knowledge base serves MCP on http://127.0.0.1:5180/mcp over the transport Streamable HTTP.
The script adds the server project-rag to the settings of one client, for all its projects.
A client with its own command line, such as Claude Code, Codex or Gemini CLI, gets the server through that command.
The script writes the JSON settings of the other clients. It keeps the old file next to the new one with the extension .bak.
The new file loses the comments of the old one.
Claude Desktop starts only local commands, so it gets the bridge tools/mcp-stdio.mjs, which needs Node.js.
The client json prints the entries for any other client and changes nothing.
Restart the client after the change.
A client keeps the token as plain text in its settings.

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

    # Print the command or the new entry and change nothing. The output hides the token.
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
# The removal of an absent entry fails on purpose, so a failed native command must not stop the script.
$PSNativeCommandUseErrorActionPreference = $false

$onWindows = $IsWindows -or $env:OS -eq 'Windows_NT'
$userHome = [Environment]::GetFolderPath('UserProfile')
$bridge = Join-Path $PSScriptRoot 'mcp-stdio.mjs'

# The folder of the settings of a desktop application: AppData on Windows, Application Support on macOS, .config on Linux.
function Get-AppDataPath([string] $relative) {
    if ($onWindows) { return Join-Path $env:APPDATA $relative }
    if ($IsMacOS) { return Join-Path $userHome (Join-Path 'Library/Application Support' $relative) }
    return Join-Path $userHome (Join-Path '.config' $relative)
}

# The Store version of Claude Desktop on Windows keeps its settings in the folder of its package.
function Get-ClaudeDesktopPath {
    if ($onWindows) {
        $package = Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $package) { return Join-Path $package.FullName 'LocalCache/Roaming/Claude/claude_desktop_config.json' }
    }

    return Get-AppDataPath 'Claude/claude_desktop_config.json'
}

# Hides the token in a text for the screen.
function Hide-Token([string] $text) {
    if ($Token -eq '') { return $text }
    return $text.Replace("Bearer $Token", 'Bearer <token>').Replace("`"$Token`"", '"<token>"')
}

function Get-Headers {
    if ($Token -eq '') { return $null }
    return [ordered]@{ Authorization = "Bearer $Token" }
}

# Runs the command line of a client. The command first removes an old entry of the server, so a second run changes the address.
function Invoke-Client([string] $command, [string[]] $removeArgs, [string[]] $addArgs) {
    $shown = ($addArgs | ForEach-Object { $text = Hide-Token $_; if ($text -match '\s') { "`"$text`"" } else { $text } }) -join ' '
    Write-Host "$command $shown"
    if ($DryRun) { return }

    if ($null -eq (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "The command $command is not on PATH. Install the client, or run the command above in a terminal where it works."
    }

    & $command @removeArgs 2>&1 | Out-Null
    & $command @addArgs
    if ($LASTEXITCODE -ne 0) { throw "$command exited with the code $LASTEXITCODE." }
}

# Puts the entry of the server into one section of a JSON settings file and keeps the other settings.
function Set-JsonEntry([string] $path, [string] $section, [System.Collections.IDictionary] $entry) {
    if ($ConfigPath -ne '') { $path = $ConfigPath }
    $path = [IO.Path]::GetFullPath($path)

    if ($DryRun) {
        Write-Host "The entry $section.$Name of ${path}:"
        Write-Host (Hide-Token ($entry | ConvertTo-Json -Depth 8))
        return
    }

    $config = [ordered]@{}
    if (Test-Path -LiteralPath $path) {
        $text = Get-Content -LiteralPath $path -Raw
        if ($null -ne $text -and $text.Trim() -ne '') {
            try {
                $config = $text | ConvertFrom-Json -AsHashtable
            }
            catch {
                throw "The file $path is not JSON that the script can read: $($_.Exception.Message) Add the entry by hand, see pwsh tools/connect-mcp.ps1 -Client json."
            }
        }

        Copy-Item -LiteralPath $path "$path.bak" -Force
    }

    if (-not $config.Contains($section) -or $null -eq $config[$section]) { $config[$section] = [ordered]@{} }
    $config[$section][$Name] = $entry

    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($path)) | Out-Null
    Set-Content -LiteralPath $path ($config | ConvertTo-Json -Depth 32) -Encoding utf8NoBOM
    # The file may hold the token, so only its owner reads it.
    if (-not $onWindows) { chmod 600 $path }
    Write-Host "The server $Name is in $path. Restart the client."
}

function Get-HttpEntry([string] $urlKey, [bool] $withType) {
    $entry = [ordered]@{}
    if ($withType) { $entry.type = 'http' }
    $entry[$urlKey] = $Url
    $headers = Get-Headers
    if ($null -ne $headers) { $entry.headers = $headers }
    return $entry
}

function Get-StdioEntry {
    # The real file of node. A version manager may give a link that lives only as long as its shell.
    $node = 'node'
    if ($null -ne (Get-Command node -ErrorAction SilentlyContinue)) {
        $path = & node -p 'process.execPath'
        if ($LASTEXITCODE -eq 0 -and $path) { $node = $path.Trim() }
    }

    $environment = [ordered]@{ PROJECT_RAG_URL = $Url }
    if ($Token -ne '') { $environment.PROJECT_RAG_TOKEN = $Token }
    return [ordered]@{ command = $node; args = @($bridge); env = $environment }
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
        if ($Token -ne '') {
            $addArgs += @('--bearer-token-env-var', 'PROJECT_RAG_TOKEN')
            if ($Token -ne $env:PROJECT_RAG_TOKEN) { Write-Warning 'Codex reads the token from PROJECT_RAG_TOKEN. Set this variable in the environment of Codex.' }
        }
        Invoke-Client 'codex' @('mcp', 'remove', $Name) $addArgs
    }
    'gemini' {
        $addArgs = @('mcp', 'add', '--scope', 'user', '--transport', 'http')
        if ($Token -ne '') { $addArgs += @('--header', "Authorization: Bearer $Token") }
        Invoke-Client 'gemini' @('mcp', 'remove', '--scope', 'user', $Name) ($addArgs + @($Name, $Url))
    }
    'claude-desktop' {
        Set-JsonEntry (Get-ClaudeDesktopPath) 'mcpServers' (Get-StdioEntry)
    }
    'cursor' {
        Set-JsonEntry (Join-Path $userHome '.cursor/mcp.json') 'mcpServers' (Get-HttpEntry 'url' $false)
    }
    'vscode' {
        Set-JsonEntry (Get-AppDataPath 'Code/User/mcp.json') 'servers' (Get-HttpEntry 'url' $true)
    }
    'windsurf' {
        Set-JsonEntry (Join-Path $userHome '.codeium/windsurf/mcp_config.json') 'mcpServers' (Get-HttpEntry 'serverUrl' $false)
    }
    'json' {
        Write-Host 'A client with Streamable HTTP:'
        Write-Host (Hide-Token ([ordered]@{ mcpServers = [ordered]@{ $Name = (Get-HttpEntry 'url' $true) } } | ConvertTo-Json -Depth 8))
        Write-Host 'A client that starts only local commands:'
        Write-Host (Hide-Token ([ordered]@{ mcpServers = [ordered]@{ $Name = (Get-StdioEntry) } } | ConvertTo-Json -Depth 8))
    }
}
