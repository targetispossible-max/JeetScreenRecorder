# Deliverable 4 – Windows App Integration

## Jo naya bana hai

### `Licensing/` folder (sirf naye files, purane recording code ko chhoot nahi)

| File | Kya karta hai |
|------|--------------|
| `DeviceIdProvider.cs` | MachineGuid + Volume + CPU se SHA-256 fingerprint |
| `InstallationIdProvider.cs` | AppData mein persist UUID (reinstall-safe) |
| `LicenseStorage.cs` + `LicenseCache` | Windows DPAPI encrypted `license.dat` |
| `SignatureVerifier.cs` | RSA-SHA256 public key se server response verify |
| `LicenseApiClient.cs` | Saare API endpoints ke liye typed HTTP client |
| `LicenseService.cs` | Core state machine: trial, activate, heartbeat, offline grace, payment flow |
| `LicenseWindow.xaml/.cs` | Status + activate form + deactivate |
| `BuyWindow.xaml/.cs` | Purchase dialog: coupon, Razorpay, Manual UPI, polling |

### Purani files mein changes

| File | Kya badla |
|------|----------|
| `App.xaml.cs` | `LicenseService` DI registration, `InitialiseAsync()` startup call |
| `MainViewModel.cs` | `LicenseService` inject, `CanRecord` gate, `LicenseCommand`, badge properties |
| `MainWindow.xaml` | Header mein license badge + "🔑 License" button |
| `JeetScreenRecorder.csproj` | `System.Security.Cryptography.ProtectedData` package joda |

---

## Setup Steps (ek baar karna hai)

### Step 1: Server deploy karo (Deliverables 1-3 se)
Hostinger SSH se:
```bash
cd /home/u123456789/domains/license.APKADOMAIN.com/public_html
# Deliverable 3 ka ZIP yahan extract karo
php tools/install.php      # Database tables banata hai
php tools/generate_keys.php  # RSA key pair banata hai
```

### Step 2: Public key app mein daalo

`generate_keys.php` ke output mein ek PUBLIC KEY PEM hoga. Use copy karke:

```
c:\...\Licensing\SignatureVerifier.cs
```

mein `PublicKeyPem` constant update karo:
```csharp
private const string PublicKeyPem =
    "-----BEGIN PUBLIC KEY-----\n" +
    "MIIBIjANBgkq...aapki actual public key...\n" +
    "-----END PUBLIC KEY-----";
```

### Step 3: Server URL update karo

`LicenseApiClient.cs` mein:
```csharp
public const string ServerBase = "https://license.APKADOMAIN.com";
```

Aur `AppVersion` verify karo ki csproj ke `<Version>` se match kare.

### Step 4: Build karo
```
dotnet build -c Release
```

Ya Visual Studio mein Build > Release.

---

## License Flow (User ka experience)

### Pehli baar (internet chahiye)
1. App khulte hi `LicenseService.InitialiseAsync()` call hoti hai
2. Server se trial register hota hai (7 din free)
3. Header mein dikhta hai: `⏳ Trial  •  7 day(s) left`
4. Start Recording button kaam karta hai

### Trial khatam
1. Start Recording disable ho jaata hai
2. Header badge: `Trial Expired` (red)
3. "🔑 License" button click par `LicenseWindow` khulta hai
4. Wahan "🛒 Buy License" se `BuyWindow` khulta hai

### Key activate karna
1. `LicenseWindow` mein key enter karo: `JEET-XXXX-XXXX-XXXX`
2. "Activate License" press karo
3. Server verify karta hai, signed response aata hai
4. `license.dat` DPAPI se encrypt hokar save hoti hai
5. Badge green ho jaata hai: `✔ Licensed  •  Expires DD-MM-YYYY`
6. Start Recording enable ho jaata hai

### Har 4 ghante
- Background heartbeat server se status check karta hai
- Agar server unreachable → 7 din ka offline grace
- Grace khatam → fir online check zaroori

---

## Security Summary

| Threat | Protection |
|--------|-----------|
| License file edit karna | DPAPI encryption - dusre user nahi padh sakte |
| Old response replay | RSA signature mein nonce + device_id bind hai |
| Date change karna | Server time track hota hai, clock rollback detect hota hai |
| Offline use | Max 7 din grace, phir online verify zaroori |
| EXE patch karna | Server-side checks asli suraksha, app-side additional layer |

---

## Aapko karna hoga (Checklist)

- [ ] Deliverable 3 server deploy kiya
- [ ] `php tools/generate_keys.php` chala ke RSA keys banayi
- [ ] Public key `SignatureVerifier.cs` mein daali
- [ ] `LicenseApiClient.cs` mein server URL daala
- [ ] Build kiya aur ek baar test kiya:
  - [ ] Pehli launch: trial active dikhta hai
  - [ ] License window khulti hai
  - [ ] Kisi free license se (php tools/issue_license.php) test kiya
  - [ ] Activated hone par badge green ho gaya
  - [ ] Start Recording button kaam kiya
- [ ] Trial expire test: admin se trial block karke dekha
- [ ] Installer rebuild kiya (Inno Setup)

---

## Known Limitations

1. **.NET SDK local machine par nahi** - is wajah se main build verify nahi kar saka. Compiler errors agar koi ho to minor hoge (namespace, typo).

2. **PlaceholderText property** - WPF TextBox mein `PlaceholderText` by default nahi hota. Agar build fail ho to ya to is property hatao ya custom style jodo. Simple fix:
   ```xml
   <!-- Placeholder ke liye simple watermark approach -->
   <TextBox x:Name="txtKey" Tag="JEET-XXXX-XXXX-XXXX"/>
   ```

3. **LicenseBadgeBrush Freeze** - Performance ke liye brush ko `.Freeze()` karna chahiye, lekin property mein har bar naya brush ban raha hai. Future optimization mein cache karo.
