using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Install;

internal static class Authenticode
{
    public static string? SignerThumbprint(string file)
    {
        // X509CertificateLoader accepts certificate encodings, not signed PE files. Keep the
        // PE-specific API here, after WinVerifyTrust has validated trust when it is required.
#pragma warning disable SYSLIB0057
        try { using var certificate = X509Certificate.CreateFromSignedFile(file); return certificate.GetCertHashString(); }
#pragma warning restore SYSLIB0057
        catch (CryptographicException) { return null; }
    }

    public static void VerifyPublisher(string file, string thumbprint)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var info = new TrustFile { Size = Marshal.SizeOf<TrustFile>(), Path = file };
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            var data = new TrustData { Size = Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = pointer, StateAction = 1 };
            try
            {
                int status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                if (status != 0 || !string.Equals(SignerThumbprint(file), thumbprint, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Executable signature or publisher is invalid: {Path.GetFileName(file)} (0x{status:X8}).");
            }
            finally { data.StateAction = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data); }
        }
        finally { Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile { public int Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public int Size; public IntPtr Policy, Sip; public int UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File; public int StateAction; public IntPtr State, Url; public int Flags, Context; public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
