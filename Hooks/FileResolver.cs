using System;
using EPDM.Interop.epdm;

namespace GladosPdmAddin.Hooks
{
    /// <summary>
    /// Best-effort lookups used to fill in the trigger call's optional path and version hints.
    /// EdmCmdData already carries a full local path for most hooks this add-in reacts to (see
    /// the per-hook comments in AddIn.OnCmd, sourced from the SOLIDWORKS PDM Professional API
    /// Help "EdmCmdData Structure" topic), but never a version number, so a
    /// IEdmVault5.GetObject lookup is needed for that regardless. The lookup can fail, most
    /// commonly right after EdmCmd_PostDelete once the file is gone from the vault; every
    /// method here catches that and returns null rather than throwing, since Glados treats path
    /// and version as hints only and re-reads the truth itself.
    /// </summary>
    internal static class FileResolver
    {
        /// <summary>Current version of the file, or null when it can no longer be resolved.</summary>
        public static int? TryGetCurrentVersion(IEdmVault5 vault, int fileId)
        {
            try
            {
                var file = vault.GetObject(EdmObjectType.EdmObject_File, fileId) as IEdmFile5;
                if (file == null)
                    return null;
                return file.CurrentVersion;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Current local path of the file in the given folder, or null when it can no
        /// longer be resolved. Used only where EdmCmdData itself does not carry a full path
        /// (EdmCmd_PostRename gives bare file names, not paths).</summary>
        public static string TryGetLocalPath(IEdmVault5 vault, int fileId, int folderId)
        {
            try
            {
                var file = vault.GetObject(EdmObjectType.EdmObject_File, fileId) as IEdmFile5;
                if (file == null)
                    return null;
                return file.GetLocalPath(folderId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Id of the file at a full local path, or 0 when it cannot be resolved. Used
        /// for EdmCmd_PostCopy, whose mlObjectID2 the API help only calls "ID of file", which
        /// leaves open whether it is the source or the new copy; the destination path names the
        /// copy without doubt.</summary>
        public static int TryGetFileIdFromPath(IEdmVault5 vault, string localPath)
        {
            if (string.IsNullOrEmpty(localPath))
                return 0;
            try
            {
                IEdmFolder5 folder;
                var file = vault.GetFileFromPath(localPath, out folder);
                return file != null ? file.ID : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>True when the file is checked in (not checked out, at least version 1),
        /// false when it is not, null when it cannot be resolved. Refreshes the file first, as
        /// the API help asks before reading IEdmFile5.IsLocked. A file just added by a user is
        /// checked out to them in private state until its first check-in.</summary>
        public static bool? TryIsCheckedIn(IEdmVault5 vault, int fileId)
        {
            try
            {
                var file = vault.GetObject(EdmObjectType.EdmObject_File, fileId) as IEdmFile5;
                if (file == null)
                    return null;
                file.Refresh();
                return !file.IsLocked && file.CurrentVersion >= 1;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Converts a full local path into a vault-relative path with backslashes, by
        /// stripping the vault's local root folder prefix (IEdmVault5.RootFolderPath). Falls
        /// back to the input unchanged if it does not start with the root, or if the root
        /// itself cannot be read.</summary>
        public static string ToVaultRelativePath(IEdmVault5 vault, string localPath)
        {
            if (string.IsNullOrEmpty(localPath))
                return null;

            string root;
            try
            {
                root = vault.RootFolderPath;
            }
            catch (Exception)
            {
                return localPath;
            }

            if (!string.IsNullOrEmpty(root) && localPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return localPath.Substring(root.Length).TrimStart('\\');

            return localPath;
        }
    }
}
