using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;

namespace Playfront.App.Video;

/// <summary>
/// Base para un control que le entrega a Avalonia una textura de la GPU directamente
/// (sin copiarla nunca a memoria normal). Adaptado del sample oficial de Avalonia
/// (samples/GpuInterop/DrawingSurfaceDemoBase.cs) - se encarga solo del "enganche" con el
/// compositor (crear la superficie, mantener su tamaño sincronizado con el control); la clase
/// derivada decide qué textura entregarle y cuándo.
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
            // Orden IMPORTANTE: primero parar el trabajo de la GPU/hilo de video
            // (FreeGraphicsResources pone _stop y espera a que el hilo de bombeo salga), y SOLO
            // DESPUES soltar la superficie de dibujo. Al reves, el hilo de bombeo puede seguir
            // usando la superficie mientras esta ya se libera -> excepciones y, en el peor caso,
            // se realimenta el bloqueo mutuo del apagado.
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

    /// <summary>Se dispara si la inicializacion falla - de normal solo interesa para depurar.</summary>
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
            return (false, "El backend grafico actual no soporta interop de GPU con Avalonia");
        return InitializeGraphicsResources(compositor, compositionDrawingSurface, interop);
    }

    protected abstract (bool success, string error) InitializeGraphicsResources(Compositor compositor,
        CompositionDrawingSurface compositionDrawingSurface, ICompositionGpuInterop gpuInterop);

    protected abstract void FreeGraphicsResources();
}
