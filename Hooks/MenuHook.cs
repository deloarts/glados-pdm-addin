using EPDM.Interop.epdm;

namespace GladosPdmAddin.Hooks
{
    /// <summary>
    /// EdmCmd_Menu handler for the add-in's own re-sync menu command (label set in
    /// AddIn.GetAddInInfo via IEdmCmdMgr5.AddCmd(ResyncCommandId, ...)). Triggers a
    /// re-sync for every selected file; a selected folder reports EdmCmdData.mlObjectID1 == 0
    /// (SOLIDWORKS PDM Professional API Help, "EdmCmdData Structure" topic, section
    /// "EdmCmdType.EdmCmd_Menu") and is skipped by TriggerHook. Support tool only: it does
    /// nothing beyond what any other trigger call does, so a stuck poll or a missed
    /// notification can be nudged manually without waiting for the next scheduled poll.
    /// </summary>
    internal static class MenuHook
    {
        /// <summary>Command id passed to IEdmCmdMgr5.AddCmd; only needs to be unique within
        /// this add-in, since EdmCmd.mlCmdID is how OnCmd tells menu commands apart.</summary>
        public const int ResyncCommandId = 1;

        public static void Handle(EdmCmd poCmd, EdmCmdData[] data)
        {
            if (poCmd.mlCmdID != ResyncCommandId)
                return;

            var vault = poCmd.mpoVault as IEdmVault5;
            if (vault == null || data == null)
                return;

            // mbsStrData2: full path to the selected file or folder (EdmCmdData Structure,
            // EdmCmd_Menu).
            TriggerHook.Handle("Menu", "resync", vault, data, entry => entry.mbsStrData2);
        }
    }
}
