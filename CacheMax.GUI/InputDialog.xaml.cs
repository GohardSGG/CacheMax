using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace CacheMax.GUI
{
    public partial class InputDialog : Window, INotifyPropertyChanged
    {
        private string _windowTitle = string.Empty;
        private string _prompt = string.Empty;
        private string _inputText = string.Empty;

        public InputDialog(string windowTitle, string prompt, string defaultValue = "")
        {
            InitializeComponent();
            DataContext = this;

            WindowTitle = windowTitle;
            Prompt = prompt;
            InputText = defaultValue;

            InputTextBox.Focus();
            InputTextBox.SelectAll();
        }

        public string WindowTitle
        {
            get => _windowTitle;
            set { _windowTitle = value; OnPropertyChanged(); }
        }

        public string Prompt
        {
            get => _prompt;
            set { _prompt = value; OnPropertyChanged(); }
        }

        public string InputText
        {
            get => _inputText;
            set { _inputText = value; OnPropertyChanged(); }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}