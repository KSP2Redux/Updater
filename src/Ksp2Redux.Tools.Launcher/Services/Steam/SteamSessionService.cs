using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using SteamKit2;
using SteamKit2.Authentication;

namespace Ksp2Redux.Tools.Launcher.Services.Steam;

/// <summary>
/// Answers Steam Guard challenges during a password sign-in. Implemented by the sign-in UI.
/// </summary>
public interface ISteamGuardPrompt
{
    /// <summary>Asks for the code Steam emailed to the player.</summary>
    Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect);

    /// <summary>Asks for the code shown in the Steam Mobile App.</summary>
    Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect);

    /// <summary>
    /// Tells the player to approve the sign-in in the Steam Mobile App.
    /// </summary>
    /// <returns>True to wait for the approval, false to enter a code instead.</returns>
    Task<bool> AcceptDeviceConfirmationAsync();
}

/// <summary>
/// The signed-in Steam account.
/// </summary>
public sealed record SteamAccount(string AccountName, string? PersonaName)
{
    /// <summary>The persona name, or the account name when there is none.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(PersonaName) ? AccountName : PersonaName;
}

/// <summary>
/// The live connection other Steam services use once the player is signed in.
/// </summary>
public sealed record SteamConnection(SteamClient Client, SteamApps Apps, SteamContent Content);

/// <summary>
/// A sign-in that Steam refused, carrying a message the player can act on.
/// </summary>
public class SteamSignInException(string message, EResult result = EResult.Fail, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Steam's result code for the failure.</summary>
    public EResult Result { get; } = result;
}

public interface ISteamSessionService
{
    bool IsSignedIn { get; }

    /// <summary>The signed-in account, or null when not signed in.</summary>
    SteamAccount? Account { get; }

    /// <summary>The live connection, or null when not signed in.</summary>
    SteamConnection? Connection { get; }

    /// <summary>True when a saved login exists that <see cref="TryResumeAsync"/> can use.</summary>
    bool HasSavedLogin { get; }

    /// <summary>The account name of the saved login, or null when there is none.</summary>
    string? SavedAccountName { get; }

    /// <summary>Raised after signing in or out, and when the connection drops. May be raised on a background thread.</summary>
    event EventHandler? SignInChanged;

    /// <summary>
    /// Signs in by having the player scan a QR code with the Steam Mobile App.
    /// </summary>
    /// <param name="onChallengeUrl">Receives the URL to encode as a QR code, again each time Steam rotates it.</param>
    /// <exception cref="SteamSignInException">Steam refused the sign-in.</exception>
    Task SignInWithQrAsync(Action<string> onChallengeUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Signs in with an account name and password, answering Steam Guard through <paramref name="guard"/>.
    /// </summary>
    /// <exception cref="SteamSignInException">Steam refused the sign-in.</exception>
    Task SignInWithCredentialsAsync(string accountName, string password, ISteamGuardPrompt guard, CancellationToken cancellationToken);

    /// <summary>
    /// Signs in again with the saved login, without asking the player anything.
    /// </summary>
    /// <returns>False when there is no saved login or Steam no longer accepts it.</returns>
    Task<bool> TryResumeAsync(CancellationToken cancellationToken);

    /// <summary>Signs out and forgets the saved login.</summary>
    Task SignOutAsync();
}

/// <summary>
/// Signs in to Steam with SteamKit2, without needing the Steam client installed.
/// </summary>
public class SteamSessionService(ISteamLoginStore loginStore, ILogService log) : ISteamSessionService
{
    private const string DEVICE_NAME = "KSP2 Redux Launcher";
    private static readonly TimeSpan CONNECT_TIMEOUT = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SteamClient? _client;
    private CallbackManager? _callbacks;
    private CancellationTokenSource? _pumpCancellation;
    private TaskCompletionSource? _connected;
    private TaskCompletionSource<SteamUser.LoggedOnCallback>? _loggedOn;
    private string? _personaName;

    public bool IsSignedIn => Account is not null && Connection is not null;

    public SteamAccount? Account { get; private set; }

    public SteamConnection? Connection { get; private set; }

    public bool HasSavedLogin => SavedAccountName is not null;

    public string? SavedAccountName => loginStore.Load()?.AccountName;

    public event EventHandler? SignInChanged;

    public async Task SignInWithQrAsync(Action<string> onChallengeUrl, CancellationToken cancellationToken)
    {
        await RunExclusiveAsync(async () =>
        {
            var client = await ConnectAsync(cancellationToken);
            var session = await client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
            {
                DeviceFriendlyName = DEVICE_NAME,
                IsPersistentSession = true
            });

            onChallengeUrl(session.ChallengeURL);
            session.ChallengeURLChanged = () => onChallengeUrl(session.ChallengeURL);

            var result = await PollAsync(session, cancellationToken);
            await LogOnAsync(result.AccountName, result.RefreshToken, result.NewGuardData, cancellationToken);
        }, cancellationToken);
    }

    public async Task SignInWithCredentialsAsync(string accountName, string password, ISteamGuardPrompt guard, CancellationToken cancellationToken)
    {
        await RunExclusiveAsync(async () =>
        {
            var client = await ConnectAsync(cancellationToken);
            var saved = loginStore.Load();
            AuthSession session;
            try
            {
                session = await client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = accountName,
                    Password = password,
                    DeviceFriendlyName = DEVICE_NAME,
                    IsPersistentSession = true,
                    GuardData = string.Equals(saved?.AccountName, accountName, StringComparison.OrdinalIgnoreCase) ? saved?.GuardData : null,
                    Authenticator = new GuardPromptAuthenticator(guard)
                });
            }
            catch (AuthenticationException ex)
            {
                throw new SteamSignInException(DescribeFailure(ex.Result), ex.Result, ex);
            }

            var result = await PollAsync(session, cancellationToken);
            await LogOnAsync(result.AccountName, result.RefreshToken, result.NewGuardData ?? saved?.GuardData, cancellationToken);
        }, cancellationToken);
    }

    public async Task<bool> TryResumeAsync(CancellationToken cancellationToken)
    {
        if (IsSignedIn) return true;
        if (loginStore.Load() is not { } saved) return false;

        try
        {
            await RunExclusiveAsync(async () =>
            {
                // Another resume may have connected while this one waited for its turn.
                if (IsSignedIn) return;
                await ConnectAsync(cancellationToken);
                await LogOnAsync(saved.AccountName, saved.RefreshToken, saved.GuardData, cancellationToken);
            }, cancellationToken);
            return true;
        }
        catch (SteamSignInException ex) when (ex.Result is EResult.InvalidPassword or EResult.AccessDenied
                                                  or EResult.Expired or EResult.InvalidSignature or EResult.Revoked)
        {
            log.Info($"The saved Steam login is no longer valid ({ex.Result}), forgetting it.");
            loginStore.Clear();
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warn($"Couldn't resume the saved Steam login: {ex.Message}");
            return false;
        }
    }

    public async Task SignOutAsync()
    {
        await _gate.WaitAsync();
        try
        {
            loginStore.Clear();
            _client?.GetHandler<SteamUser>()?.LogOff();
            TearDown();
        }
        finally
        {
            _gate.Release();
        }

        SignInChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunExclusiveAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await action();
        }
        catch
        {
            if (!IsSignedIn) TearDown();
            throw;
        }
        finally
        {
            _gate.Release();
        }

        SignInChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<SteamClient> ConnectAsync(CancellationToken cancellationToken)
    {
        TearDown();

        var client = new SteamClient();
        var callbacks = new CallbackManager(client);
        _client = client;
        _callbacks = callbacks;
        _connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => _connected?.TrySetResult());
        callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        callbacks.Subscribe<SteamUser.LoggedOnCallback>(callback => _loggedOn?.TrySetResult(callback));
        callbacks.Subscribe<SteamUser.AccountInfoCallback>(callback => _personaName = callback.PersonaName);

        _pumpCancellation = new CancellationTokenSource();
        var pumpToken = _pumpCancellation.Token;
        _ = Task.Run(async () =>
        {
            while (!pumpToken.IsCancellationRequested)
            {
                try
                {
                    await callbacks.RunWaitCallbackAsync(pumpToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    log.Error("A Steam callback failed.", ex);
                }
            }
        }, pumpToken);

        log.Info("Connecting to Steam.");
        client.Connect();
        await _connected.Task.WaitAsync(CONNECT_TIMEOUT, cancellationToken);
        return client;
    }

    private async Task<AuthPollResult> PollAsync(AuthSession session, CancellationToken cancellationToken)
    {
        try
        {
            return await session.PollingWaitForResultAsync(cancellationToken);
        }
        catch (AuthenticationException ex)
        {
            throw new SteamSignInException(DescribeFailure(ex.Result), ex.Result, ex);
        }
    }

    private async Task LogOnAsync(string accountName, string refreshToken, string? guardData, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("Not connected to Steam.");
        _loggedOn = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetHandler<SteamUser>()!.LogOn(new SteamUser.LogOnDetails
        {
            Username = accountName,
            AccessToken = refreshToken,
            ShouldRememberPassword = true
        });

        var loggedOn = await _loggedOn.Task.WaitAsync(CONNECT_TIMEOUT, cancellationToken);
        if (loggedOn.Result != EResult.OK)
        {
            throw new SteamSignInException(DescribeFailure(loggedOn.Result), loggedOn.Result);
        }

        // The persona name arrives in a separate callback. Give it a moment without blocking sign-in on it.
        for (var i = 0; i < 20 && _personaName is null; i++)
        {
            await Task.Delay(50, cancellationToken);
        }

        loginStore.Save(new SavedSteamLogin(accountName, refreshToken, guardData));
        Account = new SteamAccount(accountName, _personaName);
        Connection = new SteamConnection(client, client.GetHandler<SteamApps>()!, client.GetHandler<SteamContent>()!);
        log.Info($"Signed in to Steam as {accountName}.");
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        _connected?.TrySetException(new SteamSignInException("Couldn't reach Steam. Check your internet connection and try again."));
        _loggedOn?.TrySetException(new SteamSignInException("Steam closed the connection during sign-in. Try again."));

        if (Account is null) return;

        log.Info("Disconnected from Steam.");
        Account = null;
        Connection = null;
        SignInChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TearDown()
    {
        _pumpCancellation?.Cancel();
        _pumpCancellation = null;
        var client = _client;
        _client = null;
        _callbacks = null;
        Account = null;
        Connection = null;
        _personaName = null;
        client?.Disconnect();
    }

    internal static string DescribeFailure(EResult result) => result switch
    {
        EResult.InvalidPassword => "The account name or password is incorrect.",
        EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle =>
            "Steam has temporarily blocked sign-ins after too many attempts. Wait a while and try again.",
        EResult.Expired or EResult.InvalidSignature or EResult.Revoked => "Your saved Steam sign-in has expired. Please sign in again.",
        EResult.FileNotFound => "The QR code expired. A new one has been generated, scan that instead.",
        EResult.TwoFactorCodeMismatch or EResult.InvalidLoginAuthCode => "The Steam Guard code was incorrect.",
        EResult.ServiceUnavailable or EResult.TryAnotherCM or EResult.Timeout => "Steam is unavailable right now. Try again in a moment.",
        _ => $"Steam refused the sign-in ({result})."
    };

    private sealed class GuardPromptAuthenticator(ISteamGuardPrompt prompt) : IAuthenticator
    {
        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) => prompt.GetDeviceCodeAsync(previousCodeWasIncorrect);

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) => prompt.GetEmailCodeAsync(email, previousCodeWasIncorrect);

        public Task<bool> AcceptDeviceConfirmationAsync() => prompt.AcceptDeviceConfirmationAsync();
    }
}
