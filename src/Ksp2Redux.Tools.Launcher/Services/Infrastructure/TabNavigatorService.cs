namespace Ksp2Redux.Tools.Launcher.Services.Infrastructure;

public interface ITabNavigatorService
{
    public struct CurrentTabChangedEventArgs
    {
        public int CurrentTab;
    }
    
    event EventHandler<CurrentTabChangedEventArgs> CurrentTabChanged;
    void GoToHome();
}

public class TabNavigatorService : ITabNavigatorService
{
    public event EventHandler<ITabNavigatorService.CurrentTabChangedEventArgs>? CurrentTabChanged;

    public void GoToHome()
    {
        CurrentTabChanged?.Invoke(this, new() { CurrentTab = 0 });
    }
}