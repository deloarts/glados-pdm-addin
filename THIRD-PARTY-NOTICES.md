# Third-party notices

The GLADOS PDM Add-in ships a single file, `GladosPdmAddin.dll`. It embeds no third-party code
and uses no NuGet packages; besides the .NET Framework 4.8 it only builds against the
component below.

## Build-time and client-side only (not redistributed)

| Component | License |
| --- | --- |
| SOLIDWORKS PDM API interop assembly `EPDM.Interop.epdm.dll`, from a local SOLIDWORKS PDM client installation | Proprietary, Dassault Systemes SolidWorks Corporation; not part of this repository. The add-in references it by name only: no type definitions are embedded (`EmbedInteropTypes` False) and the DLL is not copied to the build output (`Private` False). At runtime the add-in uses the copy installed with every PDM client. Upload only `GladosPdmAddin.dll` to the vault. |
