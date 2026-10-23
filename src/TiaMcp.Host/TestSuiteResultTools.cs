using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    [McpServerTool(Name = "testsuite_result_import", Destructive = false, OpenWorld = false)]
    [Description("Inspect one committed testsuite-supplied-results.v1 JSON upload against a saved reviewId. Packet contains exact plan, results and scope observations; maximum 64 MiB. Retains original bytes, derived result/scope/findings report and JUnit. Only identical saved-review source identity and exact selectors resolve findings; cross-observation path candidates remain unresolved. Supplied coverage is not authenticated native execution, current scope/license, whole-project health or qualification. Transfer complete artifacts with artifact_read. No Siemens worker starts.")]
    public CallToolResult TestSuiteResultImport(string reviewId, string uploadRef)
    { try { return ReviewResult(new ProjectReviewStore(options.ArtifactsDirectory).ImportTestSuiteResults(reviewId, uploadRef)); } catch (Exception e) when (ReviewError(e)) { return ReviewFailure(e); } }

    [McpServerTool(Name = "testsuite_result_review", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen a supplied-result import receipt by its exact relativePath and sha256. Rechecks original upload bytes, saved review and every derived report/JUnit before returning summary or a results/scope/findings/scopeBindings page. count 1..200, response <=128 KiB; oversized rows have explicit download markers. Native execution, freshness and qualification remain unverified. Complete originals and reports are available through artifact_read.")]
    public CallToolResult TestSuiteResultReview(string relativePath, string sha256, string section = "summary", int offset = 0, int count = 50)
    { try { return ReviewResult(new ProjectReviewStore(options.ArtifactsDirectory).TestSuiteResultsPage(relativePath, sha256, section, offset, count)); } catch (Exception e) when (ReviewError(e)) { return ReviewFailure(e); } }
}
