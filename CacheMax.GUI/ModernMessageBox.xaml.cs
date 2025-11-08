using System.Windows;
using System.Windows.Controls;

namespace CacheMax.GUI
{
    public partial class ModernMessageBox : Window
    {
        public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

        private ModernMessageBox()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 显示简洁风格消息框
        /// </summary>
        public static MessageBoxResult Show(string message, string title = "提示",
            MessageBoxButton button = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.Information)
        {
            var dialog = new ModernMessageBox
            {
                Title = title,
                Owner = Application.Current.MainWindow
            };

            // 设置标题和消息
            dialog.TitleTextBlock.Text = title;
            dialog.MessageTextBlock.Text = message;

            // 设置按钮
            dialog.SetButtons(button);

            dialog.ShowDialog();
            return dialog.Result;
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
                    AddButton("确定", MessageBoxResult.OK, true);
                    AddButton("取消", MessageBoxResult.Cancel, false);
                    break;

                case MessageBoxButton.YesNo:
                    AddButton("是", MessageBoxResult.Yes, true);
                    AddButton("否", MessageBoxResult.No, false);
                    break;

                case MessageBoxButton.YesNoCancel:
                    AddButton("是", MessageBoxResult.Yes, true);
                    AddButton("否", MessageBoxResult.No, false);
                    AddButton("取消", MessageBoxResult.Cancel, false);
                    break;
            }
        }

        private void AddButton(string content, MessageBoxResult result, bool isPrimary)
        {
            var button = new Button
            {
                Content = content,
                Width = 80,
                Height = 30,
                Margin = new Thickness(0, 0, 10, 0)
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
