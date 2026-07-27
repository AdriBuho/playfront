using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;

namespace Playfront.App.Video;

/// <summary>
/// Base for a control that hands Avalonia a GPU texture directly, without ever copying it through
/// system memory. Adapted from Avalonia's official sample
/// (samples/GpuInterop/DrawingSurfaceDemoBase.cs). It only handles the compositor plumbing — creating
/// the surface and keeping its size in sync with the control; the derived class decides which texture
/// to hand over and when.
/// </summary>
public abstract class GpuCompositionControlBase : Control
{
    private CompositionSurfaceVisual? _visual;
    private Compositor? _compositor;
    private readonly Action _update;
    private bool _updateQueued;
    private bool _initialized;

    protected CompositionDrawingSurface? Surface { get; private set; }

    protected GpuCompositionControlBase()
    {
        _update = UpdateFrame;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Initialize();
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        if (_initialized)
        {
            // Order matters: stop the GPU/video work first (FreeGraphicsResources sets _stop and
            // waits for the pump thread to exit), and only THEN release the drawing surface. The
            // other way round, the pump thread can still be using the surface while it is being
            // freed — exceptions, and at worst a deadlock on shutdown.
            FreeGraphicsResources();
            Surface?.Dispose();
        }

        _initialized = false;
        base.OnDetachedFromLogicalTree(e);
    }

    private async void Initialize()
    {
        try
        {
            var selfVisual = ElementComposition.GetElementVisual(this)!;
            _compositor = selfVisual.Compositor;

            Surface = _compositor.CreateDrawingSurface();
            _visual = _compositor.CreateSurfaceVisual();
            _visual.Size = new(Bounds.Width, Bounds.Height);
            _visual.Surface = Surface;
            ElementComposition.SetElementChildVisual(this, _visual);
            var (success, error) = await DoInitialize(_compositor, Surface);
            _initialized = success;
            if (!success)
                Failed?.Invoke(error);
        }
        catch (Exception e)
        {
            Failed?.Invoke(e.ToString());
        }
    }

    /// <summary>Raised when initialisation fails; normally only of interest when debugging.</summary>
    public event Action<string>? Failed;

    private void UpdateFrame()
    {
        _updateQueued = false;
        if (this.GetPresentationSource() == null)
            return;
        _visual!.Size = new(Bounds.Width, Bounds.Height);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == BoundsProperty && _initialized && !_updateQueued && _compositor != null)
        {
            _updateQueued = true;
            _compositor.RequestCompositionUpdate(_update);
        }
        base.OnPropertyChanged(change);
    }

    private async Task<(bool success, string error)> DoInitialize(Compositor compositor,
        CompositionDrawingSurface compositionDrawingSurface)
    {
        var interop = await compositor.TryGetCompositionGpuInterop();
        if (interop == null)
            return (false, "The current graphics backend does not support GPU interop with Avalonia");
        return InitializeGraphicsResources(compositor, compositionDrawingSurface, interop);
    }

    protected abstract (bool success, string error) InitializeGraphicsResources(Compositor compositor,
        CompositionDrawingSurface compositionDrawingSurface, ICompositionGpuInterop gpuInterop);

    protected abstract void FreeGraphicsResources();
}
