param(
    [string]$ConfigurationRoot = 'D:\Containers\solo-box-full\solo-deployment',
    [string]$ButlerUrl = 'http://localhost:34108',
    [string]$SoloUrl = 'http://localhost:34110',
    [string]$Issuer = 'http://localhost:34100/butler',
    [string]$JwksUri = 'http://localhost:34100/butler/.well-known/jwks'
)

# Opt-in API acceptance against the dedicated S-01 BOX fixture. Never print tokens,
# configuration, HTTP bodies, user claims or exception details. Build the solution first.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$hostDll = Join-Path $repo 'src/Solo.Ai.Host/bin/Debug/net10.0/Solo.Ai.Host.dll'
$probeDirectory = Join-Path $repo ('artifacts/ai03-live/' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $probeDirectory -Force
$stage = 'configuration'
$process = $null
$sessionId = $null
$fixtureLogin = 's01-delegation-check@example.invalid'
$targetAudience = 'solo-ai.api'
$subjectAudience = '75b1693c-58bc-4cbd-b4da-a04877c9935e'
$checks = 0
$failed = $false

function Assert-Check([bool]$condition, [string]$name) {
    if (!$condition) { throw 'Live check failed' }
    $script:checks++
    Write-Output "PASS $name"
}

function Request-Token($form) {
    $pair = [Net.WebUtility]::UrlEncode($client.ClientId) + ':' + [Net.WebUtility]::UrlEncode($client.ClientSecret)
    $headers = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($pair)) }
    $response = Invoke-WebRequest "$ButlerUrl/connect/token" -Method Post -Headers $headers -Body $form -SkipHttpErrorCheck -TimeoutSec 15
    if ($response.StatusCode -ne 200) { throw 'Token request failed' }
    ($response.Content | ConvertFrom-Json).access_token
}

function Exchange-Token([string]$audience) {
    Request-Token @{
        grant_type = 'urn:ietf:params:oauth:grant-type:token-exchange'
        subject_token = $subject
        subject_token_type = 'urn:ietf:params:oauth:token-type:jwt'
        actor_token = $actor
        actor_token_type = 'urn:ietf:params:oauth:token-type:access_token'
        requested_token_type = 'urn:ietf:params:oauth:token-type:access_token'
        audience = $audience
    }
}

function Request-Api([string]$method, [string]$path, [string]$token, $body = $null, [bool]$spoofHeader = $false) {
    $headers = @{ 'X-Solo-Ai-Contract-Version' = '2'; 'X-Solo-Ai-Request-Id' = [guid]::NewGuid().ToString() }
    if ($token) { $headers.Authorization = 'Bearer ' + $token }
    if ($spoofHeader) { $headers['X-Solo-User-Id'] = [guid]::NewGuid().ToString() }
    $parameters = @{ Uri = "$apiUrl$path"; Method = $method; Headers = $headers; SkipHttpErrorCheck = $true; TimeoutSec = 15 }
    if ($null -ne $body) { $parameters.Body = $body | ConvertTo-Json -Compress; $parameters.ContentType = 'application/json' }
    Invoke-WebRequest @parameters
}

try {
    if (!(Test-Path -LiteralPath $hostDll)) { throw 'Build required' }
    foreach ($name in @('Microsoft.IdentityModel.Abstractions', 'Microsoft.IdentityModel.Logging', 'Microsoft.IdentityModel.Tokens', 'Microsoft.IdentityModel.JsonWebTokens')) {
        $null = [Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $hostDll) "$name.dll"))
    }
    $config = Get-Content -Raw -LiteralPath (Join-Path $ConfigurationRoot 'Butler/appsettings.Production.json') | ConvertFrom-Json
    $client = @($config.Seed.DefaultOAuthApplications | Where-Object ClientId -eq 'solo-ai-delegation')[0]
    $fixture = @($config.Seed.DefaultUsers | Where-Object Name -eq $fixtureLogin)[0]
    if (!$client.ClientSecret -or !$fixture.Password) { throw 'S-01 fixture missing' }
    $stage = 'start-isolated-api'
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $apiUrl = "http://127.0.0.1:$port"
    $environment = @{
        ASPNETCORE_ENVIRONMENT = 'Development'; DOTNET_ENVIRONMENT = 'Development'; ASPNETCORE_URLS = $apiUrl
        SoloAiAuthentication__Issuer = $Issuer; SoloAiAuthentication__Audience = $targetAudience
        SoloAiAuthentication__AllowedClientId = $client.ClientId; SoloAiAuthentication__JwksUri = $JwksUri
        SoloAiAuthentication__AllowHttpMetadata = 'true'; SoloAiAuthentication__UseFakes = 'false'; Butler__UseFakes = 'false'
        SoloAiAuthentication__ClockSkewSeconds = '5'
        SoloAiStorage__Enabled = 'true'; SoloAiStorage__DatabasePath = (Join-Path $probeDirectory 'chat.db')
        Logging__LogLevel__Default = 'Warning'
    }
    $process = Start-Process -FilePath 'dotnet' -ArgumentList @($hostDll) -WorkingDirectory $probeDirectory -Environment $environment `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $probeDirectory 'host.log') -RedirectStandardError (Join-Path $probeDirectory 'host-error.log')
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($process.HasExited) { throw 'Probe host failed' }
        try { $ready = (Invoke-WebRequest "$apiUrl/health/live" -TimeoutSec 1).StatusCode -eq 200 } catch { }
        if ($ready) { break }
        Start-Sleep -Milliseconds 250
    }
    Assert-Check $ready 'liveness'
    Assert-Check ((Invoke-WebRequest "$apiUrl/health/ready" -SkipHttpErrorCheck).StatusCode -eq 503) 'readiness-closed-without-worker'
    $stage = 'real-solo-session'
    $web = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $login = Invoke-WebRequest "$ButlerUrl/site/api/v1/user/login" -Method Post -WebSession $web -ContentType 'application/json' -Body (@{
        login = $fixtureLogin; password = $fixture.Password; targetServiceId = $subjectAudience
    } | ConvertTo-Json) -SkipHttpErrorCheck -TimeoutSec 20
    if ($login.StatusCode -ne 200) { throw 'Fixture login failed' }
    $code = ($login.Content | ConvertFrom-Json).authorizationCode
    if (!$code) { throw 'Authorization code missing' }
    $response = Invoke-WebRequest "$SoloUrl/?authCode=$code" -WebSession $web -SkipHttpErrorCheck -TimeoutSec 30
    if ($response.StatusCode -ne 200) { throw 'Solo code exchange failed' }
    $subjects = @($web.Cookies.GetCookies([uri]$SoloUrl) | ForEach-Object {
        try {
            $value = [uri]::UnescapeDataString($_.Value)
            $jwt = [Microsoft.IdentityModel.JsonWebTokens.JsonWebToken]::new($value)
            if ($jwt.Audiences -contains $subjectAudience) { $value }
        } catch { }
    } | Select-Object -Unique)
    if ($subjects.Count -ne 1) { throw 'Subject cookie missing or ambiguous' }
    $subject = $subjects[0]
    $subjectJwt = [Microsoft.IdentityModel.JsonWebTokens.JsonWebToken]::new($subject)
    $sessionId = [guid]$subjectJwt.GetPayloadValue[string]('session_id')
    $stage = 'real-token-exchange'
    $actor = Request-Token @{ grant_type = 'client_credentials' }
    $delegated = Exchange-Token $targetAudience
    $token = [Microsoft.IdentityModel.JsonWebTokens.JsonWebToken]::new($delegated)
    Assert-Check ($token.Typ -eq 'at+jwt' -and $token.Issuer -ceq $Issuer -and $token.Audiences -contains $targetAudience) 'live-token-type-issuer-audience'
    $act = $token.GetPayloadValue[string]('act') | ConvertFrom-Json
    Assert-Check ($token.Subject -ceq $subjectJwt.GetPayloadValue[string]('user_id') -and
        $token.GetPayloadValue[string]('user_id') -ceq $token.Subject -and
        $token.GetPayloadValue[string]('client_id') -ceq $client.ClientId -and $act.sub -ceq $client.ClientId) 'live-original-user-and-actor'
    Assert-Check (@($token.Claims | Where-Object Type -eq 'scope').Count -eq 0) 'live-fixture-has-no-scope'
    $stage = 'api-acceptance'
    Assert-Check ((Request-Api GET '/api/v2/chats/current' $delegated).StatusCode -eq 204) 'delegated-token-accepted-by-strict-api'
    Assert-Check ((Request-Api GET '/api/v2/chats/current' '').StatusCode -eq 401) 'missing-token-rejected'
    Assert-Check ((Request-Api GET '/api/v2/chats/current' 'malformed').StatusCode -eq 401) 'malformed-token-rejected'
    Assert-Check ((Request-Api GET '/api/v2/chats/current' $actor).StatusCode -in @(401,403)) 'service-token-rejected'
    $wrongAudience = Exchange-Token 'solo-ai.ai03-negative'
    Assert-Check ((Request-Api GET '/api/v2/chats/current' $wrongAudience).StatusCode -eq 401) 'real-wrong-audience-rejected'
    $chatId = [guid]::NewGuid().ToString()
    $created = Request-Api POST '/api/v2/chats' $delegated @{ contractVersion = 2; chatId = $chatId } $true
    Assert-Check ($created.StatusCode -eq 201) 'create-with-spoofed-header'
    $current = Request-Api GET '/api/v2/chats/current' $delegated
    Assert-Check (($current.Content | ConvertFrom-Json).data.chatId -eq $chatId) 'owner-unchanged-by-header'
    Assert-Check ((Request-Api PATCH "/api/v2/chats/$chatId" $delegated @{ contractVersion = 2; title = 'AI03 probe'; owner = [guid]::NewGuid().ToString() }).StatusCode -eq 400) 'body-owner-rejected'
    Assert-Check ((Request-Api PATCH "/api/v2/chats/$chatId" $delegated @{ contractVersion = 2; title = 'AI03 probe' }).StatusCode -eq 200) 'rename'
    Assert-Check ((Request-Api POST "/api/v2/chats/$chatId/messages" $delegated @{ contractVersion = 2; messageId = [guid]::NewGuid().ToString(); text = 'AI03 synthetic marker' }).StatusCode -eq 503) 'send-closed-without-worker'
    $messages = Request-Api GET "/api/v2/chats/$chatId/messages" $delegated
    Assert-Check (@(($messages.Content | ConvertFrom-Json).data.messages).Count -eq 0) 'no-message-or-run-accepted'
    Assert-Check ((Request-Api DELETE "/api/v2/chats/$chatId" $delegated).StatusCode -eq 204) 'delete'
    Assert-Check ((Request-Api GET "/api/v2/chats/$chatId" $delegated).StatusCode -eq 404) 'deleted-chat-unavailable'
    $stage = 'log-check'
    [string]$logs = (Get-Content -Raw -LiteralPath (Join-Path $probeDirectory 'host.log')) + (Get-Content -Raw -LiteralPath (Join-Path $probeDirectory 'host-error.log'))
    Assert-Check (!$logs.Contains($delegated) -and !$logs.Contains($subject) -and !$logs.Contains($client.ClientSecret) -and !$logs.Contains('AI03 synthetic marker')) 'no-token-secret-or-text-in-host-logs'
} catch {
    Write-Output "FAIL $stage (details suppressed)"
    $failed = $true
} finally {
    if ($sessionId) {
        # Revoke only the freshly-created S-01 fixture session; no account/configuration changes.
        $sql = 'UPDATE "Sessions" SET "Revoked"=TRUE WHERE "Id"=''' + $sessionId.ToString() + ''' AND "UserId" IN (SELECT "Id" FROM "AspNetUsers" WHERE "UserName"=''' + $fixtureLogin + ''');'
        $result = $sql | docker exec -i solo-box-solo-box-postgres psql -U postgres -d Butler -At -v ON_ERROR_STOP=1 2>$null
        if ($LASTEXITCODE -ne 0 -or $result -ne 'UPDATE 1') { Write-Output 'FAIL fixture-session-cleanup'; $failed = $true }
        else { Write-Output 'PASS fixture-session-revoked' }
    }
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
}
if ($failed) { exit 1 }
Write-Output "PASS $checks live API checks; synthetic JWT matrix is reported separately"
