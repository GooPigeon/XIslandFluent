using System.IO;
using System.Windows;
using Microsoft.Win32;
using XIslandFluent.Models;

namespace XIslandFluent;

public partial class ComposerWindow : Window
{
    public ForumInfo? SelectedForum => ForumPicker.SelectedItem as ForumInfo;
    public string PostTitle => TitleBox.Text.Trim();
    public string PostContent => ContentBox.Text.Trim();
    public string? ImagePath { get; private set; }
    public bool AddWatermark => WatermarkCheck.IsChecked == true;

    public ComposerWindow(IReadOnlyList<ForumInfo> forums, ForumInfo? preferred, bool watermark)
    {
        InitializeComponent();
        ForumPicker.ItemsSource = forums;
        ForumPicker.SelectedItem = preferred is null ? forums.FirstOrDefault() : forums.FirstOrDefault(x => x.Id == preferred.Id) ?? forums.FirstOrDefault();
        WatermarkCheck.IsChecked = watermark;
    }

    private void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "选择图片", Filter = "图片|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp|所有文件|*.*" };
        if (picker.ShowDialog(this) == true)
        {
            ImagePath = picker.FileName;
            ImageNameText.Text = Path.GetFileName(ImagePath);
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedForum is null) { MessageBox.Show(this, "请选择板块。", "发表新串"); return; }
        if (string.IsNullOrWhiteSpace(PostContent) && string.IsNullOrWhiteSpace(ImagePath)) { MessageBox.Show(this, "内容和图片不能同时为空。", "发表新串"); return; }
        DialogResult = true;
    }
}
