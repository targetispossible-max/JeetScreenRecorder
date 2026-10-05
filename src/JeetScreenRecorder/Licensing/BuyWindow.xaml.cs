using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace JeetScreenRecorder.Licensing;

/// <summary>
/// Buy License window.
///
/// Flow:
///   1. User fills name / email / mobile.
///   2. Optionally enters a coupon code (validated live against server).
///   3. Chooses payment method: Razorpay (UPI/Card/Netbanking) or Manual UPI.
///   4. App calls /api/payment/create, gets a checkout URL.
///   5. URL opens in system browser.
///   6. App polls /api/payment/status every 5 seconds.
///   7. On "paid", license key is auto-activated (if server sends it),
///      or the user is told to check email.
/// </summary>
public partial class BuyWindow : Window
{
    private readonly LicenseService _lic;
    private bool _busy;
    private string? _pendingRef;
    private string? _pendingToken;
    private string? _validatedCoupon;
    private DispatcherTimer? _pollTimer;
    private int _pollCount;
    private const int MaxPollCount = 60; // 5 min max

    public BuyWindow(LicenseService lic)
    {
        _lic = lic;
        InitializeComponent();

        // Pre-fill pending payment if user re-opened window
        if (_lic.PendingPayment.HasValue)
        {
            (_pendingRef, _pendingToken) = _lic.PendingPayment.Value;
            ShowPendingPaymentUI();
        }
    }

    // ----------------------------------------------------------------- Coupon validation

    private async void BtnValidateCoupon_Click(object sender, RoutedEventArgs e)
    {
        var code = txtCoupon.Text?.Trim().ToUpperInvariant() ?? "";
        if (string.IsNullOrEmpty(code)) return;

        SetCouponBusy(true);
        try
        {
            var api  = new LicenseApiClient();
            var resp = await api.ValidateCouponAsync(code, txtEmail.Text?.Trim());
            if (resp.TryGetProperty("valid", out var v) && v.GetBoolean())
            {
                _validatedCoupon = code;
                var final    = resp.TryGetProperty("final_amount",    out var fa) ? fa.GetString() : "";
                var discount = resp.TryGetProperty("discount_amount", out var da) ? da.GetString() : "";
                txtCouponMsg.Text       = $"✔ Coupon applied! Discount: {discount}. You pay: {final}";
                txtCouponMsg.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
            }
            else
            {
                _validatedCoupon = null;
                var msg = resp.TryGetProperty("message", out var m) ? m.GetString() : "Invalid coupon.";
                txtCouponMsg.Text       = "✗ " + msg;
                txtCouponMsg.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0x38, 0x4A));
            }
        }
        catch (LicenseException ex)
        {
            _validatedCoupon = null;
            txtCouponMsg.Text       = "✗ " + ex.Message;
            txtCouponMsg.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0x38, 0x4A));
        }
        finally { SetCouponBusy(false); }
    }

    // ----------------------------------------------------------------- Payment

    private async void BtnRazorpay_Click(object sender, RoutedEventArgs e) =>
        await StartPaymentAsync("razorpay");

    private async void BtnUpi_Click(object sender, RoutedEventArgs e) =>
        await StartPaymentAsync("upi");

    private async Task StartPaymentAsync(string method)
    {
        if (_busy) return;
        if (!ValidateForm()) return;

        SetBusy(true, "Creating payment…");

        var name   = txtName.Text.Trim();
        var email  = txtEmail.Text.Trim();
        var mobile = txtMobile.Text.Trim();
        var coupon = _validatedCoupon; // send only validated coupon

        var (ok, urlOrError, pRef, pToken) = await _lic.StartPaymentAsync(name, email, mobile, method, coupon);

        if (!ok)
        {
            SetBusy(false);
            ShowMessage(urlOrError, error: true);
            return;
        }

        _pendingRef   = pRef;
        _pendingToken = pToken;

        // Open checkout in browser (validated - only http/https URLs from our server)
        if (urlOrError.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            urlOrError.StartsWith("http://",  StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo(urlOrError) { UseShellExecute = true });
        }

        ShowPendingPaymentUI();
        StartPolling();
    }

    // ----------------------------------------------------------------- Polling

    private void StartPolling()
    {
        _pollCount = 0;
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _pollTimer.Start();
    }

    private async Task PollAsync()
    {
        if (_pendingRef == null || _pendingToken == null) return;
        if (++_pollCount > MaxPollCount)
        {
            StopPolling();
            ShowMessage("Payment verification timed out. Check your email or contact support.", error: true);
            SetBusy(false);
            return;
        }

        var (status, msg, granted) = await _lic.PollPaymentStatusAsync(_pendingRef, _pendingToken);

        switch (status)
        {
            case "paid":
                StopPolling();
                SetBusy(false);
                ShowMessage(msg, error: false);
                if (granted) DialogResult = true;
                else ShowMessage(msg + "\n\nPlease enter the key from your email to activate.", error: false);
                break;

            case "failed":
            case "cancelled":
                StopPolling();
                SetBusy(false);
                ShowMessage("Payment " + status + ". Please try again.", error: true);
                ShowMainForm();
                break;

            case "pending":
            case "created":
            default:
                // Keep polling
                txtPayStatus.Text = "Waiting for payment confirmation…  (" + _pollCount + ")";
                break;
        }
    }

    private void StopPolling() { _pollTimer?.Stop(); _pollTimer = null; }

    // ----------------------------------------------------------------- UI state helpers

    private void ShowPendingPaymentUI()
    {
        panelForm.Visibility    = Visibility.Collapsed;
        panelPending.Visibility = Visibility.Visible;
        SetBusy(true, "");
    }

    private void ShowMainForm()
    {
        panelForm.Visibility    = Visibility.Visible;
        panelPending.Visibility = Visibility.Collapsed;
    }

    private bool ValidateForm()
    {
        txtMsg.Text = "";
        var name   = txtName.Text.Trim();
        var email  = txtEmail.Text.Trim();
        var mobile = txtMobile.Text.Trim();

        if (string.IsNullOrEmpty(name))   { ShowMessage("Please enter your name.",         error:true); return false; }
        if (!IsValidEmail(email))          { ShowMessage("Please enter a valid email.",      error:true); return false; }
        if (!IsValidMobile(mobile))        { ShowMessage("Please enter a 10-digit mobile number.", error:true); return false; }
        return true;
    }

    private static bool IsValidEmail(string e)  => Regex.IsMatch(e, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
    private static bool IsValidMobile(string m) => Regex.IsMatch(m.Replace(" ",""), @"^[6-9]\d{9}$");

    private void SetBusy(bool busy, string? label = null)
    {
        _busy = busy;
        btnRazorpay.IsEnabled = !busy;
        btnUpi.IsEnabled      = !busy;
        btnCouponValidate.IsEnabled = !busy;
        txtName.IsEnabled   = !busy;
        txtEmail.IsEnabled  = !busy;
        txtMobile.IsEnabled = !busy;
        txtCoupon.IsEnabled = !busy;
        if (label != null) txtMsg.Text = label;
    }

    private void SetCouponBusy(bool busy)
    {
        btnCouponValidate.IsEnabled = !busy;
        txtCoupon.IsEnabled         = !busy;
    }

    private void ShowMessage(string msg, bool error)
    {
        txtMsg.Text       = msg;
        txtMsg.Foreground = error
            ? new SolidColorBrush(Color.FromRgb(0xF0, 0x38, 0x4A))
            : new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    }

    private void TxtCoupon_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // If the user edits the coupon text, the previous validation is no longer valid
        _validatedCoupon = null;
        txtCouponMsg.Text = "";
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        StopPolling();
        Close();
    }

    private void BtnChangePayment_Click(object sender, RoutedEventArgs e)
    {
        StopPolling();
        _pendingRef = null;
        _pendingToken = null;
        ShowMainForm();
        SetBusy(false);
    }
}
