<#
.SYNOPSIS
Starts a llama.cpp server for the knowledge base: the embedding model or the reranker.

.DESCRIPTION
The application calls the servers over their HTTP API. The script runs one server in the foreground. Press Ctrl+C to stop it.
The paths and the GPUs of this machine come from tools/llama.local.json, which Git ignores. Copy tools/llama.example.json to start.
A parameter of the script overrides the file.
The embedding model Qwen3-Embedding-4B needs about 6 GB of video memory. Give it the larger GPU.
The reranker Qwen3-Reranker-0.6B needs about 2 GB. A smaller GPU is enough.
--cache-ram 0 turns off the prompt cache. Without it, the cache of a server grows to 8 GB of RAM.
-ub must hold the longest text: llama.cpp does not split the input of an embedding.

.EXAMPLE
pwsh tools/start-llama.ps1 -Role embedding

.EXAMPLE
pwsh tools/start-llama.ps1 -Role rerank -Device CUDA0
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('embedding', 'rerank')]
    [string] $Role,

    # The folder with llama-server. An empty text takes LlamaPath of the local settings.
    [string] $LlamaPath = '',

    # The folder with the GGUF models. An empty text takes ModelsPath of the local settings.
    [string] $ModelsPath = '',

    # The GPU of the server, for example CUDA0. An empty text takes the device of the role in the local settings, or lets llama.cpp choose.
    [string] $Device = '',

    # The port of the server. 0 keeps the default of the role.
    [int] $Port = 0,

    # The file of the local settings.
    [string] $SettingsPath = (Join-Path $PSScriptRoot 'llama.local.json'),

    # Print the command and do not run it.
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'

$settings = [pscustomobject]@{}
if (Test-Path $SettingsPath) {
    $settings = Get-Content $SettingsPath -Raw | ConvertFrom-Json
}

function Get-Setting([string] $value, [string] $name) {
    if ($value -ne '') { return $value }
    $property = $settings.PSObject.Properties[$name]
    if ($null -ne $property) { return [string] $property.Value }
    return ''
}

$LlamaPath = Get-Setting $LlamaPath 'LlamaPath'
$ModelsPath = Get-Setting $ModelsPath 'ModelsPath'
if ($LlamaPath -eq '' -or $ModelsPath -eq '') {
    throw "Set LlamaPath and ModelsPath in $SettingsPath (see tools/llama.example.json), or pass -LlamaPath and -ModelsPath."
}

$server = Join-Path $LlamaPath 'llama-server'
if ($IsWindows -or $env:OS -eq 'Windows_NT') { $server += '.exe' }

if ($Role -eq 'embedding') {
    $model = Join-Path $ModelsPath (Join-Path 'Qwen3-Embedding-4B-GGUF' 'Qwen3-Embedding-4B-Q8_0.gguf')
    $Device = Get-Setting $Device 'EmbeddingDevice'
    $defaultPort = 8195
    $roleArgs = @('--embedding', '--pooling', 'last', '--alias', 'Qwen3-Embedding-4B-Q8_0')
}
else {
    $model = Join-Path $ModelsPath (Join-Path 'Qwen3-Reranker-0.6B-GGUF' 'qwen3-reranker-0.6b-q8_0.gguf')
    $Device = Get-Setting $Device 'RerankDevice'
    $defaultPort = 8196
    $roleArgs = @('--rerank', '--alias', 'Qwen3-Reranker-0.6B-Q8_0')
}

if ($Port -eq 0) { $Port = $defaultPort }

# Four slots of 2048 tokens. A chunk of the knowledge base holds at most 1800 characters, far below 2048 tokens.
$arguments = @('-m', $model) + $roleArgs + @(
    '-c', '8192', '-np', '4', '-b', '2048', '-ub', '2048',
    '--cache-ram', '0',
    '-ngl', '99',
    '--host', '127.0.0.1', '--port', "$Port"
)
if ($Device -ne '') { $arguments += @('--device', $Device) }

Write-Host "$server $($arguments -join ' ')"
if ($DryRun) { return }

foreach ($path in @($server, $model)) {
    if (-not (Test-Path $path)) { throw "The file $path does not exist." }
}

& $server @arguments
exit $LASTEXITCODE
