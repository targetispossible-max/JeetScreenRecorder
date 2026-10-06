using System.Windows;
using System.Windows.Media;

namespace JeetScreenRecorder.Licensing;

/// <summary>
/// License status + activation window.
///
/// Shows:
///   • Current status (trial / licensed / expired)
///   • "Enter License Key" form
///   • "Buy License" button → opens the purchase page on our website in the default browser
///   • "Deactivate this PC" for licensed users (frees the device slot)
/// </summary>
public partial class LicenseWindow : Window
{
    private readonly LicenseService _lic;
    private bool _busy;

    public LicenseWindow(LicenseService lic)
    {
        _lic = lic;
        InitializeComponent();
        Refresh();
    }

    // ----------------------------------------------------------------- refresh UI

    private void Refresh()
    {
        // Status text
        txtStatus.Text = _lic.StatusLabel;
        txtStatus.Foreground = _lic.Status switch
        {
            LicenseStatus.Licensed  => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
            LicenseStatus.Offline   => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
            LicenseStatus.TrialActive => new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
            _ => new SolidColorBrush(Color.FromRgb(0xF0, 0x38, 0x4A)),
        };

        // Show/hide sections
        sectionActivate.Visibility  = _lic.CanRecord || _lic.IsExpired ? Visibility.Visible : Visibility.Visible;
        sectionDeactivate.Visibility = _lic.IsLicensed ? Visibility.Visible : Visibility.Collapsed;

        // Details panel
        if (_lic.IsLicensed)
        {
            txtDetail.Text = _lic.LicenseType == "complimentary"
                ? $"Complimentary license valid until {_lic.ExpiresOn}."
                : $"Licensed until {_lic.ExpiresOn} ({_lic.DaysRemaining} day(s) remaining).";
            txtDetail.Foreground = new SolidColorBrush(Color.FromRgb(0xA3, 0xB3, 0xCF));
        }
        else if (_lic.IsTrialActive)
        {
            txtDetail.Text = $"Free trial: {_lic.TrialDaysLeft} day(s) remaining.";
            txtDetail.Foreground = new SolidColorBrush(Color.FromRgb(0xA3, 0xB3, 0xCF));
        }
        else if (_lic.Status == LicenseStatus.TrialExpired)
        {
            txtDetail.Text = "Your free trial has ended. Please buy a license to continue screen recording.";
            txtDetail.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0x38, 0x4A));
        }
        else if (_lic.Status == LicenseStatus.Expired)
        {
            txtDetail.Text = "Your license has expired. Please buy or renew a license to continue screen recording.";
            txtDetail.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0x38, 0x4A));
        }
        else
        {
            txtDetail.Text = "Please activate a license or buy one below.";
            txtDetail.Foreground = new SolidColorBrush(Color.FromRgb(0xA3, 0xB3, 0xCF));
        }
    }

    // ----------------------------------------------------------------- Activate

    private async void BtnActivate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var key = txtKey.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(key))
        {
            ShowMessage("Please enter your license key.", error: true);
            return;
        }

        SetBusy(true, "Activating…");
        var (ok, msg) = await _lic.ActivateAsync(key);
        SetBusy(false);

        if (ok)
        {
            Refresh();
            ShowMessage(msg, error: false);
            txtKey.Text = "";
        }
        else
        {
            ShowMessage(msg, error: true);
        }
    }

    // ----------------------------------------------------------------- Buy

    private void BtnBuy_Click(object sender, RoutedEventArgs e)
    {
        // Buying happens on our website (secure payment page); the app only takes the license key.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LicenseApiClient.BuyPageUrl)
            {
                UseShellExecute = true
            });
            ShowMessage("Opening our website. After you pay, your license key is e-mailed to you. Paste it above and click Activate.", error: false);
        }
        catch (Exception ex)
        {
            Utils.AppLogger.Warn("Could not open the buy page: " + ex.Message);
            ShowMessage("Could not open your browser. Please visit " + LicenseApiClient.BuyPageUrl + " to buy a license.", error: true);
        }
    }

    // ----------------------------------------------------------------- Deactivate

    private async void BtnDeactivate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var result = MessageBox.Show(
            "This will remove the license from this PC so you can activate it on another device.\n\nProceed?",
            "Deactivate License", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        SetBusy(true, "Deactivating…");
        var (ok, msg) = await _lic.DeactivateAsync();
        SetBusy(false);

        Refresh();
        ShowMessage(msg, error: !ok);
    }

    // ----------------------------------------------------------------- helpers

    private void SetBusy(bool busy, string? label = null)
    {
        _busy = busy;
        btnActivate.IsEnabled   = !busy;
        btnBuy.IsEnabled        = !busy;
        btnDeactivate.IsEnabled = !busy;
        txtKey.IsEnabled        = !busy;
        if (label != null) txtMsg.Text = label;
    }

    private void ShowMessage(string msg, bool error)
    {
        txtMsg.Text       = msg;
        txtMsg.Foreground = error
            ? new SolidColorBrush(Color.FromRgb(0xF0, 0x38, 0x4A))
            : new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
