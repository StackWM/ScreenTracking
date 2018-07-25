namespace LostTech.Stack.ScreenTracking
{
    using System.ComponentModel;
    using LostTech.Windows;

    public interface IScreenLayoutViewModel: INotifyPropertyChanged
    {
        Win32Screen Screen { get; set; }
    }
}
