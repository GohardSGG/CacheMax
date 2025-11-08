using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CacheMax.GUI
{
    public partial class ModernMessageBox : Window
    {
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public string Detail { get; set; } = "";
        public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

        private ModernMessageBox()
        {
            InitializeComponent();
            DataContext = this;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.Cancel;
            Close();
        }

        /// <summary>
        /// 显示现代化消息框
        /// </summary>
        public static MessageBoxResult Show(string message, string title = "提示",
            MessageBoxButton button = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.Information,
            string detail = "")
        {
            var dialog = new ModernMessageBox
            {
                Title = title,
                Message = message,
                Detail = detail,
                Owner = Application.Current.MainWindow
            };

            // 设置图标
            dialog.SetIcon(icon);

            // 设置按钮
            dialog.SetButtons(button);

            // 显示详细信息（如果有）
            if (!string.IsNullOrEmpty(detail))
            {
                dialog.DetailText.Visibility = Visibility.Visible;
            }

            dialog.ShowDialog();
            return dialog.Result;
        }

        private void SetIcon(MessageBoxImage icon)
        {
            switch (icon)
            {
                case MessageBoxImage.Information:
                    IconBrush.Color = Color.FromRgb(0, 120, 212); // 蓝色
                    IconText.Text = "i";
                    break;
                case MessageBoxImage.Warning:
                    IconBrush.Color = Color.FromRgb(255, 185, 0); // 橙色
                    IconText.Text = "!";
                    break;
                case MessageBoxImage.Error:
                    IconBrush.Color = Color.FromRgb(232, 17, 35); // 红色
                    IconText.Text = "✕";
                    break;
                case MessageBoxImage.Question:
                    IconBrush.Color = Color.FromRgb(0, 120, 212); // 蓝色
                    IconText.Text = "?";
                    break;
                default:
                    IconBrush.Color = Color.FromRgb(0, 120, 212);
                    IconText.Text = "i";
                    break;
            }
        }

        private void SetButtons(MessageBoxButton button)
        {
            ButtonPanel.Children.Clear();

            switch (button)
            {
                case MessageBoxButton.OK:
                    AddButton("确定", MessageBoxResult.OK, true);
                    break;

                case MessageBoxButton.OKCancel:
                    AddButton("取消", MessageBoxResult.Cancel, false);
                    AddButton("确定", MessageBoxResult.OK, true);
                    break;

                case MessageBoxButton.YesNo:
                    AddButton("否", MessageBoxResult.No, false);
                    AddButton("是", MessageBoxResult.Yes, true);
                    break;

                case MessageBoxButton.YesNoCancel:
                    AddButton("取消", MessageBoxResult.Cancel, false);
                    AddButton("否", MessageBoxResult.No, false);
                    AddButton("是", MessageBoxResult.Yes, true);
                    break;
            }
        }

        private void AddButton(string content, MessageBoxResult result, bool isPrimary)
        {
            var button = new Button
            {
                Content = content,
                MinWidth = 80,
                Margin = new Thickness(10, 0, 0, 0),
                Style = isPrimary ?
                    (Style)FindResource("PrimaryButton") :
                    (Style)FindResource("ModernButton")
            };

            button.Click += (s, e) =>
            {
                Result = result;
                Close();
            };

            // 设置默认按钮
            if (isPrimary)
            {
                button.IsDefault = true;
            }

            // 如果是取消按钮，设置为Cancel按钮
            if (result == MessageBoxResult.Cancel || result == MessageBoxResult.No)
            {
                button.IsCancel = true;
            }

            ButtonPanel.Children.Add(button);
        }
    }
}
