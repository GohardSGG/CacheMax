using System.Windows;

namespace CacheMax
{
    public partial class RemoveAccelerationDialog : Window
    {
        public bool DeleteCacheFiles { get; private set; }

        public RemoveAccelerationDialog(string message, int itemCount)
        {
            InitializeComponent();
            MessageTextBlock.Text = message;

            // 默认不删除缓存文件（取消勾选）
            DeleteCacheCheckBox.IsChecked = false;

            // 如果只有一个项目，显示更友好的提示
            if (itemCount == 1)
            {
                DeleteCacheCheckBox.Content = "同时删除缓存文件（完全清理，无法恢复）";
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DeleteCacheFiles = DeleteCacheCheckBox.IsChecked == true;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
