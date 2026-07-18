using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace UsageDeck.Infrastructure.Security;

public sealed class WindowsCredentialManagerSecretStore(string targetPrefix) : ISecretStore
{
    private const int CredentialBlobMaximumBytes = 2560;
    private const int ErrorNotFound = 1168;
    private const int GenericCredentialType = 1;
    private const int LocalMachinePersistence = 2;
    private readonly string _targetPrefix = ValidateTargetPart(targetPrefix, nameof(targetPrefix));

    public bool Contains(string name)
    {
        string target = this.BuildTarget(name);
        if (!NativeMethods.CredRead(target, GenericCredentialType, 0, out nint credentialPointer))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                return false;
            }

            throw CreateException("Windows Credential Manager could not be read.", error);
        }

        NativeMethods.CredFree(credentialPointer);
        return true;
    }

    public string? Read(string name)
    {
        string target = this.BuildTarget(name);
        if (!NativeMethods.CredRead(target, GenericCredentialType, 0, out nint credentialPointer))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw CreateException("Windows Credential Manager could not be read.", error);
        }

        byte[]? secretBytes = null;
        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == nint.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            if (credential.CredentialBlobSize > CredentialBlobMaximumBytes)
            {
                throw new SecretStoreException(
                    "The saved credential is larger than UsageDeck can process safely.",
                    new InvalidDataException("Credential blob exceeded the supported size."));
            }

            secretBytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
            string secret = Encoding.UTF8.GetString(secretBytes);
            return string.IsNullOrWhiteSpace(secret) ? null : secret;
        }
        finally
        {
            if (secretBytes is not null)
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }

            NativeMethods.CredFree(credentialPointer);
        }
    }

    public void Write(string name, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        string target = this.BuildTarget(name);
        byte[] secretBytes = Encoding.UTF8.GetBytes(secret.Trim());
        if (secretBytes.Length > CredentialBlobMaximumBytes)
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            throw new ArgumentException("The API key is too large to store safely.", nameof(secret));
        }

        nint secretPointer = nint.Zero;
        try
        {
            secretPointer = Marshal.AllocCoTaskMem(secretBytes.Length);
            Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length);
            NativeCredential credential = new()
            {
                Type = GenericCredentialType,
                TargetName = target,
                CredentialBlobSize = secretBytes.Length,
                CredentialBlob = secretPointer,
                Persist = LocalMachinePersistence,
                UserName = "api-key",
                Comment = "API key stored locally by UsageDeck",
            };

            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                throw CreateException(
                    "Windows Credential Manager could not save the API key.",
                    Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            if (secretPointer != nint.Zero)
            {
                Marshal.Copy(new byte[secretBytes.Length], 0, secretPointer, secretBytes.Length);
                Marshal.FreeCoTaskMem(secretPointer);
            }
        }
    }

    public void Delete(string name)
    {
        string target = this.BuildTarget(name);
        if (NativeMethods.CredDelete(target, GenericCredentialType, 0))
        {
            return;
        }

        int error = Marshal.GetLastPInvokeError();
        if (error != ErrorNotFound)
        {
            throw CreateException("Windows Credential Manager could not remove the API key.", error);
        }
    }

    private static SecretStoreException CreateException(string safeMessage, int error) =>
        new(safeMessage, new Win32Exception(error));

    private static string ValidateTargetPart(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string clean = value.Trim();
        if (clean.Length > 128 || clean.Any(character => char.IsControl(character) || character is '\\' or ':'))
        {
            throw new ArgumentException("Credential names contain unsupported characters.", parameterName);
        }

        return clean;
    }

    private string BuildTarget(string name) =>
        $"{this._targetPrefix}/{ValidateTargetPart(name, nameof(name))}";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    private static class NativeMethods
    {
        [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredWrite(ref NativeCredential credential, int flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredRead(string target, int type, int flags, out nint credential);

        [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredDelete(string target, int type, int flags);

        [DllImport("Advapi32.dll")]
        public static extern void CredFree(nint buffer);
    }
}
