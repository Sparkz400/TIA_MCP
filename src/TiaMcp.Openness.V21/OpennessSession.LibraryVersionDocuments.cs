using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.Types;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        /// <summary>Copy caller documents into an owned snapshot whose complete content hash the apply step re-verifies.</summary>
        private void PrepareLibraryDocumentSnapshot(LibraryChange change)
        {
            var payload = change.Payload;
            string documentDirectory = ChangeText(payload, "documentDirectory", 4096);
            if (!ProjectPathIsAbsolute(documentDirectory) || !Directory.Exists(documentDirectory)) throw new BridgeException("InvalidRequest", "An absolute documentDirectory containing native exported library documents is required.");
            foreach (string name in LibraryDocumentNames.Read(payload)) ValidateLibraryName(name);
            documentDirectory = Path.GetFullPath(documentDirectory);
            string documentsHash = LibraryDirectoryHash(documentDirectory);
            change.DocumentDirectory = Path.Combine(change.Directory, "document-source");
            Directory.CreateDirectory(change.DocumentDirectory);
            LibraryCopyDirectory(documentDirectory, change.DocumentDirectory);
            if (LibraryDirectoryHash(change.DocumentDirectory) != documentsHash || LibraryDirectoryHash(documentDirectory) != documentsHash)
                throw new BridgeException("PackageChanged", "Native authoring documents changed while capturing their immutable managed snapshot.");
            change.Preview["documentsHash"] = documentsHash;
            change.Preview["documentSource"] = new JObject { ["originalDirectory"] = documentDirectory, ["snapshotDirectory"] = change.DocumentDirectory, ["sha256"] = documentsHash, ["hashScope"] = "Complete sorted companion-file paths and SHA-256 digests", ["ownedSnapshot"] = true };
        }

        // A test instance cannot be replaced by source generation (TIA refuses), so content changes
        // create the next version from documents: Siemens' documented project-library route.
        private void PrepareLibraryVersionDocuments(LibraryChange change)
        {
            var version = change.Versions[0];
            if (version.TypeObject.Versions.Any(x => x.State == LibraryTypeVersionState.InWork))
                throw new BridgeException("InvalidChangeState", "The type already has an InWork version. Release or discard it before creating a version from documents.");
            LibraryPlcScope(change, out _);
            string composition = change.Payload.Value<string>("destinationComposition");
            if (composition != "blocks" && composition != "types") throw new BridgeException("InvalidRequest", "Document versions require destinationComposition=blocks or types and an exact selected PLC/software-unit scope.");
            if (change.Payload["documentNames"] != null) throw new BridgeException("InvalidRequest", "A document version takes exactly one documentName.");
            PrepareLibraryDocumentSnapshot(change);
            change.Preview["versionFromDocuments"] = new JObject { ["typeGuid"] = version.TypeObject.Guid.ToString("D"), ["baseVersionGuid"] = version.Guid.ToString("D"),
                ["createOptions"] = "None", ["scope"] = "Creates one new version of the selected type from the snapshot; an existing InWork version is never overridden." };
        }

        private LibraryTypeVersion ApplyLibraryVersionDocuments(LibraryChange change, JObject result)
        {
            VerifyLibraryDocumentSnapshot(change);
            var scope = LibraryPlcScope(change, out _);
            var target = change.Payload.Value<string>("destinationComposition") == "blocks" ? (IEngineeringObject)scope.Blocks : scope.Types;
            var type = change.Versions[0].TypeObject;
            var transfer = type.Versions.CreateFromDocuments(new DirectoryInfo(change.DocumentDirectory), change.Payload.Value<string>("documentName"), target, CreateOptions.None, LibraryImportOptions.None);
            var created = transfer.CreatedVersion ?? throw new BridgeException("PreservationMismatch", "Native document version creation returned no version.");
            if (created.TypeObject.Guid != type.Guid) throw new BridgeException("PreservationMismatch", "Native document version belongs to a different type.");
            result["transferState"] = transfer.TransferResultState.ToString();
            result["transferMessages"] = new JArray(transfer.Messages.Select(x => x.Message));
            return created;
        }
    }
}
