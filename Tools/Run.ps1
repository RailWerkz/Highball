# Builds and runs the game. Extra arguments go to the game (see the README's dev flags).
param([Parameter(ValueFromRemainingArguments = $true)] $GameArgs)

$root = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $root "Highball") -c Release -- @GameArgs
