using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace GladosPdmAddin
{
    /// <summary>
    /// The signed-in Windows user's Glados server URL and personal access token, entered on the
    /// Glados tab of a vault view (GladosTabControl). The token lives in the Windows Credential
    /// Manager as a generic credential (target "Glados.PdmAddin", user name "PAT", persisted per
    /// user on this computer, blob = the token as UTF-16LE), the same format the SolidWorks
    /// add-in uses under its own target, so the two add-ins never share or overwrite each
    /// other's token. The URL is not a secret and sits in HKCU\Software\Glados\PdmAddin. Both
    /// are read again on every call instead of being cached, so a sign-in in File Explorer
    /// reaches the hooks of a SOLIDWORKS process that is already running; a Credential Manager
    /// read is a local call and triggers are rare enough for that to cost nothing. Nothing
    /// here throws: a failed read is "not signed in", a failed write or delete is logged. The
    /// token itself is never logged.
    /// </summary>
    internal static class GladosCredentials
    {
        private const string CredentialTarget = "Glados.PdmAddin";
        private const string CredentialUserName = "PAT";
        private const string RegistryKeyPath = @"Software\Glados\PdmAddin";
        private const string ServerUrlValueName = "ServerUrl";

        private const int CredTypeGeneric = 1;
        private const int CredPersistLocalMachine = 2;
        private const int ErrorNotFound = 1168;

        /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE: Windows rejects anything larger.</summary>
        private const int MaxBlobBytes = 5 * 512;

        /// <summary>True when both a server URL and a token are stored.</summary>
        public static bool TryGet(out string serverUrl, out string token)
        {
            serverUrl = GetServerUrl();
            token = string.IsNullOrEmpty(serverUrl) ? null : GetToken();
            return !string.IsNullOrEmpty(serverUrl) && !string.IsNullOrEmpty(token);
        }

        public static string GetServerUrl()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath))
                {
                    var value = key == null ? null : key.GetValue(ServerUrlValueName) as string;
                    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                }
            }
            catch (Exception ex)
            {
                Logging.Log("Credentials", 0, null, "server URL could not be read: " + ex.Message);
                return null;
            }
        }

        public static bool SaveServerUrl(string serverUrl)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath))
                {
                    key.SetValue(ServerUrlValueName, serverUrl.Trim(), RegistryValueKind.String);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logging.Log("Credentials", 0, null, "server URL not saved: " + ex.Message);
                return false;
            }
        }

        public static bool HasToken()
        {
            return !string.IsNullOrEmpty(GetToken());
        }

        public static string GetToken()
        {
            try
            {
                IntPtr credentialPointer;
                if (!CredRead(CredentialTarget, CredTypeGeneric, 0, out credentialPointer))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorNotFound)
                        Logging.Log("Credentials", 0, null, "token could not be read: " + new Win32Exception(error).Message);
                    return null;
                }

                try
                {
                    var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
                    if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                        return null;

                    var token = Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
                    return string.IsNullOrWhiteSpace(token) ? null : token;
                }
                finally
                {
                    CredFree(credentialPointer);
                }
            }
            catch (Exception ex)
            {
                Logging.Log("Credentials", 0, null, "token could not be read: " + ex.GetType().Name);
                return null;
            }
        }

        public static bool SaveToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            var blob = Encoding.Unicode.GetBytes(token.Trim());
            if (blob.Length > MaxBlobBytes)
            {
                Logging.Log("Credentials", 0, null, "token not saved: longer than Credential Manager allows");
                return false;
            }

            var blobPointer = IntPtr.Zero;
            try
            {
                blobPointer = Marshal.AllocHGlobal(blob.Length);
                Marshal.Copy(blob, 0, blobPointer, blob.Length);

                var credential = new NativeCredential
                {
                    Type = CredTypeGeneric,
                    TargetName = CredentialTarget,
                    UserName = CredentialUserName,
                    CredentialBlobSize = blob.Length,
                    CredentialBlob = blobPointer,
                    Persist = CredPersistLocalMachine,
                };

                if (CredWrite(ref credential, 0))
                    return true;

                Logging.Log("Credentials", 0, null, "token not saved: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                return false;
            }
            catch (Exception ex)
            {
                Logging.Log("Credentials", 0, null, "token not saved: " + ex.GetType().Name);
                return false;
            }
            finally
            {
                if (blobPointer != IntPtr.Zero)
                {
                    // Zero the unmanaged copy of the token before releasing it.
                    for (var i = 0; i < blob.Length; i++)
                        Marshal.WriteByte(blobPointer, i, 0);
                    Marshal.FreeHGlobal(blobPointer);
                }
                Array.Clear(blob, 0, blob.Length);
            }
        }

        /// <summary>Signs out: removes the token, keeps the server URL for the next
        /// sign-in.</summary>
        public static void DeleteToken()
        {
            try
            {
                if (!CredDelete(CredentialTarget, CredTypeGeneric, 0))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorNotFound)
                        Logging.Log("Credentials", 0, null, "token could not be deleted: " + new Win32Exception(error).Message);
                }
            }
            catch (Exception ex)
            {
                Logging.Log("Credentials", 0, null, "token could not be deleted: " + ex.GetType().Name);
            }
        }

        /// <summary>CREDENTIALW. Only the fields this class sets or reads matter; the rest stay
        /// zero, which is what the Cred* API expects for a plain generic credential.</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref NativeCredential credential, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll", SetLastError = false)]
        private static extern void CredFree(IntPtr buffer);
    }
}
