using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using QRCoder;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Steam;

/// <summary>The step the Steam sign-in window is showing.</summary>
public enum SteamSignInStage
{
    /// <summary>The QR code and the password form are both on offer.</summary>
    Choose,

    /// <summary>Waiting for Steam to check the password.</summary>
    Working,

    /// <summary>Steam wants a Steam Guard code typed in.</summary>
    GuardCode,

    /// <summary>Steam wants the sign-in approved in the Steam Mobile App.</summary>
    AwaitingApproval
}

/// <summary>
/// Drives the "Sign in with Steam" window: a QR code for the Steam Mobile App, with an account name
/// and password form as the fallback.
/// </summary>
public partial class SteamSignInViewModel : ViewModelBase, ISteamGuardPrompt
{
    private readonly ISteamSessionService _session;
    private readonly ILogService _log;
    private readonly ILocalizationService _localization;
    private CancellationTokenSource? _qrCancellation;
    private CancellationTokenSource? _credentialsCancellation;
    private TaskCompletionSource<string>? _guardCode;

    public SteamSignInViewModel(ISteamSessionService session, ILogService log, ILocalizationService localization)
    {
        _session = session;
        _log = log;
        _localization = localization;
    }

    /// <summary>Raised when sign-in ends. The argument is true if the player signed in.</summary>
    public event EventHandler<bool>? Completed;

    [ObservableProperty]
    public partial Bitmap? QrCode { get; set; }

    [ObservableProperty]
    public partial string AccountName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GuardCodeInput { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing), nameof(IsWorking), nameof(IsEnteringGuardCode), nameof(IsAwaitingApproval))]
    public partial SteamSignInStage Stage { get; set; } = SteamSignInStage.Choose;

    [ObservableProperty]
    public partial string GuardPrompt { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public bool IsChoosing => Stage == SteamSignInStage.Choose;

    public bool IsWorking => Stage == SteamSignInStage.Working;

    public bool IsEnteringGuardCode => Stage == SteamSignInStage.GuardCode;

    public bool IsAwaitingApproval => Stage == SteamSignInStage.AwaitingApproval;

    /// <summary>Starts the QR sign-in, which renews its code until sign-in ends.</summary>
    public void Start() => _ = RunQrSignInAsync();

    [RelayCommand]
    public async Task SignInWithPassword()
    {
        if (string.IsNullOrWhiteSpace(AccountName) || string.IsNullOrEmpty(Password))
        {
            ErrorMessage = _localization["Steam.SignIn.EnterCredentials"];
            return;
        }

        // Steam allows one sign-in at a time. The QR session restarts if the password fails.
        _qrCancellation?.Cancel();
        _credentialsCancellation = new CancellationTokenSource();
        ErrorMessage = null;
        Stage = SteamSignInStage.Working;

        try
        {
            await _session.SignInWithCredentialsAsync(AccountName.Trim(), Password, this, _credentialsCancellation.Token);
            Password = string.Empty;
            Finish(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"Steam password sign-in failed: {ex.Message}");
            ErrorMessage = ex is SteamSignInException ? ex.UserMessage() : _localization.Format("Steam.SignIn.Failed", ex.UserMessage());
            Stage = SteamSignInStage.Choose;
            _ = RunQrSignInAsync();
        }
    }

    [RelayCommand]
    public void SubmitGuardCode()
    {
        var code = GuardCodeInput.Trim().ToUpperInvariant();
        if (code.Length == 0) return;

        GuardCodeInput = string.Empty;
        Stage = SteamSignInStage.Working;
        _guardCode?.TrySetResult(code);
    }

    /// <summary>Abandons sign-in and raises <see cref="Completed"/> with false.</summary>
    [RelayCommand]
    public void Cancel()
    {
        _qrCancellation?.Cancel();
        _credentialsCancellation?.Cancel();
        _guardCode?.TrySetCanceled();
        Finish(false);
    }

    /// <inheritdoc />
    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
        AskForCode(_localization.Format(previousCodeWasIncorrect ? "Steam.SignIn.EmailCodeWrong" : "Steam.SignIn.EmailCode", email));

    /// <inheritdoc />
    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
        AskForCode(_localization[previousCodeWasIncorrect ? "Steam.SignIn.DeviceCodeWrong" : "Steam.SignIn.DeviceCode"]);

    /// <inheritdoc />
    public async Task<bool> AcceptDeviceConfirmationAsync()
    {
        // Shown before answering, because Steam starts waiting for the approval as soon as this returns.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            GuardPrompt = _localization["Steam.SignIn.Approve"];
            Stage = SteamSignInStage.AwaitingApproval;
        });
        return true;
    }

    private Task<string> AskForCode(string prompt)
    {
        _guardCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            GuardPrompt = prompt;
            Stage = SteamSignInStage.GuardCode;
        });
        return _guardCode.Task;
    }

    private async Task RunQrSignInAsync()
    {
        _qrCancellation?.Cancel();
        var cancellation = _qrCancellation = new CancellationTokenSource();

        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await _session.SignInWithQrAsync(
                    url => Dispatcher.UIThread.Post(() => QrCode = RenderQrCode(url)),
                    cancellation.Token);
                await Dispatcher.UIThread.InvokeAsync(() => Finish(true));
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Info($"Steam QR sign-in restarted: {ex.Message}");
                await Dispatcher.UIThread.InvokeAsync(() => QrCode = null);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void Finish(bool signedIn)
    {
        _qrCancellation?.Cancel();
        Completed?.Invoke(this, signedIn);
    }

    /// <summary>Renders <paramref name="url"/> as a dark-on-white QR code.</summary>
    internal static Bitmap RenderQrCode(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8, [0x11, 0x11, 0x11], [0xFF, 0xFF, 0xFF], drawQuietZones: true);
        using var stream = new MemoryStream(png);
        return new Bitmap(stream);
    }
}
