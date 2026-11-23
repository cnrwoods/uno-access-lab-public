# Execute the restore checker with a fake CLI. No Azure calls or real sleeps.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../config/config.example.ps1"
$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Subscription = 'test-subscription'
$ResourceGroup = 'test-group'
$PostgresServerName = 'original-postgres'
$KeyVaultName = 'test-keyvault'
$script:TargetName = 'restored-postgres'
$script:IdentityId = '/subscriptions/test-subscription/resourceGroups/test-group/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity'
$script:EnvironmentId = '/subscriptions/test-subscription/resourceGroups/test-group/providers/Microsoft.App/managedEnvironments/test-environment'
$script:ParserPython = $null
$script:ParserValidated = $false
$script:RunnerBash = 'C:/Program Files/Git/bin/bash.exe'
$script:RunnerValidated = $false
if (-not (Test-Path -LiteralPath $script:RunnerBash)) { $script:RunnerBash = $null }
$azCommand = Get-Command az -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($azCommand) {
    $cliRoot = Split-Path (Split-Path $azCommand.Source -Parent) -Parent
    $pythonCandidate = Join-Path $cliRoot 'python.exe'
    if (Test-Path -LiteralPath $pythonCandidate) { $script:ParserPython = $pythonCandidate }
}

function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function ArgumentValue([string[]]$Arguments, [string]$Name) {
    $index = [Array]::IndexOf($Arguments, $Name)
    if ($index -lt 0) { throw "Required argument $Name was omitted." }
    return $Arguments[$index + 1]
}
function Start-Sleep { param($Seconds); $script:Waited += $Seconds }
function Assert-ShellRunner([string]$Runner) {
    Assert (-not $Runner.Contains("`r")) 'The Linux shell runner must have LF line endings even in a Windows CRLF checkout.'
    if ($script:Scenario -ne 'healthy' -or -not $script:RunnerBash) { return }
    $runnerFile = Join-Path ([IO.Path]::GetTempPath()) "keycloak-runner-check-$([guid]::NewGuid()).sh"
    try {
        foreach ($expectedExit in @(0, 3)) {
            # Only shell plumbing is executed: stub psql validates argument
            # boundaries, and stub sleep records the delay without sleeping.
            $stubs = @'
SQL_CHECK_EXPECTED='SELECT 1; DO $quoted$ SELECT x; $quoted$;'
set -- "$SQL_CHECK_EXPECTED"
psql() {
    if [ "$#" -ne 6 ] || [ "$6" != "$SQL_CHECK_EXPECTED" ]; then
        return 99
    fi
    return __RESULT__
}
sleep() {
    printf 'FINGAUTH_TEST_SLEEP=%s\n' "$1"
}
'@
            $program = $stubs.Replace('__RESULT__', [string]$expectedExit).Replace("`r`n", "`n") + "`n" + $Runner
            [IO.File]::WriteAllText($runnerFile, $program, (New-Object Text.UTF8Encoding($false)))
            $runnerOutput = & $script:RunnerBash ($runnerFile.Replace('\', '/'))
            $actualExit = $LASTEXITCODE
            $joinedOutput = $runnerOutput -join "`n"
            Assert ($actualExit -eq $expectedExit) "Shell runner lost the psql exit code: expected $expectedExit, received $actualExit."
            Assert ($joinedOutput.Contains("FINGAUTH_RESTORE_CHECK_EXIT=$expectedExit")) 'Shell runner did not emit the actual SQL exit marker.'
            Assert ($joinedOutput.Contains('FINGAUTH_TEST_SLEEP=90') -eq ($expectedExit -ne 0)) 'Only failed SQL checks should wait for diagnostic collection.'
        }
        $script:RunnerValidated = $true
        Write-Host 'PASS: shell runner preserves SQL argument boundaries and exit codes; only failure requests the diagnostic delay.'
    }
    finally { Remove-Item -LiteralPath $runnerFile -ErrorAction SilentlyContinue }
}
function Assert-AzureYamlPayload([string]$PayloadPath) {
    $bytes = [IO.File]::ReadAllBytes($PayloadPath)
    $hasUtf8Bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    Assert (-not $hasUtf8Bom) 'Azure CLI YAML payloads must be UTF-8 without a BOM, including when written by Windows PowerShell 5.1.'
    if ($script:Scenario -ne 'healthy' -or -not $script:ParserPython) { return }

    # Exercise the installed CLI's actual file decoder/YAML loader without
    # constructing an Azure CLI context, opening profiles or making network calls.
    $parserFile = Join-Path ([IO.Path]::GetTempPath()) "keycloak-parser-check-$([guid]::NewGuid()).py"
    $parserCode = @'
import sys
from azure.cli.command_modules.containerapp._decorator_utils import load_yaml_file

job = load_yaml_file(sys.argv[1])
assert job['properties']['configuration']['triggerType'] == 'Manual'
container = job['properties']['template']['containers'][0]
assert container['command'] == ['/bin/sh']
assert container['args'][0] == '-c'
assert 'psql' in container['args'][1]
assert '--command "$1"' in container['args'][1]
assert 'BEGIN TRANSACTION READ ONLY;' in container['args'][-1]
assert all('value' not in secret for secret in job['properties']['configuration']['secrets'])
print('PASS: installed Azure CLI YAML loader parsed the submitted job payload.')
'@
    try {
        [IO.File]::WriteAllText($parserFile, $parserCode, [Text.Encoding]::ASCII)
        $parserOutput = & $script:ParserPython $parserFile $PayloadPath
        Assert ($LASTEXITCODE -eq 0) "Installed Azure CLI YAML parsing failed: $($parserOutput -join ' ')"
        $script:ParserValidated = $true
        Write-Host ($parserOutput -join "`n")
    }
    finally { Remove-Item -LiteralPath $parserFile -ErrorAction SilentlyContinue }
}
function Invoke-Az {
    param([string[]]$Arguments)
    $script:Calls.Add([string[]]$Arguments)
    Assert ((ArgumentValue $Arguments '--subscription') -eq $Subscription) 'Every call must select the configured subscription.'
    switch ($Arguments[0..1] -join ' ') {
        'postgres flexible-server' {
            Assert ($Arguments[2] -eq 'show') 'The original and restored databases must only be inspected.'
            $serverName = ArgumentValue $Arguments '--name'
            Assert ($serverName -in @($PostgresServerName, $script:TargetName)) 'Unexpected server inspected.'
            $server = @{
                id = "/subscriptions/test-subscription/resourceGroups/test-group/providers/Microsoft.DBforPostgreSQL/flexibleServers/$serverName"
                name = $serverName
                state = 'Ready'
                fullyQualifiedDomainName = "$serverName.postgres.database.azure.com"
                network = @{
                    delegatedSubnetResourceId = '/subscriptions/test-subscription/resourceGroups/test-group/providers/Microsoft.Network/virtualNetworks/test-vnet/subnets/postgres'
                    privateDnsZoneArmResourceId = '/subscriptions/test-subscription/resourceGroups/test-group/providers/Microsoft.Network/privateDnsZones/test.private.postgres.database.azure.com'
                    publicNetworkAccess = 'Disabled'
                }
            }
            if ($serverName -eq $script:TargetName) {
                switch ($script:Scenario) {
                    'not-ready' { $server.state = 'Starting' }
                    'different-subnet' { $server.network.delegatedSubnetResourceId += '-other' }
                    'different-dns' { $server.network.privateDnsZoneArmResourceId += '-other' }
                    'same-id' { $server.id = $server.id.Replace($script:TargetName, $PostgresServerName) }
                }
            }
            return ($server | ConvertTo-Json -Depth 8)
        }
        'identity show' {
            if ($script:Scenario -eq 'missing-identity') { return }
            return $script:IdentityId
        }
        'containerapp env' {
            Assert ($Arguments[2] -eq 'show') 'The existing Container Apps environment must not be changed.'
            if ($script:Scenario -eq 'missing-environment') { return }
            return $script:EnvironmentId
        }
        'containerapp job' {
            $jobName = ArgumentValue $Arguments '--name'
            Assert ($jobName -match '^kc-restore-check-[a-f0-9]{8}$') 'Diagnostic resource name must be unique and separate from Keycloak.'
            Assert ($jobName -cne $ContainerAppName) 'The running Keycloak app must never be targeted.'
            if (-not $script:JobName) { $script:JobName = $jobName }
            Assert ($jobName -ceq $script:JobName) 'The check and cleanup must target the same temporary job.'
            switch ($Arguments[2]) {
                'create' {
                    $script:PayloadPath = ArgumentValue $Arguments '--yaml'
                    Assert-AzureYamlPayload $script:PayloadPath
                    $script:Payload = Get-Content -LiteralPath $script:PayloadPath -Raw | ConvertFrom-Json
                    if ($script:Scenario -eq 'create-failure') { throw 'Mock create failed after resource submission.' }
                    return
                }
                'start' {
                    if ($script:Scenario -eq 'start-failure') { throw 'Mock start failed.' }
                    if ($script:Scenario -eq 'missing-execution') { return }
                    return "$jobName-execution"
                }
                'execution' {
                    Assert ($Arguments[3] -eq 'show') 'Only the created execution may be inspected.'
                    Assert ((ArgumentValue $Arguments '--job-execution-name') -ceq "$jobName-execution") 'Polling targeted another execution.'
                    $script:ExecutionReads++
                    switch ($script:Scenario) {
                        'failed' { return 'Failed' }
                        'timeout' { return 'Running' }
                        'logs-unavailable' { return 'Running' }
                        'succeeded-without-logs' { return 'Succeeded' }
                        'sql-failed' { if ($script:ExecutionReads -eq 1) { return 'Running' } }
                        'native-stderr' { if ($script:ExecutionReads -eq 1) { return 'Running' } }
                        'delayed' { if ($script:ExecutionReads -eq 1) { return 'Running' } }
                    }
                    return 'Succeeded'
                }
                'replica' {
                    Assert ($Arguments[3] -eq 'list') 'Only replicas of the diagnostic execution may be inspected.'
                    Assert ((ArgumentValue $Arguments '--execution') -ceq "$jobName-execution") 'Replica inspection targeted another execution.'
                    $script:ReplicaReads++
                    $query = ArgumentValue $Arguments '--query'
                    if ($query -eq '[].name') { return '["restore-check-replica"]' }
                    return '[{"name":"restore-check-replica","containers":[{"name":"restore-check","runningState":"Terminated","runningStateDetails":"Mock diagnostic container terminated","exitCode":1}]}]'
                }
                'logs' {
                    Assert ($Arguments[3] -eq 'show') 'Only diagnostic execution logs may be inspected.'
                    Assert ((ArgumentValue $Arguments '--execution') -ceq "$jobName-execution") 'Log inspection targeted another execution.'
                    Assert ((ArgumentValue $Arguments '--container') -eq 'restore-check') 'Logs must target the SQL check container.'
                    Assert ((ArgumentValue $Arguments '--tail') -eq '100' -and (ArgumentValue $Arguments '--format') -eq 'text') 'Collect only a bounded diagnostic log tail.'
                    $script:LogReads++
                    switch ($script:Scenario) {
                        'failed' { throw 'Mock logs unavailable for failed startup.' }
                        'logs-unavailable' { throw 'Mock logs unavailable while starting.' }
                        'succeeded-without-logs' { throw 'Mock completed replica logs unavailable.' }
                        'sql-failed' { return @('ERROR: Restored portal client is missing', 'FINGAUTH_RESTORE_CHECK_EXIT=3') }
                        'native-stderr' {
                            # Reproduce Windows PowerShell 5.1's native stderr
                            # behavior under the real optional-log redirection.
                            $nativeProbe = Join-Path ([IO.Path]::GetTempPath()) "keycloak-native-stderr-$([guid]::NewGuid()).py"
                            try {
                                $probeCode = "import sys`nsys.stderr.write('Synthetic optional CLI preview warning\\n')`nprint('FINGAUTH_RESTORE_CHECK_EXIT=0')`n"
                                [IO.File]::WriteAllText($nativeProbe, $probeCode, [Text.Encoding]::ASCII)
                                & $script:ParserPython $nativeProbe
                                $script:NativeStderrCompleted = $true
                                Assert ($LASTEXITCODE -eq 0) 'The synthetic native log reader failed.'
                            }
                            finally { Remove-Item -LiteralPath $nativeProbe -ErrorAction SilentlyContinue }
                            return
                        }
                    }
                    return 'FINGAUTH_RESTORE_CHECK_EXIT=0'
                }
                'delete' {
                    Assert ($Arguments -contains '--yes') 'Cleanup must delete only the explicitly named temporary job without prompting.'
                    return
                }
                default { throw "Unexpected job action: $($Arguments[2])." }
            }
        }
        default { throw "Unexpected CLI call: $($Arguments[0..1] -join ' ')." }
    }
}

$path = Join-Path $ProjectRoot 'scripts/14-test-keycloak-restore.ps1'
$parseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$parseErrors)
Assert (-not $parseErrors) 'Restore checker has PowerShell parse errors.'
$checker = [scriptblock]::Create((Get-Content -LiteralPath $path -Raw).Replace('. "$PSScriptRoot/common.ps1"', ''))

$preflightFailures = @('not-ready', 'different-subnet', 'different-dns', 'same-id', 'missing-identity', 'missing-environment')
$executionFailures = @('create-failure', 'start-failure', 'missing-execution', 'failed', 'sql-failed', 'timeout', 'logs-unavailable')
$successScenarios = @('healthy', 'custom-alias', 'delayed', 'succeeded-without-logs', 'what-if')
if ($script:ParserPython) { $successScenarios += 'native-stderr' }
foreach ($script:Scenario in ($successScenarios + $preflightFailures + $executionFailures)) {
    $script:Calls = New-Object 'System.Collections.Generic.List[object]'
    $script:Payload = $null
    $script:PayloadPath = $null
    $script:JobName = $null
    $script:Waited = 0
    $script:ExecutionReads = 0
    $script:ReplicaReads = 0
    $script:LogReads = 0
    $script:NativeStderrCompleted = $false
    $script:ExpectedAlias = if ($script:Scenario -eq 'custom-alias') { 'linkedin' } else { 'linkedin-openid-connect' }
    $caught = $null
    try {
        if ($script:Scenario -eq 'what-if') { & $checker -RestoredServerName $script:TargetName -WhatIf }
        elseif ($script:Scenario -eq 'custom-alias') { & $checker -RestoredServerName $script:TargetName -LinkedInAlias 'linkedin' }
        else { & $checker -RestoredServerName $script:TargetName -WaitTimeoutSeconds $(if ($script:Scenario -in @('timeout', 'logs-unavailable')) { 0 } else { 300 }) }
    }
    catch { $caught = $_ }
    Assert ([bool]$caught -eq ($script:Scenario -in ($preflightFailures + $executionFailures))) "Unexpected outcome for $script:Scenario : $caught"
    $creates = @($script:Calls | Where-Object { $_[0] -eq 'containerapp' -and $_[1] -eq 'job' -and $_[2] -eq 'create' })
    $deletes = @($script:Calls | Where-Object { $_[0] -eq 'containerapp' -and $_[1] -eq 'job' -and $_[2] -eq 'delete' })
    $shouldCreate = $script:Scenario -notin ($preflightFailures + @('what-if'))
    Assert ($creates.Count -eq [int]$shouldCreate -and $deletes.Count -eq [int]$shouldCreate) "Temporary resource creation or cleanup changed for $script:Scenario."
    if ($script:PayloadPath) { Assert (-not (Test-Path -LiteralPath $script:PayloadPath)) 'The temporary job payload was not removed.' }
    if ($script:Scenario -eq 'delayed') { Assert ($script:ExecutionReads -eq 2 -and $script:Waited -gt 0) 'The checker did not wait for completion.' }
    if ($script:Scenario -eq 'timeout') { Assert ($script:ExecutionReads -eq 1 -and $script:Waited -eq 0) 'The timeout is not bounded.' }
    if ($script:Scenario -eq 'sql-failed') { Assert ($script:LogReads -ge 1 -and $script:ExecutionReads -eq 1) 'A nonzero SQL exit marker must fail while the container remains alive for diagnostics.' }
    if ($script:Scenario -eq 'failed') { Assert ($script:ReplicaReads -ge 1) 'A startup failure with unavailable logs must still attempt replica diagnostics.' }
    if ($script:Scenario -eq 'native-stderr') { Assert ($script:NativeStderrCompleted -and $script:ExecutionReads -eq 2) 'Optional native stderr must not interrupt log collection or replace authoritative execution polling.' }
    Assert ($ErrorActionPreference -eq 'Stop') 'Optional log reads must not relax error handling outside their local scope.'
    foreach ($replicaCall in @($script:Calls | Where-Object { $_[0] -eq 'containerapp' -and $_[1] -eq 'job' -and $_[2] -eq 'replica' })) {
        $diagnosticQuery = ArgumentValue $replicaCall '--query'
        Assert ($diagnosticQuery -eq '[].name' -or ($diagnosticQuery.Contains('properties.containers[].{') -and $diagnosticQuery -notmatch '(?i)\b(env|secretRef|value)\b')) 'Replica diagnostics must select status fields without retrieving environment variables or secret values.'
    }

    if ($script:Payload) {
        $job = $script:Payload
        Assert ($job.properties.environmentId -ceq $script:EnvironmentId) 'The diagnostic must use the existing private-network environment.'
        Assert ($job.identity.type -eq 'UserAssigned' -and $job.identity.userAssignedIdentities.PSObject.Properties.Name -contains $script:IdentityId) 'The configured managed identity is missing.'
        Assert (-not $job.properties.configuration.ingress) 'The diagnostic must have no public ingress.'
        Assert ($job.properties.configuration.triggerType -eq 'Manual' -and $job.properties.configuration.replicaRetryLimit -eq 0) 'The check must be one manual execution without retries.'
        $containers = @($job.properties.template.containers)
        Assert ($containers.Count -eq 1 -and $containers[0].image -eq 'docker.io/library/postgres:17-alpine' -and ($containers[0].command -join ' ') -eq '/bin/sh') 'Run only the diagnostic PostgreSQL client wrapper, not a database server.'
        $container = $containers[0]
        $runner = $container.args[1]
        Assert-ShellRunner $runner
        Assert ($container.args[0] -eq '-c' -and $runner -match '\bpsql\b' -and $runner.Contains('--no-password') -and $runner.Contains('ON_ERROR_STOP=1') -and $runner.Contains('--command "$1"')) 'SQL must be passed as a single psql argument, fail on errors, and never prompt for a password.'
        Assert ($runner.Contains('FINGAUTH_RESTORE_CHECK_EXIT=') -and $runner -match 'sleep\s+90' -and $runner -match '\bexit\s+"?\$') 'The wrapper must expose the original SQL exit code and retain failed replicas briefly for diagnostics.'
        $env = @{}
        foreach ($item in $container.env) { $env[$item.name] = $item }
        Assert ($env.PGHOST.value -ceq "$script:TargetName.postgres.database.azure.com") 'The client must only connect to the restored database.'
        Assert ($env.PGUSER.secretRef -and $env.PGPASSWORD.secretRef -and -not $env.PGUSER.value -and -not $env.PGPASSWORD.value) 'Database credentials must only be supplied through secret references.'
        Assert ($env.PGSSLMODE.value -eq 'require' -and $env.PGOPTIONS.value -match 'default_transaction_read_only=on' -and $env.PGOPTIONS.value -match 'statement_timeout=') 'TLS and bounded read-only sessions are required.'
        foreach ($secret in $job.properties.configuration.secrets) {
            Assert (-not $secret.value -and $secret.identity -ceq $script:IdentityId -and $secret.keyVaultUrl -match '^https://test-keyvault\.vault\.azure\.net/secrets/kc-db-(username|password)$') 'Payloads must use existing Key Vault references and contain no inline credentials.'
        }
        $sql = $container.args[-1]
        Assert ($sql -match '(?is)BEGIN\s+(?:TRANSACTION\s+)?READ\s+ONLY' -and $sql -match '(?i)RAISE\s+EXCEPTION' -and $sql -match '(?i)COMMIT') 'Missing restored records must fail a read-only transaction.'
        Assert ($sql.Contains($RealmName) -and $sql.Contains($ManagementClientId) -and $sql.Contains('platform-admin') -and $sql.Contains('user_entity')) 'The check must cover the realm, portal client, provider, role and users.'
        Assert ($sql.Contains("provider_alias = '$script:ExpectedAlias'") -and $sql -notmatch '(?i)\bprovider_id\s*=') 'Check the exact configured alias while allowing Keycloak provider implementation IDs to differ.'
        if ($script:Scenario -eq 'custom-alias') { Assert (-not $sql.Contains("provider_alias = 'linkedin-openid-connect'")) 'An explicit custom alias must replace the default alias.' }
        Assert ($sql -notmatch '(?i)\b(INSERT|UPDATE|DELETE|ALTER|DROP|TRUNCATE|CREATE)\b' -and $sql -notmatch '(?i)identity_provider_config|\bcredential\b') 'The check must not modify data or retrieve secrets.'
    }
}

foreach ($invalidTarget in @($PostgresServerName, $PostgresServerName.ToUpperInvariant(), 'invalid/server')) {
    $script:Calls = New-Object 'System.Collections.Generic.List[object]'
    $caught = $null
    try { & $checker -RestoredServerName $invalidTarget } catch { $caught = $_ }
    Assert ($caught -and $script:Calls.Count -eq 0) 'The production server or an invalid target must be rejected before Azure calls.'
}

if ($script:ParserPython) { Assert $script:ParserValidated 'The installed Azure CLI parser regression check did not run.' }
else { Write-Host 'SKIP: installed Azure CLI bundled Python is unavailable; mandatory payload BOM checks passed.' }
if ($script:RunnerBash) { Assert $script:RunnerValidated 'The available shell runner regression check did not run.' }
else { Write-Host 'SKIP: Git Bash is unavailable; mandatory runner LF line-ending checks passed.' }
Write-Host 'PASS: restored-target isolation, private-network preflight, BOM-free payloads, read-only SQL, Key Vault references, failure diagnostics, manual execution, cleanup, WhatIf and bounded waits. No Azure calls were made.'
