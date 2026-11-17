using System;

namespace GladosPdmAddin
{
    /// <summary>
    /// Outcome of GladosClient.GetBackendVersionAsync: the Glados version and the pat contract
    /// minor it serves, or why neither is known. Unknown (Glados unreachable, an older Glados
    /// without the version route, an unexpected answer) is kept apart from a known contract on
    /// purpose, because only a contract Glados actually named may lead to the update warning;
    /// a failed version call must never look like an unsupported server.
    /// </summary>
    internal sealed class BackendVersionResult
    {
        public bool Known { get; private set; }
        public string Version { get; private set; }
        public string PatContract { get; private set; }
        public string Detail { get; private set; }

        /// <summary>True only when Glados named a pat contract and it differs from
        /// GladosClient.SupportedPatContract.</summary>
        public bool IsUnsupported
        {
            get { return Known && !string.Equals(PatContract, GladosClient.SupportedPatContract, StringComparison.Ordinal); }
        }

        public static BackendVersionResult Read(string version, string patContract)
        {
            return new BackendVersionResult { Known = true, Version = version, PatContract = patContract };
        }

        public static BackendVersionResult Unknown(string detail)
        {
            return new BackendVersionResult { Detail = detail };
        }
    }
}
