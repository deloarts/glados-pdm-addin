using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using EPDM.Interop.epdm;

namespace GladosPdmAddin.Hooks
{
    /// <summary>
    /// Adds the Glados tab (GladosTabControl) to the bottom panel of every File Explorer vault
    /// view and keeps track of the instances. PDM sends EdmCmd_PreExploreInit before it opens a
    /// vault view, with an IEdmCmdMgr6 in EdmCmd.mpoExtra and no EdmCmdData; the add-in calls
    /// IEdmCmdMgr6.AddVaultViewTab there with the control's window handle and a unique id of
    /// its own choosing. EdmCmd_ActivateAPITab (tab selected), EdmCmd_SelectItem and
    /// EdmCmd_DeSelectItem (an item selected or deselected in that window, sent only to an
    /// add-in that owns a vault view tab) and EdmCmd_UserTabDelete (Explorer closing) carry
    /// that id in EdmCmdData.mbsStrData3; sources: SOLIDWORKS PDM Professional API Help,
    /// "EdmCmdData Structure" (PreExploreInit, ActivateAPITab, SelectItem with mlObjectID1 the
    /// item id, mbsStrData1 the file name and mbsStrData2 the folder name, DeSelectItem with
    /// the same names but no id) and the "Create Vault View Tab Add-in Example (C#)"
    /// (UserTabDelete, which the structure topic does not list). explorer.exe runs each window on its own thread, so the id map is
    /// locked and a control is disposed on the thread that created it. The 16x16 tab icon PDM
    /// asks for as a PNG path is drawn once into %LOCALAPPDATA%\Glados, since the vault only
    /// distributes the DLL.
    /// </summary>
    internal static class ExplorerTabHook
    {
        private const string TabName = "GLADOS";
        private const string TabToolTip = "GLADOS sign-in and sync history";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, GladosTabControl> Tabs = new Dictionary<string, GladosTabControl>();
        private static int _nextId;

        public static void HandlePreExploreInit(EdmCmd poCmd)
        {
            var cmdMgr = poCmd.mpoExtra as IEdmCmdMgr6;
            if (cmdMgr == null)
                return;

            var id = "glados-tab-" + Interlocked.Increment(ref _nextId);
            GladosTabControl control = null;
            try
            {
                control = new GladosTabControl();
                cmdMgr.AddVaultViewTab(control.Handle.ToInt64(), TabName, EnsureIconPath(), TabToolTip, id);
                lock (SyncRoot)
                {
                    Tabs[id] = control;
                }
                control.RefreshStatus();
            }
            catch (Exception ex)
            {
                Logging.Log("ExploreInit", 0, null, "GLADOS tab not added: " + ex.Message);
                if (control != null)
                    control.Dispose();
            }
        }

        public static void HandleActivate(EdmCmdData[] data)
        {
            var control = Find(data, false);
            if (control != null)
                control.RefreshStatus();
        }

        /// <summary>Forwards every selected item of the call to its tab; the vault name comes
        /// from the command, since the tab needs it for the history call.</summary>
        public static void HandleSelect(EdmCmd poCmd, EdmCmdData[] data)
        {
            var control = Find(data, false);
            if (control == null)
                return;

            var vaultName = SafeGetVaultName(poCmd.mpoVault as IEdmVault5);
            foreach (var entry in data)
                control.SelectItem(vaultName, entry.mlObjectID1, entry.mbsStrData1, entry.mbsStrData2);
        }

        public static void HandleDeselect(EdmCmdData[] data)
        {
            var control = Find(data, false);
            if (control == null)
                return;

            foreach (var entry in data)
                control.DeselectItem(entry.mbsStrData1, entry.mbsStrData2);
        }

        private static string SafeGetVaultName(IEdmVault5 vault)
        {
            try
            {
                return vault != null ? vault.Name : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void HandleUserTabDelete(EdmCmdData[] data)
        {
            var control = Find(data, true);
            if (control == null)
                return;

            try
            {
                if (control.InvokeRequired)
                    control.BeginInvoke((Action)control.Dispose);
                else
                    control.Dispose();
            }
            catch (Exception ex)
            {
                Logging.Log("UserTabDelete", 0, null, "GLADOS tab not disposed: " + ex.Message);
            }
        }

        private static GladosTabControl Find(EdmCmdData[] data, bool remove)
        {
            if (data == null || data.Length == 0 || string.IsNullOrEmpty(data[0].mbsStrData3))
                return null;

            var id = data[0].mbsStrData3;
            lock (SyncRoot)
            {
                GladosTabControl control;
                if (!Tabs.TryGetValue(id, out control))
                    return null;
                if (remove)
                    Tabs.Remove(id);
                return control;
            }
        }

        /// <summary>Path of the tab icon, drawing it on first use: a white "G" on a blue
        /// circle. Returns an empty string when it cannot be written, which costs the tab its
        /// icon but not the tab.</summary>
        private static string EnsureIconPath()
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glados");
                var path = Path.Combine(directory, "pdm-addin-tab.png");
                if (File.Exists(path))
                    return path;

                Directory.CreateDirectory(directory);
                using (var bitmap = new Bitmap(16, 16, PixelFormat.Format32bppArgb))
                using (var graphics = Graphics.FromImage(bitmap))
                using (var fill = new SolidBrush(Color.FromArgb(0x1F, 0x6F, 0xB5)))
                using (var font = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    graphics.Clear(Color.Transparent);
                    graphics.FillEllipse(fill, 0, 0, 15, 15);
                    graphics.DrawString("G", font, Brushes.White, new RectangleF(0, 0, 16, 16), format);

                    var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    bitmap.Save(temporaryPath, ImageFormat.Png);
                    if (!File.Exists(path))
                        File.Move(temporaryPath, path);
                    else
                        File.Delete(temporaryPath);
                }
                return path;
            }
            catch (Exception ex)
            {
                Logging.Log("ExploreInit", 0, null, "tab icon not written: " + ex.Message);
                return string.Empty;
            }
        }
    }
}
