using DshDesktop.Domain;

namespace DshDesktop.Application;

public sealed class ShellViewModel : ViewModelBase
{
    private int _windowX;
    private int _windowY;
    private int _windowWidth;
    private int _windowHeight;
    private bool _windowMaximized;

    public int WindowX { get => _windowX; set => Set(ref _windowX, value); }
    public int WindowY { get => _windowY; set => Set(ref _windowY, value); }
    public int WindowWidth { get => _windowWidth; set => Set(ref _windowWidth, value); }
    public int WindowHeight { get => _windowHeight; set => Set(ref _windowHeight, value); }
    public bool WindowMaximized { get => _windowMaximized; set => Set(ref _windowMaximized, value); }

    public void RestoreFrom(AppConfig cfg)
    {
        _windowX = cfg.WindowX;
        _windowY = cfg.WindowY;
        _windowWidth = cfg.WindowWidth;
        _windowHeight = cfg.WindowHeight;
        _windowMaximized = cfg.WindowMaximized;
        Raise(nameof(WindowX));
        Raise(nameof(WindowY));
        Raise(nameof(WindowWidth));
        Raise(nameof(WindowHeight));
        Raise(nameof(WindowMaximized));
    }

    public AppConfig ApplyTo(AppConfig cfg) => cfg with
    {
        WindowX = _windowX,
        WindowY = _windowY,
        WindowWidth = _windowWidth,
        WindowHeight = _windowHeight,
        WindowMaximized = _windowMaximized,
    };
}
