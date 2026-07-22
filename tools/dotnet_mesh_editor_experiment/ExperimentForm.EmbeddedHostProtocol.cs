using System.Text.Json;

namespace Cdmw.MeshEditorExperiment;

internal sealed partial class ExperimentForm
{
    private void HandleHostAttachRequest(JsonElement root)
    {
        var requestId = JsonLongValue(root, "request_id");
        var parentHwnd = JsonLongValue(root, "parent_hwnd");
        var activate = !root.TryGetProperty("activate", out var activateValue)
            || activateValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || activateValue.GetBoolean();
        if (!_options.Embedded || requestId <= 0 || parentHwnd <= 0)
        {
            PublishHostAttachFailure(requestId, parentHwnd, "Embedded host attachment request is incomplete.");
            return;
        }

        if (!NativeWindowHost.Embed(this, new IntPtr(parentHwnd), activate))
        {
            PublishHostAttachFailure(requestId, parentHwnd, "The requested embedded host window is unavailable.");
            return;
        }

        _embeddedParentHwnd = parentHwnd;
        _pendingEmbeddedParentSize = Size.Empty;
        _pendingEmbeddedParentSizeTimestamp = 0L;
        WriteProtocolEvent("host_attach_applied", new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["parent_hwnd"] = parentHwnd,
            ["process_id"] = Environment.ProcessId,
        });
    }

    private void PublishHostAttachFailure(long requestId, long parentHwnd, string message)
    {
        WriteProtocolEvent("host_attach_failed", new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["parent_hwnd"] = parentHwnd,
            ["process_id"] = Environment.ProcessId,
            ["message"] = message,
        });
    }
}
