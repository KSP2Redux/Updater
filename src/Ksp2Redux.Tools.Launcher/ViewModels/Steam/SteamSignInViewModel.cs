using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using QRCoder;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Steam;

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
    private CancellationTokenSource? _qrCancellation;
    private CancellationTokenSource? _credentialsCancellation;
    private TaskCompletionSource<string>? _guardCode;

    public SteamSignInViewModel(ISteamSessionService session, ILogService log)
    {
        _session = session;
        _log = log;
    }

    /// <summary>Raised once the player is signed in, or has closed the window.</summary>
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

    /// <summary>
    /// Starts the QR sign-in. Called when the window opens.
    /// </summary>
    public void Start() => _ = RunQrSignInAsync();

    [RelayCommand]
    public async Task SignInWithPassword()
    {
        if (string.IsNullOrWhiteSpace(AccountName) || string.IsNullOrEmpty(Password))
        {
            ErrorMessage = "Enter your Steam account name and password.";
            return;
        }

        // Only one sign-in can talk to Steam at a time, so the QR session steps aside while the
        // password is tried, and comes back if it fails.
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
            ErrorMessage = ex is SteamSignInException ? ex.Message : $"Couldn't sign in: {ex.Message}";
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

    [RelayCommand]
    public void Cancel()
    {
        _qrCancellation?.Cancel();
        _credentialsCancellation?.Cancel();
        _guardCode?.TrySetCanceled();
        Finish(false);
    }

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
        AskForCode(previousCodeWasIncorrect
            ? $"That code didn't work. Enter the new code Steam sent to {email}."
            : $"Steam sent a code to {email}. Enter it below.");

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
        AskForCode(previousCodeWasIncorrect
            ? "That code didn't work. Enter the current code from your Steam Mobile App."
            : "Enter the code shown in your Steam Mobile App.");

    public async Task<bool> AcceptDeviceConfirmationAsync()
    {
        // Shown before answering, because Steam starts waiting for the approval as soon as this returns.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            GuardPrompt = "Open the Steam Mobile App and approve the sign-in.";
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
                // A QR session that expires or is declined on the phone just starts over with a new code.
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

    /// <summary>
    /// Draws the Steam challenge URL as a QR code, dark modules on white so any phone camera reads it.
    /// </summary>
    internal static Bitmap RenderQrCode(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8, [0x11, 0x11, 0x11], [0xFF, 0xFF, 0xFF], drawQuietZones: true);
        using var stream = new MemoryStream(png);
        return new Bitmap(stream);
    }
}
