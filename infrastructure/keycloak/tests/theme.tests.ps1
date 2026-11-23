# Local structural checks. These do not replace a real FreeMarker/container test.
$ErrorActionPreference = 'Stop'
function Assert($condition, [string]$message) { if (-not $condition) { throw $message } }
$projectRoot = Split-Path $PSScriptRoot -Parent
$theme = Join-Path $projectRoot 'themes/fingauth/login'
$footer = Get-Content (Join-Path $theme 'footer.ftl') -Raw
$properties = Get-Content (Join-Path $theme 'theme.properties') -Raw
$messages = ConvertFrom-StringData (Get-Content (Join-Path $theme 'messages/messages_en.properties') -Raw)

Assert ($properties -match '(?m)^parent=keycloak\.v2\s*$') 'The theme must inherit the pinned Keycloak v2 login implementation.'
Assert ($properties -match '(?m)^styles=css/styles\.css css/fingauth\.css\s*$') 'Load the inherited stylesheet before the custom one.'
foreach ($nativeTemplate in @('login.ftl', 'login-verification.ftl', 'template.ftl', 'field.ftl', 'social-providers.ftl')) {
    Assert (-not (Test-Path -LiteralPath (Join-Path $theme $nativeTemplate))) "Inherit native $nativeTemplate so credentials, validation and social sign-in follow the pinned Keycloak release."
}
Assert ($footer.Contains('Keycloak</a>') -and $messages.fingauthProvidedBy -eq 'Provided by') 'The provider attribution is required.'
Assert ($footer.Contains('rel="noopener noreferrer"')) 'External attribution links must avoid leaking the login URL.'
Assert ($messages.loginTitleHtml -eq 'Fing Auth' -and $messages.loginAccountTitle -eq 'Welcome back.') 'Visible brand and sign-in heading changed.'

foreach ($file in Get-ChildItem -LiteralPath $theme -Filter *.ftl) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    Assert ($text -notmatch '\?no_esc') 'Custom templates must retain normal output escaping.'
    foreach ($match in [regex]::Matches($text, 'msg\("(fingauth[^"\)]+)"\)')) {
        Assert ($messages.ContainsKey($match.Groups[1].Value)) "Missing message key in $($file.Name)."
    }
    $blocks = New-Object 'System.Collections.Generic.Stack[string]'
    foreach ($match in [regex]::Matches($text, '<(?<close>/)?#(?<kind>if|list|macro)\b[^>]*>')) {
        $kind = $match.Groups['kind'].Value
        if ($match.Groups['close'].Success) {
            Assert ($blocks.Count -gt 0 -and $blocks.Pop() -eq $kind) "Unbalanced FreeMarker block in $($file.Name)."
        }
        else { $blocks.Push($kind) }
    }
    Assert ($blocks.Count -eq 0) "Unclosed FreeMarker block in $($file.Name)."
}

$css = Get-Content (Join-Path $theme 'resources/css/fingauth.css') -Raw
Assert ($css.Contains('#16362e') -and $css.Contains('#12644f') -and $css.Contains('#f6f8f4')) 'The theme must use the portal palette.'
Assert ($css -notmatch '(?i)https?://|@import') 'Theme assets should be local, with no third-party requests on the login page.'
Assert ($css.Contains('#kc-login') -and $css.Contains('#kc-social-providers a.pf-v5-c-button')) 'Style both the native credential and social sign-in buttons.'
$logo = Join-Path $theme 'resources/img/fingauth-app-logo.png'
$sourceLogo = Join-Path (Split-Path (Split-Path $projectRoot -Parent) -Parent) 'assets/branding/fingauth-app-logo.png'
Assert ((Get-FileHash -LiteralPath $logo).Hash -eq (Get-FileHash -LiteralPath $sourceLogo).Hash) 'The theme logo does not match the project asset.'
$container = Get-Content (Join-Path $projectRoot 'Containerfile') -Raw
Assert ($container.Contains('COPY --chown=keycloak:keycloak themes/fingauth/ /opt/keycloak/themes/fingauth/')) 'The image must include the custom theme.'
$stages = [regex]::Matches($container, '(?m)^FROM quay\.io/keycloak/keycloak:(\d+\.\d+\.\d+)(?: AS builder)?\s*$')
$config = Get-Content (Join-Path $projectRoot 'config/config.example.ps1') -Raw
$configuredVersion = [regex]::Match($config, '(?m)^\$KeycloakVersion\s*=\s*"(\d+\.\d+\.\d+)"').Groups[1].Value
Assert ($stages.Count -eq 2 -and $configuredVersion -and $stages[0].Groups[1].Value -eq $configuredVersion -and $stages[1].Groups[1].Value -eq $configuredVersion) 'Both image stages and example configuration must pin the same Keycloak version.'
$realm = Get-Content (Join-Path $projectRoot 'realm/realm-fingauth.json') -Raw | ConvertFrom-Json
Assert ($realm.realm -eq 'fingauth' -and $realm.displayName -eq 'Fing Auth' -and $realm.loginTheme -eq 'fingauth') 'The import template must select the theme on the application realm.'
Write-Host 'PASS: native login inheritance, attribution, messages, local assets, palette, image version pins and packaging. Full Keycloak rendering and sign-in still need a live check.'
