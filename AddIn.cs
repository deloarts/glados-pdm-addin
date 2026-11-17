using System;
using System.Runtime.InteropServices;
using EPDM.Interop.epdm;
using GladosPdmAddin.Hooks;

namespace GladosPdmAddin
{
    /// <summary>
    /// Entry point of the vault add-in. PDM creates one instance per client process, asks it
    /// which hooks it wants in GetAddInInfo and calls OnCmd for every registered hook. Every
    /// EdmCmdData field access in the case comments below is sourced from the SOLIDWORKS PDM
    /// Professional API Help, "EdmCmdData Structure" topic (API_GB.chm, shipped with the PDM
    /// client under the PDM install folder), which documents which members are populated for
    /// each EdmCmdType.
    /// </summary>
    [ComVisible(true)]
    [Guid("C12CB9A9-59E0-4E39-9455-96E105CD00A5")]
    [ProgId("GladosPdmAddin.AddIn")]
    public class AddIn : IEdmAddIn5
    {
        public void GetAddInInfo(ref EdmAddInInfo poInfo, IEdmVault5 poVault, IEdmCmdMgr5 poCmdMgr)
        {
            // Name, company and the first sentence of the description are restated in
            // Properties/AssemblyInfo.cs; keep both in step.
            poInfo.mbsAddInName = "GLADOS PDM Add-in";
            poInfo.mbsCompany = "DELOARTS";
            poInfo.mbsDescription = "Notifies GLADOS about vault changes and shows what GLADOS did with the selected files. Sign in on the GLADOS tab of a vault view.";
            poInfo.mlAddInVersion = 1;
            // Minimum PDM version used by the SOLIDWORKS samples; any current PDM satisfies it.
            poInfo.mlRequiredVersionMajor = 5;
            poInfo.mlRequiredVersionMinor = 2;

            poCmdMgr.AddHook(EdmCmdType.EdmCmd_PostState);
            poCmdMgr.AddHook(EdmCmdType.EdmCmd_PostUnlock);
            poCmdMgr.AddHook(EdmCmdType.EdmCmd_PostDelete);
            poCmdMgr.AddHook(EdmCmdType.EdmCmd_PostMove);
            poCmdMgr.AddHook(EdmCmdType.EdmCmd_PostRename);
            poCmdMgr.AddHook(EdmCmdType.EdmCmd_PostAdd);
            poCmdMgr.AddHook(EdmCmdType.EdmCmd_PostCopy);
            poCmdMgr.AddHook(EdmCmdType.EdmCmd_PreExploreInit);
            poCmdMgr.AddHook(EdmCmdType.EdmCmd_ActivateAPITab);
            poCmdMgr.AddHook(EdmCmdType.EdmCmd_UserTabDelete);
            poCmdMgr.AddHook(EdmCmdType.EdmCmd_SelectItem);
            poCmdMgr.AddHook(EdmCmdType.EdmCmd_DeSelectItem);

            poCmdMgr.AddCmd(MenuHook.ResyncCommandId, "GLADOS: Resync");
        }

        public void OnCmd(ref EdmCmd poCmd, ref EdmCmdData[] ppoData)
        {
            try
            {
                var vault = poCmd.mpoVault as IEdmVault5;

                switch (poCmd.meCmdType)
                {
                    case EdmCmdType.EdmCmd_PostState:
                        // mbsStrData1: path to the file whose state just changed; mbsStrData2:
                        // name of the destination state.
                        TriggerHook.Handle("PostState", "state_change", vault, ppoData, d => d.mbsStrData1, d => d.mbsStrData2);
                        break;

                    case EdmCmdType.EdmCmd_PostUnlock:
                        // mbsStrData1: path to the file that was just checked in. Lets in-place
                        // updates of already-released files reach Glados without waiting for
                        // the next scheduled poll.
                        TriggerHook.Handle("PostUnlock", "check_in", vault, ppoData, d => d.mbsStrData1);
                        break;

                    case EdmCmdType.EdmCmd_PostDelete:
                        // mbsStrData1: path to the file that was just deleted. IEdmVault5.
                        // GetObject typically can no longer resolve a deleted file, so
                        // FileResolver's version/path fallbacks are expected to miss here;
                        // file_id and this path are still sent.
                        TriggerHook.Handle("PostDelete", "delete", vault, ppoData, d => d.mbsStrData1);
                        break;

                    case EdmCmdType.EdmCmd_PostMove:
                        // mbsStrData2: destination path after the move.
                        TriggerHook.Handle("PostMove", "move", vault, ppoData, d => d.mbsStrData2);
                        break;

                    case EdmCmdType.EdmCmd_PostRename:
                        // mbsStrData1/mbsStrData2 are the old/new file name only, not a path,
                        // for this hook. TriggerHook falls back to IEdmFile5.GetLocalPath.
                        TriggerHook.Handle("PostRename", "rename", vault, ppoData, d => null);
                        break;

                    case EdmCmdType.EdmCmd_PostAdd:
                        // mlObjectID1: parent folder id; mlObjectID2: id of the new file;
                        // mbsStrData1: full path to the file. mlLongData1 is 1 for a network
                        // sharing link, which is sent like any other file.
                        TriggerHook.HandleNewFiles("PostAdd", "add", vault, ppoData,
                            d => d.mlObjectID2, d => d.mlObjectID1, d => d.mbsStrData1);
                        break;

                    case EdmCmdType.EdmCmd_PostCopy:
                        // mlObjectID1: destination folder id; mbsStrData2: destination file
                        // path, which names the new copy (mlObjectID2 is only "ID of file", and
                        // mbsStrData1 the source path).
                        TriggerHook.HandleNewFiles("PostCopy", "copy", vault, ppoData,
                            d => FileResolver.TryGetFileIdFromPath(vault, d.mbsStrData2), d => d.mlObjectID1, d => d.mbsStrData2);
                        break;

                    case EdmCmdType.EdmCmd_PreExploreInit:
                        // No EdmCmdData; mpoExtra carries the IEdmCmdMgr6 to add the tab with.
                        ExplorerTabHook.HandlePreExploreInit(poCmd);
                        break;

                    case EdmCmdType.EdmCmd_ActivateAPITab:
                        // mbsStrData3: the unique id the tab was added with.
                        ExplorerTabHook.HandleActivate(ppoData);
                        break;

                    case EdmCmdType.EdmCmd_UserTabDelete:
                        // mbsStrData3: the unique id the tab was added with (API help, vault
                        // view tab example; not listed in the EdmCmdData Structure topic).
                        ExplorerTabHook.HandleUserTabDelete(ppoData);
                        break;

                    case EdmCmdType.EdmCmd_SelectItem:
                        // mlObjectID1: id of the selected item; mbsStrData1: file name (empty
                        // for a folder); mbsStrData2: folder name; mbsStrData3: the unique id
                        // the tab was added with.
                        ExplorerTabHook.HandleSelect(poCmd, ppoData);
                        break;

                    case EdmCmdType.EdmCmd_DeSelectItem:
                        // mbsStrData1: file name; mbsStrData2: folder name; mbsStrData3: the
                        // tab's unique id. No item id for this hook.
                        ExplorerTabHook.HandleDeselect(ppoData);
                        break;

                    case EdmCmdType.EdmCmd_Menu:
                        MenuHook.Handle(poCmd, ppoData);
                        break;

                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                // Nothing may escape into the PDM client.
                Logging.Log("OnCmd", 0, null, "unhandled exception: " + ex.Message);
            }
        }
    }
}
