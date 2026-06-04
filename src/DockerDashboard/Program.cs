using Velopack;

namespace DockerDashboard;

public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack 必須最先執行：處理安裝/更新/解除安裝 hooks
        VelopackApp.Build().Run();

        // 將 working directory 切離安裝目錄（current\），
        // 避免子 process 繼承 current\ 為 working dir，導致關閉後資料夾被 lock
        Environment.CurrentDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
