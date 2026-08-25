using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace BlfToCsv;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "選擇包含 BLF 檔案的資料夾",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (Directory.Exists(FolderPathTextBox.Text))
            dialog.InitialDirectory = FolderPathTextBox.Text;

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
            return;

        FolderPathTextBox.Text = dialog.SelectedPath;
        int count = Directory.EnumerateFiles(dialog.SelectedPath, "*.blf", SearchOption.TopDirectoryOnly).Count();
        StatusTextBlock.Text = count == 0 ? "此目錄沒有 BLF 檔案。" : $"找到 {count} 個 BLF 檔案。";
        ConvertButton.IsEnabled = count > 0;
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        string folder = FolderPathTextBox.Text;
        string[] files = Directory.GetFiles(folder, "*.blf", SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
        {
            MessageBox.Show("指定目錄中沒有 BLF 檔案。", "無檔案", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BrowseButton.IsEnabled = false;
        ConvertButton.IsEnabled = false;
        int completed = 0;
        int totalRows = 0;

        try
        {
            foreach (string file in files)
            {
                StatusTextBlock.Text = $"正在轉換 {Path.GetFileName(file)}（{completed + 1}/{files.Length}）";
                int rows = await Task.Run(() => BlfConverter.ConvertFile(file));
                totalRows += rows;
                completed++;
            }

            StatusTextBlock.Text = $"轉換完成：{completed} 個檔案，共 {totalRows:N0} 筆資料。";
            MessageBox.Show(
                $"轉換完成！\n\n檔案數：{completed}\n資料筆數：{totalRows:N0}\n輸出位置：{folder}",
                "轉換完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"轉換失敗：{ex.Message}";
            MessageBox.Show($"轉換失敗：\n{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BrowseButton.IsEnabled = true;
            ConvertButton.IsEnabled = true;
        }
    }
}
