using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace InstallerKeeper.Services;

internal enum AuthenticodeStatus
{
    NotApplicable,
    Valid,
    RevocationUnavailable,
    NoEmbeddedSignature,
    Invalid
}

internal sealed record AuthenticodeVerificationResult(
    AuthenticodeStatus Status,
    string Message,
    int? NativeStatus = null,
    string SignerSubject = "");

/// <summary>
/// Verifies supported Windows installer signatures through the Windows trust provider.
/// It never launches the target file or displays trust UI.
/// </summary>
internal static class AuthenticodeVerifier
{
    private const uint UiNone = 2;
    private const uint UnionChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint RevocationCheckNone = 0x10;
    private const uint CacheOnlyUrlRetrieval = 0x1000;
    private const int ErrorSuccess = 0;
    private const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";
    private const int TrustENoSignature = unchecked((int)0x800B0100);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static AuthenticodeVerificationResult Verify(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".msi", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".msix", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".msixbundle", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".appx", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".appxbundle", StringComparison.OrdinalIgnoreCase))
            return new AuthenticodeVerificationResult(AuthenticodeStatus.NotApplicable,
                "No se comprobó Authenticode para este formato.");

        IntPtr fileInfoPointer = IntPtr.Zero;
        var actionId = GenericVerifyV2;
        var fileInfoMarshalled = false;
        var verificationStarted = false;
        var trustData = new WinTrustData
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>(),
            UiChoice = UiNone,
            RevocationChecks = 0,
            UnionChoice = UnionChoiceFile,
            StateAction = StateActionVerify,
            ProviderFlags = RevocationCheckNone | CacheOnlyUrlRetrieval,
            UiContext = 1
        };

        try
        {
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = Path.GetFullPath(filePath),
                FileHandle = IntPtr.Zero,
                KnownSubject = IntPtr.Zero
            }, fileInfoPointer, fDeleteOld: false);
            fileInfoMarshalled = true;
            trustData.FileInfo = fileInfoPointer;

            var status = WinVerifyTrust(new IntPtr(-1), ref actionId, ref trustData);
            verificationStarted = true;
            if (status == ErrorSuccess)
            {
                var signer = CheckSignerAndOnlineRevocation(filePath);
                return signer.Status switch
                {
                    OnlineRevocationStatus.Valid => new AuthenticodeVerificationResult(
                        AuthenticodeStatus.Valid,
                        string.IsNullOrWhiteSpace(signer.CommonName)
                            ? "Firma Authenticode y revocación de toda la cadena válidas según Windows. No se pudo leer el nombre común del firmante."
                            : $"Firma Authenticode y revocación de toda la cadena válidas según Windows; firmante: {signer.CommonName}.",
                        status,
                        signer.CommonName),
                    OnlineRevocationStatus.Revoked => new AuthenticodeVerificationResult(
                        AuthenticodeStatus.Invalid,
                        $"Windows confirmó que el certificado del firmante está revocado{FormatSigner(signer.CommonName)}.",
                        status,
                        signer.CommonName),
                    OnlineRevocationStatus.Unavailable => new AuthenticodeVerificationResult(
                        AuthenticodeStatus.RevocationUnavailable,
                        $"La firma Authenticode es válida según Windows{FormatSigner(signer.CommonName)}, pero no se pudo confirmar la revocación de toda la cadena. El archivo queda en preparación y no debe promocionarse.",
                        status,
                        signer.CommonName),
                    _ => new AuthenticodeVerificationResult(
                        AuthenticodeStatus.Invalid,
                        $"Windows no pudo validar la cadena de certificados de firma{FormatSigner(signer.CommonName)}.",
                        status,
                        signer.CommonName)
                };
            }

            if (IsUnsigned(status))
                return new AuthenticodeVerificationResult(AuthenticodeStatus.NoEmbeddedSignature,
                    "Windows no encontró una firma Authenticode embebida que pueda comprobar.", status);

            return new AuthenticodeVerificationResult(AuthenticodeStatus.Invalid,
                $"Windows rechazó la firma Authenticode (0x{unchecked((uint)status):X8}).", status);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or MarshalDirectiveException or ArgumentException or IOException)
        {
            return new AuthenticodeVerificationResult(AuthenticodeStatus.Invalid,
                "Windows no pudo completar la comprobación Authenticode del archivo.");
        }
        finally
        {
            if (verificationStarted)
            {
                trustData.StateAction = StateActionClose;
                _ = WinVerifyTrust(new IntPtr(-1), ref actionId, ref trustData);
            }

            if (fileInfoMarshalled)
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            if (fileInfoPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    private static SignerRevocationResult CheckSignerAndOnlineRevocation(string filePath)
    {
        IntPtr oidPointer = IntPtr.Zero;
        var commonName = string.Empty;
        try
        {
            // .NET currently exposes signed-file certificate loading through this legacy API.
#pragma warning disable SYSLIB0057
            using var signedCertificate = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            if (signedCertificate.Handle == IntPtr.Zero)
                return new SignerRevocationResult(commonName, OnlineRevocationStatus.Unavailable);

            using var certificate = X509CertificateLoader.LoadCertificate(signedCertificate.Export(X509ContentType.Cert));

            oidPointer = Marshal.StringToHGlobalAnsi("2.5.4.3"); // X.500 commonName
            const uint CertNameAttributeType = 3;
            var requiredCharacters = CertGetNameString(certificate.Handle, CertNameAttributeType, 0,
                oidPointer, IntPtr.Zero, 0);
            if (requiredCharacters > 1)
            {
                var buffer = Marshal.AllocHGlobal(checked((int)requiredCharacters * sizeof(char)));
                try
                {
                    var writtenCharacters = CertGetNameString(certificate.Handle, CertNameAttributeType, 0,
                        oidPointer, buffer, requiredCharacters);
                    if (writtenCharacters > 1)
                        commonName = (Marshal.PtrToStringUni(buffer) ?? string.Empty).Trim();
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(CodeSigningEku));
            // WinVerifyTrust ya validó la firma y su sello de tiempo en el momento de firmar. Un certificado
            // caducado después de firmar no debe invalidar la firma; la revocación de la cadena sigue comprobándose.
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;

            var chainBuilt = chain.Build(certificate);
            var statuses = chain.ChainStatus.Select(chainStatus => chainStatus.Status).ToArray();
            if (statuses.Any(chainStatus => (chainStatus & X509ChainStatusFlags.Revoked) != 0))
                return new SignerRevocationResult(commonName, OnlineRevocationStatus.Revoked);
            if (chainBuilt)
                return new SignerRevocationResult(commonName, OnlineRevocationStatus.Valid);

            const X509ChainStatusFlags unavailableFlags = X509ChainStatusFlags.RevocationStatusUnknown
                | X509ChainStatusFlags.OfflineRevocation;
            if (statuses.Length > 0
                && statuses.Any(chainStatus => (chainStatus & unavailableFlags) != 0)
                && statuses.All(chainStatus => (chainStatus & ~unavailableFlags) == 0))
                return new SignerRevocationResult(commonName, OnlineRevocationStatus.Unavailable);

            if (statuses.Length == 0)
                return new SignerRevocationResult(commonName, OnlineRevocationStatus.Unavailable);

            return new SignerRevocationResult(commonName, OnlineRevocationStatus.Invalid);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or ArgumentException
            or DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or OverflowException)
        {
            return new SignerRevocationResult(commonName, OnlineRevocationStatus.Unavailable);
        }
        finally
        {
            if (oidPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(oidPointer);
        }
    }

    private static string FormatSigner(string commonName) =>
        string.IsNullOrWhiteSpace(commonName) ? string.Empty : $" de {commonName}";

    private enum OnlineRevocationStatus
    {
        Valid,
        Unavailable,
        Revoked,
        Invalid
    }

    private sealed record SignerRevocationResult(string CommonName, OnlineRevocationStatus Status);

    private static bool IsUnsigned(int status)
    {
        return status == TrustENoSignature;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr windowHandle, ref Guid actionId, ref WinTrustData trustData);

    [DllImport("crypt32.dll", EntryPoint = "CertGetNameStringW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CertGetNameString(IntPtr certificateContext, uint nameType, uint flags,
        IntPtr typeParameter, IntPtr name, uint nameCharacters);
}
