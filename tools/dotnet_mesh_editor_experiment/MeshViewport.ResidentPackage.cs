namespace Cdmw.MeshEditorExperiment;

internal sealed partial class MeshViewport
{
    public long ResidentSceneLoadCount => _d3d11Viewport?.ResidentSceneLoadCount ?? 0;

    public void ReplaceResidentPackage(
        ObjDocument document,
        NetMaterialSet materials,
        NetTextureSet textureSet,
        NetSceneState scene)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(textureSet);
        ArgumentNullException.ThrowIfNull(scene);
        if (InvokeRequired)
        {
            throw new InvalidOperationException("Resident package replacement must run on the viewport owner thread.");
        }
        var renderer = _d3d11Viewport
            ?? throw new InvalidOperationException("The production D3D11 renderer is not available for resident package replacement.");
        var preserveArchiveCamera = !string.IsNullOrWhiteSpace(_scene.ArchivePreviewSourcePath)
            && string.Equals(
                _scene.ArchivePreviewSourcePath,
                scene.ArchivePreviewSourcePath,
                StringComparison.OrdinalIgnoreCase);
        var previousCamera = (_yaw, _pitch, _zoom, _panX, _panY);

        renderer.ReplaceResidentScene(document, materials, textureSet, scene);
        _document = document;
        _materials = materials;
        _textureSet = textureSet;
        _scene = scene;
        _selectedVertices.Clear();
        _selectedFaces.Clear();
        _selectedEdges.Clear();
        _selectedSources.Clear();
        _acknowledgedSelection = new SelectionAuthoritySnapshot(
            new Dictionary<int, HashSet<int>>(),
            new Dictionary<int, HashSet<int>>(),
            new HashSet<int>(),
            new HashSet<int>(),
            0,
            0);
        _provisionalSelectionRequestId = 0;
        _provisionalSelectionBaseRevision = 0;
        _hoverEdgeId = -1;
        _edgeTopology = NetEdgeTopology.Empty;
        _partAdjacency.Clear();
        _presentationContexts.Clear();
        _activeCameraContextId = "editable";
        _presentationGridVisible = scene.GridVisible;
        _presentationGizmoVisible = scene.GizmoVisible;
        _presentationStateFingerprint = string.Empty;
        FrameMesh();
        if (preserveArchiveCamera)
        {
            (_yaw, _pitch, _zoom, _panX, _panY) = previousCamera;
        }
        else
        {
            ApplyArchivePreviewInitialCamera();
        }
        InitializePresentationContexts();
        if (_options.SimplePreview)
        {
            _ = TrySetSynchronizedDisplayMode(
                scene.ArchivePreviewTexturesEnabled ? "textured" : "untextured_wire",
                out _);
        }
        ApplySceneState();
    }

    private void ApplyArchivePreviewInitialCamera()
    {
        if (!_scene.HasArchivePreviewCamera)
        {
            return;
        }
        _yaw = _scene.ArchivePreviewYawDegrees * MathF.PI / 180.0f;
        _pitch = Math.Clamp(_scene.ArchivePreviewPitchDegrees, -89.0f, 89.0f) * MathF.PI / 180.0f;
        _panX = 0.0f;
        _panY = 0.0f;
        if (_scene.ArchivePreviewFitToView)
        {
            _zoom = FitZoomForBounds(SceneBoundsForContext(_activeCameraContextId));
        }
        UpdateGpuViewport();
    }
}
