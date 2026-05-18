using System.Windows;
using SyncWaveAudio.ViewModels;

namespace SyncWaveAudio.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.HostInstance.Services.GetService(typeof(MainViewModel));
        Loaded += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                await vm.InitializeCommand.ExecuteAsync(null);
            }
        };
    }
}
